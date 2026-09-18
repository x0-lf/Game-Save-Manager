namespace GameSaves.Core.Save
{
    public interface ICuratedMappingSeeder
    {
        /// <summary>
        /// Seeds the bundled project-curated mapping dataset into the target SQLite database.
        /// Applies deterministic update and merge precedence rules, preserving user custom
        /// mappings and local modifications.
        /// </summary>
        CuratedSeedResult Seed(string databasePath);

        /// <summary>
        /// Seeds a custom JSON dataset into the target SQLite database.
        /// </summary>
        CuratedSeedResult Seed(string databasePath, string jsonContent);

        /// <summary>
        /// Seeds a parsed seed document into the target SQLite database.
        /// </summary>
        CuratedSeedResult Seed(string databasePath, CuratedMappingSeedDocument document);

        /// <summary>
        /// Loads and parses the versioned curated mapping seed document bundled as an embedded resource.
        /// </summary>
        CuratedMappingSeedDocument LoadCuratedSeed();

        /// <summary>
        /// Parses a JSON string into a <see cref="CuratedMappingSeedDocument"/>.
        /// </summary>
        CuratedMappingSeedDocument ParseSeedDocument(string jsonContent);
    }
}
