using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace BakuretsuOsakanaKobo;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    internal const int MaximumPayloadBytes = 64 * 1024;
    internal static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    private const int ConnectAttemptMilliseconds = 500;
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _serverTask;
    private bool _ownsMutex;
    private bool _disposed;

    internal SingleInstanceCoordinator()
        : this(CreateProductInstanceName())
    {
    }

    internal SingleInstanceCoordinator(string instanceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        _pipeName = instanceName;
        _mutex = new Mutex(true, $"Local\\{instanceName}", out var createdNew);
        IsPrimary = createdNew;
        _ownsMutex = createdNew;
    }

    internal bool IsPrimary { get; }

    internal event Func<LaunchRequest, Task>? RequestReceived;

    internal event EventHandler<SingleInstanceDiagnosticEventArgs>? Diagnostic;

    internal void StartListening()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsPrimary || _serverTask is not null)
        {
            return;
        }

        _serverTask = RunServerAsync(_cancellation.Token);
        _ = _serverTask.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        ReportDiagnostic("server-started");
    }

    internal async Task<bool> SendAsync(LaunchRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        var payload = JsonSerializer.SerializeToUtf8Bytes(request);
        if (payload.Length is <= 0 or > MaximumPayloadBytes)
        {
            ReportDiagnostic("client-payload-rejected", $"Payload length was {payload.Length} bytes.");
            return false;
        }

        using var deadlineCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadlineCancellation.CancelAfter(SendTimeout);
        while (!deadlineCancellation.IsCancellationRequested)
        {
            try
            {
                await using var client = new NamedPipeClientStream(
                    ".",
                    _pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                await client.ConnectAsync(ConnectAttemptMilliseconds, deadlineCancellation.Token);
                await client.WriteAsync(BitConverter.GetBytes(payload.Length), deadlineCancellation.Token);
                await client.WriteAsync(payload, deadlineCancellation.Token);
                await client.FlushAsync(deadlineCancellation.Token);

                var response = new byte[1];
                await client.ReadExactlyAsync(response, deadlineCancellation.Token)
                    .AsTask()
                    .WaitAsync(ResponseTimeout, deadlineCancellation.Token);
                ReportDiagnostic("client-response-received", $"ACK was {response[0]}.");
                return response[0] == 1;
            }
            catch (Exception exception) when (
                exception is IOException or TimeoutException or OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (deadlineCancellation.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    await Task.Delay(RetryDelay, deadlineCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    deadlineCancellation.IsCancellationRequested &&
                    !cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        ReportDiagnostic("client-send-timed-out");
        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Cancel();
        try
        {
            _serverTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException exception) when (
            exception.InnerExceptions.All(inner => inner is OperationCanceledException))
        {
        }
        if (_ownsMutex)
        {
            _mutex.ReleaseMutex();
            _ownsMutex = false;
        }

        _mutex.Dispose();
        _cancellation.Dispose();
    }

    private async Task RunServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var server = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestCancellation.CancelAfter(RequestReadTimeout);
                var request = await ReadRequestAsync(server, requestCancellation.Token).ConfigureAwait(false);
                if (RequestReceived is { } handler)
                {
                    await handler(request).ConfigureAwait(false);
                }

                await WriteAcknowledgementAsync(server, accepted: true, cancellationToken).ConfigureAwait(false);
                ReportDiagnostic("server-request-handled");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException exception)
            {
                ReportDiagnostic("server-request-rejected", "The IPC request timed out.", exception);
                if (server.IsConnected)
                {
                    try
                    {
                        await WriteAcknowledgementAsync(server, accepted: false, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception acknowledgementException) when (
                        acknowledgementException is IOException or OperationCanceledException)
                    {
                    }
                }
            }
            catch (Exception exception)
            {
                ReportDiagnostic("server-request-rejected", exception.Message, exception);
                if (server.IsConnected)
                {
                    try
                    {
                        await WriteAcknowledgementAsync(server, accepted: false, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception acknowledgementException) when (
                        acknowledgementException is IOException or OperationCanceledException)
                    {
                    }
                }
            }
        }
    }

    private static async Task<LaunchRequest> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
        var payloadLength = BitConverter.ToInt32(lengthBytes);
        if (payloadLength is <= 0 or > MaximumPayloadBytes)
        {
            throw new InvalidDataException($"Invalid IPC payload length: {payloadLength}.");
        }

        var payload = new byte[payloadLength];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        var request = JsonSerializer.Deserialize<LaunchRequest>(payload);
        if (request?.FileArguments is null || request.FileArguments.Any(argument => argument is null))
        {
            throw new InvalidDataException("IPC request did not contain valid file arguments.");
        }

        return request;
    }

    private static async Task WriteAcknowledgementAsync(
        Stream stream,
        bool accepted,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(new byte[] { accepted ? (byte)1 : (byte)0 }, cancellationToken)
            .ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ReportDiagnostic(string eventCode, string? message = null, Exception? exception = null) =>
        Diagnostic?.Invoke(this, new SingleInstanceDiagnosticEventArgs(eventCode, message, exception));

    private static string CreateProductInstanceName()
    {
        var userSid = WindowsIdentity.GetCurrent().User?.Value ??
            throw new InvalidOperationException("The current Windows user SID is unavailable.");
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userSid)))[..24];
        return $"BakuretsuOsakanaKobo.SingleInstance.v1.{suffix}";
    }
}

internal sealed record SingleInstanceDiagnosticEventArgs(
    string EventCode,
    string? Message = null,
    Exception? Exception = null);
