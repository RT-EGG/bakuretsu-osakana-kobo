using System.Text.Json;
using BakuretsuOsakanaKobo.Update;
using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class UpdateStartupResultReaderTests
{
    [Fact]
    public void ExtractArguments_WithInternalResultSwitch_RemovesItFromLaunchRequest()
    {
        var result = UpdateStartupResultReader.ExtractArguments(
            ["--update-result", @"C:\temp\update-result.json"]);

        Assert.Empty(result.LaunchArguments);
        Assert.Equal(@"C:\temp\update-result.json", result.ResultPath);
    }

    [Fact]
    public async Task ReadAsync_WithOwnedValidResult_ReturnsResultAndCleansWorkingDirectory()
    {
        using var fixture = new ResultFixture();
        var resultPath = fixture.WriteResult(UpdateHelperStatus.Succeeded);

        var read = await UpdateStartupResultReader.ReadAsync(resultPath, fixture.WorkingRoot);

        Assert.Equal(UpdateHelperStatus.Succeeded, read.Result?.Status);
        await WaitUntilAsync(() => !Directory.Exists(fixture.WorkingDirectory));
    }

    [Fact]
    public async Task ReadAsync_WithOutsidePath_DoesNotReadOrDeleteOutsideDirectory()
    {
        using var fixture = new ResultFixture();
        var outsideDirectory = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outsideDirectory);
        var outsidePath = Path.Combine(outsideDirectory, UpdateHelperHost.ResultFileName);
        await File.WriteAllTextAsync(outsidePath, "{}");

        var read = await UpdateStartupResultReader.ReadAsync(outsidePath, fixture.WorkingRoot);

        Assert.Null(read.Result);
        Assert.NotNull(read.TechnicalMessage);
        await Task.Delay(250);
        Assert.True(File.Exists(outsidePath));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 20 && !condition(); attempt++)
        {
            await Task.Delay(50);
        }

        Assert.True(condition());
    }

    private sealed class ResultFixture : IDisposable
    {
        internal ResultFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "BakuretsuOsakanaKobo.Update.Result.Tests",
                Guid.NewGuid().ToString("N"));
            WorkingRoot = Path.Combine(Root, "working");
            WorkingDirectory = Path.Combine(WorkingRoot, $"update-{Guid.NewGuid():N}");
            Directory.CreateDirectory(WorkingDirectory);
        }

        internal string Root { get; }

        internal string WorkingRoot { get; }

        internal string WorkingDirectory { get; }

        internal string WriteResult(UpdateHelperStatus status)
        {
            var path = Path.Combine(WorkingDirectory, UpdateHelperHost.ResultFileName);
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(
                    new UpdateHelperResult(1, status, RestartAttempted: true),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
