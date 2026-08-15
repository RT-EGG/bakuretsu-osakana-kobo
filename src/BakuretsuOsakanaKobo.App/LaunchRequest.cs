namespace BakuretsuOsakanaKobo;

internal sealed class LaunchRequest
{
    public int SenderProcessId { get; set; }

    public string[] FileArguments { get; set; } = [];

    public bool IsInitialLaunch { get; set; }
}
