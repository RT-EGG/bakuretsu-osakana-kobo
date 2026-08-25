using BakuretsuOsakanaKobo.Update;

namespace BakuretsuOsakanaKobo.Updater;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 1)
        {
            return 2;
        }

        var result = await new UpdateHelperHost(failureNotifier: new WindowsUpdateFailureNotifier())
            .RunAsync(args[0])
            .ConfigureAwait(false);
        return result.Status == UpdateHelperStatus.Succeeded ? 0 : 1;
    }
}
