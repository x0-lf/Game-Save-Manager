namespace GameSaves.Core.Save
{
    // The embedded JSON also carries schemaVersion, exportedUtc and, per entry,
    // sourceName and reviewStatus for human readers. The seeder ignores them:
    // every seeded row is 'CuratedSeed', Approved and enabled by definition.
    public sealed record CuratedMappingSeedDocument(
        IReadOnlyList<CuratedMappingEntry> Mappings);

    public sealed record CuratedMappingEntry(
        string SteamAppId,
        string GameName,
        string Platform,
        string PathTemplate,
        string PathKind,
        string? SourceUrl,
        string? SourceLicense,
        string? Notes,
        int Priority = 100);

    public sealed record CuratedSeedResult(
        int TotalProcessed,
        int Inserted,
        int Updated,
        int Unchanged,
        int SkippedUserOverrides);
}
