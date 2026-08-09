namespace BakuretsuOsakanaKobo.Playback;

internal sealed class StreamingLookaheadLimiter
{
    private readonly int _channels;
    private readonly double _boost;
    private readonly double _ceiling;
    private readonly double _releaseCoefficient;
    private readonly int _lookaheadFrames;
    private readonly float[] _pending;
    private readonly long[] _pendingIndices;
    private readonly long[] _peakIndices;
    private readonly double[] _peakValues;
    private int _pendingHead;
    private int _pendingCount;
    private int _peakHead;
    private int _peakCount;
    private long _nextIndex;
    private double _smoothedGain = 1;

    public StreamingLookaheadLimiter(
        int channels,
        int sampleRate,
        double boost,
        double ceiling,
        double lookaheadMilliseconds,
        double releaseMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ThrowIfNotFinite(boost);
        ThrowIfNotFinite(ceiling);
        ThrowIfNotFinite(lookaheadMilliseconds);
        ThrowIfNotFinite(releaseMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(boost);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ceiling, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ceiling, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lookaheadMilliseconds, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(releaseMilliseconds, 0);

        _channels = channels;
        _boost = boost;
        _ceiling = ceiling;
        _lookaheadFrames = Math.Max(1, (int)Math.Round(sampleRate * lookaheadMilliseconds / 1000));
        _releaseCoefficient = Math.Exp(-1 / (sampleRate * releaseMilliseconds / 1000));
        var capacity = _lookaheadFrames + 1;
        _pending = new float[capacity * channels];
        _pendingIndices = new long[capacity];
        _peakIndices = new long[capacity];
        _peakValues = new double[capacity];
    }

    private static void ThrowIfNotFinite(double value, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Value must be finite.");
        }
    }

    public long OutputFrames { get; private set; }

    public int PendingFrames => _pendingCount;

    public double Peak { get; private set; }

    public long OverRangeSamples { get; private set; }

    public long NonFiniteInputSamples { get; private set; }

    public long NonFiniteOutputSamples { get; private set; }

    public double MinimumAppliedGain { get; private set; } = 1;

    public float[] Process(float[] input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length % _channels != 0)
        {
            throw new ArgumentException("Input must contain complete PCM frames.", nameof(input));
        }

        var output = new float[input.Length];
        var outputSamples = 0;
        for (var offset = 0; offset < input.Length; offset += _channels)
        {
            AddFrame(input, offset);
            if (_pendingCount > _lookaheadFrames)
            {
                outputSamples += EmitFrame(output, outputSamples);
            }
        }

        if (outputSamples == output.Length)
        {
            return output;
        }

        Array.Resize(ref output, outputSamples);
        return output;
    }

    public float[] Flush()
    {
        var output = new float[_pendingCount * _channels];
        var outputSamples = 0;
        while (_pendingCount > 0)
        {
            outputSamples += EmitFrame(output, outputSamples);
        }

        return output;
    }

    public void Reset()
    {
        _pendingHead = 0;
        _pendingCount = 0;
        _peakHead = 0;
        _peakCount = 0;
        _nextIndex = 0;
        _smoothedGain = 1;
    }

    private void AddFrame(float[] input, int offset)
    {
        var capacity = _pendingIndices.Length;
        var slot = (_pendingHead + _pendingCount) % capacity;
        var index = _nextIndex++;
        double peak = 0;
        for (var channel = 0; channel < _channels; channel++)
        {
            var inputValue = input[offset + channel];
            if (!float.IsFinite(inputValue))
            {
                NonFiniteInputSamples++;
                inputValue = 0;
            }

            var value = (float)(inputValue * _boost);
            _pending[(slot * _channels) + channel] = value;
            peak = Math.Max(peak, Math.Abs((double)value));
        }

        _pendingIndices[slot] = index;
        _pendingCount++;

        while (_peakCount > 0)
        {
            var last = (_peakHead + _peakCount - 1) % capacity;
            if (_peakValues[last] > peak)
            {
                break;
            }

            _peakCount--;
        }

        var peakSlot = (_peakHead + _peakCount) % capacity;
        _peakIndices[peakSlot] = index;
        _peakValues[peakSlot] = peak;
        _peakCount++;
    }

    private int EmitFrame(float[] output, int outputOffset)
    {
        var capacity = _pendingIndices.Length;
        var index = _pendingIndices[_pendingHead];
        var lookaheadPeak = _peakValues[_peakHead];
        var requiredGain = lookaheadPeak > _ceiling ? _ceiling / lookaheadPeak : 1;
        _smoothedGain = requiredGain < _smoothedGain
            ? requiredGain
            : requiredGain + (_releaseCoefficient * (_smoothedGain - requiredGain));
        MinimumAppliedGain = Math.Min(MinimumAppliedGain, _smoothedGain);

        for (var channel = 0; channel < _channels; channel++)
        {
            var value = _pending[(_pendingHead * _channels) + channel] * _smoothedGain;
            value = Math.Clamp(value, -_ceiling, _ceiling);
            if (!double.IsFinite(value))
            {
                NonFiniteOutputSamples++;
                value = 0;
            }

            output[outputOffset + channel] = (float)value;
            Peak = Math.Max(Peak, Math.Abs(value));
            if (Math.Abs(value) > 1)
            {
                OverRangeSamples++;
            }
        }

        OutputFrames++;
        _pendingHead = (_pendingHead + 1) % capacity;
        _pendingCount--;
        if (_peakCount > 0 && _peakIndices[_peakHead] <= index)
        {
            _peakHead = (_peakHead + 1) % capacity;
            _peakCount--;
        }

        return _channels;
    }
}
