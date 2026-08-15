using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class SingleInstanceCoordinatorTests
{
    [Fact]
    public void PrimaryRejectsMalformedClientsAndAcceptsValidRequestAfterward()
    {
        RunOnDedicatedThread(() =>
        {
            var instanceName = $"BakuretsuOsakanaKobo.Tests.{Guid.NewGuid():N}";
            using var primary = new SingleInstanceCoordinator(instanceName);
            Assert.True(primary.IsPrimary);

            var received = new TaskCompletionSource<LaunchRequest>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            primary.RequestReceived += request =>
            {
                received.TrySetResult(request);
                return Task.CompletedTask;
            };
            primary.StartListening();

            Assert.Equal(0, SendRawRequest(instanceName, BitConverter.GetBytes(0)));
            Assert.Equal(0, SendPayload(instanceName, "not-json"u8.ToArray()));
            Assert.Equal(
                0,
                SendPayload(
                    instanceName,
                    JsonSerializer.SerializeToUtf8Bytes(new { SenderProcessId = 1, FileArguments = (string[]?)null })));
            SendPartialRequestAndDisconnect(instanceName);
            Assert.Equal(0, SendStalledRequest(instanceName));

            using var secondary = new SingleInstanceCoordinator(instanceName);
            Assert.False(secondary.IsPrimary);
            var expectedPath = @"C:\動画 フォルダー\  日本語 動画  .mp4";
            var accepted = secondary.SendAsync(new LaunchRequest
            {
                SenderProcessId = 42,
                FileArguments = [expectedPath],
            }).GetAwaiter().GetResult();

            Assert.True(accepted);
            var request = received.Task.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            Assert.Equal(42, request.SenderProcessId);
            Assert.Equal([expectedPath], request.FileArguments);
        });
    }

    [Fact]
    public void OnlyFirstCoordinatorIsPrimaryAndZeroOrMultipleArgumentsRoundTrip()
    {
        RunOnDedicatedThread(() =>
        {
            var instanceName = $"BakuretsuOsakanaKobo.Tests.{Guid.NewGuid():N}";
            using var primary = new SingleInstanceCoordinator(instanceName);
            using var secondary = new SingleInstanceCoordinator(instanceName);
            Assert.True(primary.IsPrimary);
            Assert.False(secondary.IsPrimary);

            var requests = new List<LaunchRequest>();
            primary.RequestReceived += request =>
            {
                lock (requests)
                {
                    requests.Add(request);
                }

                return Task.CompletedTask;
            };
            primary.StartListening();

            Assert.True(secondary.SendAsync(new LaunchRequest
            {
                SenderProcessId = 1,
                FileArguments = [],
            }).GetAwaiter().GetResult());
            Assert.True(secondary.SendAsync(new LaunchRequest
            {
                SenderProcessId = 2,
                FileArguments = [@"C:\one.mp4", @"C:\two.wmv"],
            }).GetAwaiter().GetResult());

            lock (requests)
            {
                Assert.Collection(
                    requests,
                    request => Assert.Empty(request.FileArguments),
                    request => Assert.Equal([@"C:\one.mp4", @"C:\two.wmv"], request.FileArguments));
            }
        });
    }

    [Fact]
    public void OversizedClientPayloadIsRejectedBeforeConnection()
    {
        RunOnDedicatedThread(() =>
        {
            var instanceName = $"BakuretsuOsakanaKobo.Tests.{Guid.NewGuid():N}";
            using var coordinator = new SingleInstanceCoordinator(instanceName);
            var accepted = coordinator.SendAsync(new LaunchRequest
            {
                FileArguments = [new string('x', SingleInstanceCoordinator.MaximumPayloadBytes)],
            }).GetAwaiter().GetResult();

            Assert.False(accepted);
        });
    }

    [Fact]
    public void DisposeStopsListenerAndReleasesInstanceOwnership()
    {
        RunOnDedicatedThread(() =>
        {
            var instanceName = $"BakuretsuOsakanaKobo.Tests.{Guid.NewGuid():N}";
            var primary = new SingleInstanceCoordinator(instanceName);
            primary.StartListening();
            primary.Dispose();
            primary.Dispose();

            using var replacement = new SingleInstanceCoordinator(instanceName);
            Assert.True(replacement.IsPrimary);
        });
    }

    private static int SendPayload(string pipeName, byte[] payload)
    {
        var request = new byte[sizeof(int) + payload.Length];
        BitConverter.GetBytes(payload.Length).CopyTo(request, 0);
        payload.CopyTo(request, sizeof(int));
        return SendRawRequest(pipeName, request);
    }

    private static int SendRawRequest(string pipeName, byte[] request)
    {
        using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        client.Connect(2_000);
        client.Write(request);
        client.Flush();
        return client.ReadByte();
    }

    private static void SendPartialRequestAndDisconnect(string pipeName)
    {
        using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous);
        client.Connect(2_000);
        client.Write(BitConverter.GetBytes(32));
        client.Write(Encoding.UTF8.GetBytes("partial"));
        client.Flush();
    }

    private static int SendStalledRequest(string pipeName)
    {
        using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        client.Connect(2_000);
        client.Write(BitConverter.GetBytes(32));
        client.Flush();
        return client.ReadByte();
    }

    private static void RunOnDedicatedThread(Action action)
    {
        Exception? failure = null;
        using var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                completed.Set();
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();

        Assert.True(completed.Wait(TimeSpan.FromSeconds(15)), "The dedicated IPC test thread timed out.");
        if (failure is not null)
        {
            throw new AggregateException(failure);
        }
    }
}
