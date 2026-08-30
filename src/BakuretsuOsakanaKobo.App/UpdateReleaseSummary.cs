namespace BakuretsuOsakanaKobo;

internal static class UpdateReleaseSummary
{
    private const int MaximumLength = 600;

    internal static string Create(string? releaseBody)
    {
        if (string.IsNullOrWhiteSpace(releaseBody))
        {
            return "更新内容の詳細はGitHub Releaseページで確認できます。";
        }

        var normalized = releaseBody
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        return normalized.Length <= MaximumLength
            ? normalized
            : $"{normalized[..MaximumLength].TrimEnd()}…";
    }
}
