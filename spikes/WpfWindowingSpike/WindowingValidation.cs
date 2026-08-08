using System.Text.Json.Serialization;

namespace BakuretsuOsakanaKobo.Spikes.WpfWindowing;

internal sealed class WindowingValidationReport
{
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public bool Passed { get; set; }
    public string PlaybackBackendType { get; set; } = "";
    public List<MonitorSnapshot> Monitors { get; set; } = [];
    public WindowSnapshot? InitialWindow { get; set; }
    public WindowSnapshot? FullScreenWindow { get; set; }
    public WindowSnapshot? RestoredWindow { get; set; }
    public WindowSnapshot? MovedWindow { get; set; }
    public List<WindowSnapshot> MonitorVisits { get; set; } = [];
    public bool PlaylistVisible { get; set; }
    public bool PlaylistHasDistinctHandle { get; set; }
    public bool PlaylistOwnerIsMainWindow { get; set; }
    public bool SeekPopupOpened { get; set; }
    public bool FullScreenEntered { get; set; }
    public bool FullScreenUsedCurrentMonitor { get; set; }
    public bool FullScreenRestored { get; set; }
    public bool MovedToAnotherMonitor { get; set; }
    public bool DpiChangedAcrossMonitors { get; set; }
    public bool ForegroundActivationSucceeded { get; set; }
    public string MultiMonitorOutcome { get; set; } = "";
    public List<string> Errors { get; } = [];

    [JsonIgnore]
    public bool HasErrors => Errors.Count != 0;
}
