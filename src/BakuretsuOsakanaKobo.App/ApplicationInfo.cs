using System.Reflection;

namespace BakuretsuOsakanaKobo;

public static class ApplicationInfo
{
    public const string DisplayName = "爆裂おさかな工房";

    internal static bool TryGetCurrentSemanticVersion(out SemanticVersion? version)
    {
        var informationalVersion = typeof(ApplicationInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return SemanticVersion.TryParse(informationalVersion, out version);
    }
}
