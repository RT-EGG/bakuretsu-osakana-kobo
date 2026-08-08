using System.IO;
using System.Text.Json;

namespace BakuretsuOsakanaKobo.Spikes.SingleInstanceData;

internal static class DataValidation
{
    internal static async Task<int> RunAsync(string reportPath, string executableDirectory)
    {
        var report = new DataValidationReport
        {
            StartedAt = DateTimeOffset.Now,
            ExecutableDirectory = Path.GetFullPath(executableDirectory),
        };
        var store = new PortableJsonStore<SpikeSettings>(report.ExecutableDirectory, "settings.json");
        report.DataDirectory = store.DataDirectory;
        report.SettingsPath = store.FilePath;

        try
        {
            var firstSave = store.TrySave(new SpikeSettings { VolumePercent = 125, Label = "初回 保存" });
            Require(firstSave.Success, $"Initial save failed: {firstSave.Error}");
            report.InitialSaveAndLoad = store.LoadOrDefault().Value.VolumePercent == 125;

            var interruptedTemporaryPath = Path.Combine(store.DataDirectory, ".settings.interrupted.tmp");
            await File.WriteAllTextAsync(interruptedTemporaryPath, "{ incomplete");
            report.InterruptedWritePreservedExisting = store.LoadOrDefault().Value.VolumePercent == 125;

            var replacement = store.TrySave(new SpikeSettings { VolumePercent = 250, Label = "置換後" });
            report.AtomicReplacement = replacement.Success &&
                store.LoadOrDefault().Value.VolumePercent == 250;
            File.Delete(interruptedTemporaryPath);

            await File.WriteAllTextAsync(store.FilePath, "{ invalid json");
            var corrupt = store.LoadOrDefault();
            report.CorruptJsonRecoveredWithDefaults = corrupt.UsedDefault &&
                corrupt.Value.VolumePercent == 100 &&
                corrupt.CorruptBackupPath is not null &&
                File.Exists(corrupt.CorruptBackupPath);
            report.CorruptBackupPath = corrupt.CorruptBackupPath;

            var beforeReadOnly = store.TrySave(new SpikeSettings { VolumePercent = 300, Label = "読取専用前" });
            Require(beforeReadOnly.Success, $"Pre-read-only save failed: {beforeReadOnly.Error}");
            File.SetAttributes(store.FilePath, File.GetAttributes(store.FilePath) | FileAttributes.ReadOnly);
            try
            {
                var failedSave = store.TrySave(new SpikeSettings { VolumePercent = 400, Label = "保存不可" });
                report.WriteFailureReported = !failedSave.Success && !string.IsNullOrWhiteSpace(failedSave.Error);
                report.WriteFailurePreservedExisting = store.LoadOrDefault().Value.VolumePercent == 300;
                report.WriteFailureMessage = failedSave.Error;
            }
            finally
            {
                File.SetAttributes(store.FilePath, FileAttributes.Normal);
            }

            report.PathIsPortableDataDirectory = string.Equals(
                store.DataDirectory,
                Path.Combine(report.ExecutableDirectory, "data"),
                StringComparison.OrdinalIgnoreCase);
            report.NoStoreTemporaryFilesRemain = !Directory.EnumerateFiles(
                    store.DataDirectory,
                    ".settings.json.*.tmp")
                .Any();

            Require(report.InitialSaveAndLoad, "Initial save/load did not round-trip.");
            Require(report.InterruptedWritePreservedExisting, "An incomplete temp file affected existing JSON.");
            Require(report.AtomicReplacement, "Atomic replacement did not persist the new value.");
            Require(report.CorruptJsonRecoveredWithDefaults, "Corrupt JSON did not recover to defaults with a backup.");
            Require(report.WriteFailureReported, "A read-only target did not report a save failure.");
            Require(report.WriteFailurePreservedExisting, "A failed save modified the existing value.");
            Require(report.PathIsPortableDataDirectory, "The store path was not executableDirectory/data.");
            Require(report.NoStoreTemporaryFilesRemain, "Store temporary files remained after validation.");
        }
        catch (Exception exception)
        {
            report.Errors.Add(exception.ToString());
        }

        report.Passed = report.Errors.Count == 0;
        report.FinishedAt = DateTimeOffset.Now;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        await File.WriteAllTextAsync(
            reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return report.Passed ? 0 : 1;

        void Require(bool condition, string message)
        {
            if (!condition)
            {
                report.Errors.Add(message);
            }
        }
    }
}

internal sealed class SpikeSettings
{
    public int SchemaVersion { get; set; } = 1;
    public int VolumePercent { get; set; } = 100;
    public string Label { get; set; } = "default";
}

internal sealed class DataValidationReport
{
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public bool Passed { get; set; }
    public string ExecutableDirectory { get; set; } = "";
    public string DataDirectory { get; set; } = "";
    public string SettingsPath { get; set; } = "";
    public bool InitialSaveAndLoad { get; set; }
    public bool InterruptedWritePreservedExisting { get; set; }
    public bool AtomicReplacement { get; set; }
    public bool CorruptJsonRecoveredWithDefaults { get; set; }
    public string? CorruptBackupPath { get; set; }
    public bool WriteFailureReported { get; set; }
    public bool WriteFailurePreservedExisting { get; set; }
    public string? WriteFailureMessage { get; set; }
    public bool PathIsPortableDataDirectory { get; set; }
    public bool NoStoreTemporaryFilesRemain { get; set; }
    public List<string> Errors { get; } = [];
}
