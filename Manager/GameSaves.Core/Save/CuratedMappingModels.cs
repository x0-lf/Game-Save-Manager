namespace GameSaves.Core.Save
{
    public sealed record CuratedMappingSeedDocument(
        int SchemaVersion,
        DateTimeOffset ExportedUtc,
        IReadOnlyList<CuratedMappingEntry> Mappings);

    public sealed record CuratedMappingEntry(
        string SteamAppId,
        string GameName,
        string Platform,
        string PathTemplate,
        string PathKind,
        string SourceName,
        string? SourceUrl,
        string? SourceLicense,
        string? Notes,
        int Priority = 100,
        string ReviewStatus = "Approved");

    public sealed record CuratedSeedResult(
        int TotalProcessed,
        int Inserted,
        int Updated,
        int Unchanged,
        int SkippedUserOverrides);
}
