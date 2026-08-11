using System.Globalization;

namespace BakuretsuOsakanaKobo.Playback;

public static class PlaybackRate
{
    public const float Default = 1.0f;

    public static IReadOnlyList<float> Supported { get; } =
        Array.AsReadOnly(new[] { 0.25f, 0.5f, Default, 1.5f, 2.0f });

    public static bool IsSupported(float rate) =>
        float.IsFinite(rate) && Supported.Any(candidate => AreEqual(candidate, rate));

    public static bool TryParse(string? text, out float rate)
    {
        if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
            IsSupported(parsed))
        {
            rate = Supported.First(candidate => AreEqual(candidate, parsed));
            return true;
        }

        rate = Default;
        return false;
    }

    public static string Format(float rate) =>
        IsSupported(rate)
            ? $"{rate.ToString("0.0#", CultureInfo.InvariantCulture)}×"
            : $"{Default.ToString("0.0", CultureInfo.InvariantCulture)}×";

    public static bool AreEqual(float left, float right) => Math.Abs(left - right) < 0.001f;
}
