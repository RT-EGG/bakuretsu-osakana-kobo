namespace BakuretsuOsakanaKobo.Infrastructure.Persistence;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public static bool IsValid(AppSettings settings) =>
        settings.SchemaVersion == CurrentSchemaVersion;
}
