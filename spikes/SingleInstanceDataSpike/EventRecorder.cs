using System.IO;
using System.Text.Json;

namespace BakuretsuOsakanaKobo.Spikes.SingleInstanceData;

internal sealed class EventRecorder : IDisposable
{
    private readonly object _sync = new();
    private readonly StreamWriter? _writer;

    internal EventRecorder(string? path)
    {
        if (path is null)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };
    }

    internal void Record(string eventName, object payload)
    {
        if (_writer is null)
        {
            return;
        }

        var line = JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.Now,
            eventName,
            payload,
        });
        lock (_sync)
        {
            _writer.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _writer?.Dispose();
        }
    }
}
