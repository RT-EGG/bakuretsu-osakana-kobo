namespace BakuretsuOsakanaKobo;

internal sealed class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    private SemanticVersion(
        ulong major,
        ulong minor,
        ulong patch,
        IReadOnlyList<string> prereleaseIdentifiers,
        string? buildMetadata)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PrereleaseIdentifiers = prereleaseIdentifiers;
        BuildMetadata = buildMetadata;
    }

    internal ulong Major { get; }

    internal ulong Minor { get; }

    internal ulong Patch { get; }

    internal IReadOnlyList<string> PrereleaseIdentifiers { get; }

    internal string? BuildMetadata { get; }

    internal static bool TryParse(string? value, out SemanticVersion? version)
    {
        version = null;
        if (string.IsNullOrEmpty(value) || value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var buildSeparator = value.IndexOf('+');
        var coreAndPrerelease = buildSeparator >= 0 ? value[..buildSeparator] : value;
        var buildMetadata = buildSeparator >= 0 ? value[(buildSeparator + 1)..] : null;
        if (buildSeparator >= 0 &&
            (!AreValidIdentifiers(buildMetadata!, allowNumericLeadingZero: true) ||
             buildMetadata!.Contains('+', StringComparison.Ordinal)))
        {
            return false;
        }

        var prereleaseSeparator = coreAndPrerelease.IndexOf('-');
        var core = prereleaseSeparator >= 0
            ? coreAndPrerelease[..prereleaseSeparator]
            : coreAndPrerelease;
        var prerelease = prereleaseSeparator >= 0
            ? coreAndPrerelease[(prereleaseSeparator + 1)..]
            : null;
        if (prerelease is not null && !AreValidIdentifiers(prerelease, allowNumericLeadingZero: false))
        {
            return false;
        }

        var coreParts = core.Split('.');
        if (coreParts.Length != 3 ||
            !TryParseCoreNumber(coreParts[0], out var major) ||
            !TryParseCoreNumber(coreParts[1], out var minor) ||
            !TryParseCoreNumber(coreParts[2], out var patch))
        {
            return false;
        }

        version = new SemanticVersion(
            major,
            minor,
            patch,
            prerelease?.Split('.') ?? [],
            buildMetadata);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var coreComparison = Major.CompareTo(other.Major);
        if (coreComparison == 0)
        {
            coreComparison = Minor.CompareTo(other.Minor);
        }

        if (coreComparison == 0)
        {
            coreComparison = Patch.CompareTo(other.Patch);
        }

        if (coreComparison != 0)
        {
            return coreComparison;
        }

        if (PrereleaseIdentifiers.Count == 0 || other.PrereleaseIdentifiers.Count == 0)
        {
            return PrereleaseIdentifiers.Count == other.PrereleaseIdentifiers.Count
                ? 0
                : PrereleaseIdentifiers.Count == 0 ? 1 : -1;
        }

        var count = Math.Min(PrereleaseIdentifiers.Count, other.PrereleaseIdentifiers.Count);
        for (var index = 0; index < count; index++)
        {
            var comparison = ComparePrereleaseIdentifier(
                PrereleaseIdentifiers[index],
                other.PrereleaseIdentifiers[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return PrereleaseIdentifiers.Count.CompareTo(other.PrereleaseIdentifiers.Count);
    }

    public bool Equals(SemanticVersion? other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Major);
        hash.Add(Minor);
        hash.Add(Patch);
        foreach (var identifier in PrereleaseIdentifiers)
        {
            hash.Add(identifier, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    public override string ToString()
    {
        var value = $"{Major}.{Minor}.{Patch}";
        if (PrereleaseIdentifiers.Count > 0)
        {
            value += $"-{string.Join('.', PrereleaseIdentifiers)}";
        }

        return BuildMetadata is null ? value : $"{value}+{BuildMetadata}";
    }

    private static bool TryParseCoreNumber(string value, out ulong number)
    {
        number = 0;
        return value.Length > 0 &&
               (value.Length == 1 || value[0] != '0') &&
               value.All(static character => character is >= '0' and <= '9') &&
               ulong.TryParse(value, System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture, out number);
    }

    private static bool AreValidIdentifiers(string value, bool allowNumericLeadingZero)
    {
        var identifiers = value.Split('.');
        return identifiers.All(identifier =>
            identifier.Length > 0 &&
            identifier.All(static character =>
                character is >= '0' and <= '9' or
                    >= 'A' and <= 'Z' or
                    >= 'a' and <= 'z' or '-') &&
            (allowNumericLeadingZero ||
             identifier.Length == 1 ||
             identifier[0] != '0' ||
             !identifier.All(static character => character is >= '0' and <= '9')));
    }

    private static int ComparePrereleaseIdentifier(string left, string right)
    {
        var leftNumeric = left.All(static character => character is >= '0' and <= '9');
        var rightNumeric = right.All(static character => character is >= '0' and <= '9');
        if (leftNumeric != rightNumeric)
        {
            return leftNumeric ? -1 : 1;
        }

        if (!leftNumeric)
        {
            return string.CompareOrdinal(left, right);
        }

        var lengthComparison = left.Length.CompareTo(right.Length);
        return lengthComparison != 0 ? lengthComparison : string.CompareOrdinal(left, right);
    }
}
