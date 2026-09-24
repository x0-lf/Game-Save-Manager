using GameSaves.Core.Save;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GameSaves.Core.Catalog
{
    /// <summary>
    /// Service contract for analyzing game directory structures, detecting game engine fingerprints,
    /// and proposing tokenized candidate save paths for human review.
    /// </summary>
    public interface IAiPatternDetectorService
    {
        /// <summary>
        /// Analyzes a game directory, detects engine signatures, and generates save path proposals.
        /// </summary>
        Task<AiDetectionResult> DetectSavePathsAsync(
            AiDetectionRequest request,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Converts detection proposals into schema-valid SavePathImportItem objects
        /// with review_status strictly defaulted to 'Pending' and enabled = false.
        /// Throws <see cref="System.InvalidOperationException"/> when the result has no numeric Steam AppID.
        /// </summary>
        List<SavePathImportItem> ToImportItems(
            AiDetectionResult result,
            int? overridePriority = null);

        /// <summary>
        /// Converts detection proposals into a schema-valid MappingImportDocument.
        /// Throws <see cref="System.InvalidOperationException"/> when the result has no numeric Steam AppID.
        /// </summary>
        MappingImportDocument ToImportDocument(
            AiDetectionResult result,
            int? overridePriority = null);
    }
}
