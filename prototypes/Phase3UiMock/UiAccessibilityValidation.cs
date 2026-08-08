using System.Windows.Media;

namespace BakuretsuOsakanaKobo.Phase3UiMock;

internal static class UiAccessibilityValidation
{
    public static double ContrastRatio(string foreground, string background)
    {
        var foregroundColor = (Color)ColorConverter.ConvertFromString(foreground);
        var backgroundColor = (Color)ColorConverter.ConvertFromString(background);
        var lighter = Math.Max(RelativeLuminance(foregroundColor), RelativeLuminance(backgroundColor));
        var darker = Math.Min(RelativeLuminance(foregroundColor), RelativeLuminance(backgroundColor));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color) =>
        0.2126 * Linearize(color.R / 255d)
        + 0.7152 * Linearize(color.G / 255d)
        + 0.0722 * Linearize(color.B / 255d);

    private static double Linearize(double component) => component <= 0.04045
        ? component / 12.92
        : Math.Pow((component + 0.055) / 1.055, 2.4);
}
