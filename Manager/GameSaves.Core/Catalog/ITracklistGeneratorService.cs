using System.Collections.Generic;

namespace GameSaves.Core.Catalog
{
    /// <summary>
    /// Reconciles game catalog titles against existing save path mappings and generates
    /// prioritized, privacy-scrubbed tracklists of missing titles for research and harvesting.
    /// </summary>
    public interface ITracklistGeneratorService
    {
        /// <summary>
        /// Generates a tracklist by reconciling provided candidates and/or database titles against save path mappings.
        /// </summary>
        /// <param name="databasePath">Path to the SQLite application database.</param>
        /// <param name="candidates">Optional explicit list of candidate titles. If null, catalog and/or installed titles are used.</param>
        /// <param name="options">Options for filtering, platform, and prioritization.</param>
        /// <returns>A structured tracklist of missing games.</returns>
        MissingTitlesTracklist GenerateTracklist(
            string databasePath,
            IEnumerable<MissingTitleCandidate>? candidates = null,
            TracklistOptions? options = null);

        /// <summary>
        /// Generates a tracklist specifically from installed Steam games.
        /// </summary>
        MissingTitlesTracklist GenerateTracklistFromInstalled(
            string databasePath,
            TracklistOptions? options = null);

        /// <summary>
        /// Exports the tracklist as a schema-valid JSON string.
        /// </summary>
        string ExportJson(MissingTitlesTracklist tracklist, bool indented = true);

        /// <summary>
        /// Exports the tracklist as an RFC 4180 compliant CSV string.
        /// </summary>
        string ExportCsv(MissingTitlesTracklist tracklist);

        /// <summary>
        /// Exports the tracklist directly to a file in the specified format.
        /// </summary>
        void ExportToFile(MissingTitlesTracklist tracklist, string outputPath, TracklistExportFormat format);
    }
}
