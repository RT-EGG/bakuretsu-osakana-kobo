using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BakuretsuOsakanaKobo.Spikes.SingleInstanceData;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _serverTask;
    private bool _ownsMutex;

    internal SingleInstanceCoordinator(string instanceId)
    {
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instanceId)))[..24];
        _pipeName = $"BakuretsuOsakanaKobo.{suffix}";
        _mutex = new Mutex(true, $"Local\\{_pipeName}", out var createdNew);
        IsPrimary = createdNew;
        _ownsMutex = createdNew;
    }

    internal bool IsPrimary { get; }
    internal event Func<LaunchRequest, Task>? RequestReceived;
    internal event EventHandler<string>? Diagnostic;

    internal void StartListening()
    {
        if (!IsPrimary || _serverTask is not null)
        {
            return;
        }

        _serverTask = RunServerAsync(_cancellation.Token);
        Diagnostic?.Invoke(this, "server-started");
    }

    internal async Task<bool> SendAsync(LaunchRequest request)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var client = new NamedPipeClientStream(
                    ".",
                    _pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                await client.ConnectAsync(500);
                Diagnostic?.Invoke(this, "client-connected");
                var payload = JsonSerializer.SerializeToUtf8Bytes(request);
                await client.WriteAsync(BitConverter.GetBytes(payload.Length));
                await client.WriteAsync(payload);
                await client.FlushAsync();
                Diagnostic?.Invoke(this, "client-request-written");
                var response = new byte[1];
                await client.ReadExactlyAsync(response).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
                Diagnostic?.Invoke(this, $"client-response:{response[0]}");
                return response[0] == 1;
            }
            catch (TimeoutException)
            {
                await Task.Delay(50);
            }
            catch (IOException)
            {
                await Task.Delay(50);
            }
        }

        return false;
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        try
        {
            _serverTask?.Wait(TimeSpan.FromSeconds(1));
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
                await server.WaitForConnectionAsync(cancellationToken);
                Diagnostic?.Invoke(this, "server-client-connected");
                var lengthBytes = new byte[sizeof(int)];
                await server.ReadExactlyAsync(lengthBytes, cancellationToken);
                var payloadLength = BitConverter.ToInt32(lengthBytes);
                if (payloadLength is <= 0 or > 1_048_576)
                {
                    throw new InvalidDataException($"Invalid IPC payload length: {payloadLength}.");
                }

                var payload = new byte[payloadLength];
                await server.ReadExactlyAsync(payload, cancellationToken);
                Diagnostic?.Invoke(this, $"server-payload-read:{payloadLength}");
                var request = JsonSerializer.Deserialize<LaunchRequest>(payload);
                if (request?.FileArguments is null)
                {
                    throw new InvalidDataException("IPC request did not contain file arguments.");
                }

                if (RequestReceived is { } handler)
                {
                    await handler(request);
                    Diagnostic?.Invoke(this, "server-request-handled");
                }

                await server.WriteAsync(new byte[] { 1 }, cancellationToken);
                await server.FlushAsync(cancellationToken);
                Diagnostic?.Invoke(this, "server-response-written");
            }
            catch (Exception exception) when (
                exception is IOException or InvalidDataException or JsonException)
            {
                Diagnostic?.Invoke(this, $"server-request-rejected:{exception.GetType().Name}");
                if (server.IsConnected)
                {
                    try
                    {
                        await server.WriteAsync(new byte[] { 0 }, cancellationToken);
                        await server.FlushAsync(cancellationToken);
                    }
                    catch (IOException)
                    {
                    }
                }
            }
        }
    }
}

internal sealed class LaunchRequest
{
    public int SenderProcessId { get; set; }
    public string[] FileArguments { get; set; } = [];
    public bool IsInitialLaunch { get; set; }
}
