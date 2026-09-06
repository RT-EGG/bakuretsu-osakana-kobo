using NAudio.Wave;

namespace BakuretsuOsakanaKobo.Playback;

internal sealed class RealtimeAudioPipeline
{
    private static readonly TimeSpan BufferDuration = TimeSpan.FromSeconds(2);
    private static readonly int MaximumStagedBytes =
        (int)Math.Ceiling(
            RealtimeVolumeProcessor.SampleRate *
            RealtimeVolumeProcessor.LookaheadMilliseconds /
            1000) *
        sizeof(float) *
        RealtimeVolumeProcessor.Channels;
    private readonly RealtimeVolumeProcessor _processor = new();
    private readonly BufferedWaveProvider _rawBuffer;
    private byte[] _stagedOutput = [];
    private int _stagedOffset;
    private bool _drainFlushed;

    public RealtimeAudioPipeline()
    {
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
            RealtimeVolumeProcessor.SampleRate,
            RealtimeVolumeProcessor.Channels);
        _rawBuffer = new BufferedWaveProvider(WaveFormat)
        {
            BufferDuration = BufferDuration,
            DiscardOnBufferOverflow = false,
            ReadFully = false,
        };
    }

    public WaveFormat WaveFormat { get; }

    public int VolumePercent => _processor.VolumePercent;

    public bool IsMuted => _processor.IsMuted;

    public int RawBufferedBytes => _rawBuffer.BufferedBytes;

    public TimeSpan RawBufferedDuration => _rawBuffer.BufferedDuration;

    public int PendingFrames => _processor.PendingFrames;

    public long NonFiniteInputSamples => _processor.NonFiniteInputSamples;

    public long NonFiniteOutputSamples => _processor.NonFiniteOutputSamples;

    public double Peak => _processor.Peak;

    public long OverRangeSamples => _processor.OverRangeSamples;

    public int PlaybackBufferedBytes => _rawBuffer.BufferedBytes + StagedBytes;

    public int DrainBufferedBytes =>
        PlaybackBufferedBytes + PendingFrames * WaveFormat.BlockAlign;

    public bool IsDrainComplete => PlaybackBufferedBytes == 0 && PendingFrames == 0;

    private int StagedBytes => _stagedOutput.Length - _stagedOffset;

    public void SetVolumePercent(int volumePercent) => _processor.SetVolumePercent(volumePercent);

    public void SetMuted(bool isMuted) => _processor.SetMuted(isMuted);

    public void AddPcm16(short[] pcm16)
    {
        ArgumentNullException.ThrowIfNull(pcm16);
        if (pcm16.Length % RealtimeVolumeProcessor.Channels != 0)
        {
            throw new ArgumentException("Input must contain complete stereo PCM frames.", nameof(pcm16));
        }

        var bytes = new byte[pcm16.Length * sizeof(float)];
        var samples = new float[pcm16.Length];
        for (var index = 0; index < pcm16.Length; index++)
        {
            samples[index] = pcm16[index] / 32768f;
        }

        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        _rawBuffer.AddSamples(bytes, 0, bytes.Length);
        _drainFlushed = false;
    }

    public void BeginDrain() => _drainFlushed = false;

    public int Read(byte[] destination, int offset, int count, bool isDraining, out int generatedFrames)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > destination.Length - count)
        {
            throw new ArgumentException("The destination range is outside the buffer.");
        }

        if (count % WaveFormat.BlockAlign != 0)
        {
            throw new ArgumentException("The requested byte count must contain complete PCM frames.", nameof(count));
        }

        generatedFrames = 0;
        var written = CopyStagedOutput(destination, offset, count);
        while (written < count)
        {
            if (_rawBuffer.BufferedBytes > 0)
            {
                var rawByteCount = Math.Min(count - written, _rawBuffer.BufferedBytes);
                rawByteCount -= rawByteCount % WaveFormat.BlockAlign;
                if (rawByteCount == 0)
                {
                    break;
                }

                var rawBytes = new byte[rawByteCount];
                var rawRead = _rawBuffer.Read(rawBytes, 0, rawBytes.Length);
                var rawSamples = new float[rawRead / sizeof(float)];
                Buffer.BlockCopy(rawBytes, 0, rawSamples, 0, rawRead);
                var processed = _processor.Process(rawSamples);
                generatedFrames += processed.Length / RealtimeVolumeProcessor.Channels;
                written += CopyProcessedOutput(
                    processed,
                    destination,
                    offset + written,
                    count - written);
                continue;
            }

            if (isDraining && !_drainFlushed)
            {
                var flushed = _processor.Flush();
                _drainFlushed = true;
                generatedFrames += flushed.Length / RealtimeVolumeProcessor.Channels;
                written += CopyProcessedOutput(
                    flushed,
                    destination,
                    offset + written,
                    count - written);
                continue;
            }

            break;
        }

        return written;
    }

    public RealtimeAudioPipelineReset Reset()
    {
        var reset = new RealtimeAudioPipelineReset(
            _rawBuffer.BufferedBytes / WaveFormat.BlockAlign,
            StagedBytes,
            _processor.PendingFrames);
        _rawBuffer.ClearBuffer();
        _stagedOutput = [];
        _stagedOffset = 0;
        _processor.Reset();
        _drainFlushed = false;
        return reset;
    }

    private int CopyStagedOutput(byte[] destination, int offset, int count)
    {
        var copied = Math.Min(count, StagedBytes);
        if (copied == 0)
        {
            return 0;
        }

        Buffer.BlockCopy(_stagedOutput, _stagedOffset, destination, offset, copied);
        _stagedOffset += copied;
        if (_stagedOffset == _stagedOutput.Length)
        {
            _stagedOutput = [];
            _stagedOffset = 0;
        }

        return copied;
    }

    private int CopyProcessedOutput(
        float[] samples,
        byte[] destination,
        int offset,
        int count)
    {
        if (samples.Length == 0)
        {
            return 0;
        }

        var sampleBytes = samples.Length * sizeof(float);
        var copied = Math.Min(count, sampleBytes);
        Buffer.BlockCopy(samples, 0, destination, offset, copied);
        var remainder = sampleBytes - copied;
        if (remainder == 0)
        {
            return copied;
        }

        if (StagedBytes != 0 || remainder > MaximumStagedBytes)
        {
            throw new InvalidOperationException(
                $"The bounded processed PCM staging buffer exceeded {MaximumStagedBytes} byte(s).");
        }

        _stagedOutput = new byte[remainder];
        Buffer.BlockCopy(samples, copied, _stagedOutput, 0, remainder);
        _stagedOffset = 0;
        return copied;
    }
}

internal readonly record struct RealtimeAudioPipelineReset(
    int DiscardedRawFrames,
    int DiscardedProcessedBytes,
    int DiscardedLimiterFrames);
