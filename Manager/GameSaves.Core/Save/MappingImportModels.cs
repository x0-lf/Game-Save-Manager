using System.Collections.Generic;

namespace GameSaves.Core.Save
{
    public sealed record TitleImportEntry(
        string SteamAppId,
        string Title,
        string? PlatformHint = null,
        int? PcgwPageId = null,
        string? PcgwPageName = null,
        string? SourceName = null,
        string? SourceUrl = null,
        string? SourceLicense = null,
        string? Notes = null);

    public sealed record MappingImportEntry(
        string SteamAppId,
        string? GameName,
        string Platform,
        string PathTemplate,
        string PathKind = "Directory",
        string? SourceName = null,
        string? SourceUrl = null,
        string? SourceLicense = null,
        string? Notes = null,
        int Priority = 100,
        string? ReviewStatus = null);

    public sealed record MappingImportDocument(
        int SchemaVersion = 1,
        IReadOnlyList<TitleImportEntry>? Titles = null,
        IReadOnlyList<MappingImportEntry>? Mappings = null);

    public sealed record MappingImportError(
        int Index,
        string? SteamAppId,
        string Property,
        string Message);

    public sealed record MappingImportOptions(
        bool AutoApprove = false,
        bool ForceReviewStatus = false,
        string DefaultSourceName = "JsonImport",
        bool UpdateExistingTitles = false);

    public sealed record MappingImportReport(
        int TotalItemsProcessed,
        int MappingsInserted,
        int MappingsUpdated,
        int MappingsUnchanged,
        int MappingsSkippedDuplicate,
        int TitlesInserted,
        int TitlesUpdated,
        int TitlesSkippedDuplicate,
        IReadOnlyList<MappingImportError> Errors)
    {
        public bool Success => Errors.Count == 0;

        public string FormatSummary()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Import Summary: {TotalItemsProcessed} item(s) processed.");
            sb.AppendLine($"Mappings: {MappingsInserted} inserted, {MappingsUpdated} updated, {MappingsUnchanged} unchanged, {MappingsSkippedDuplicate} duplicates skipped.");
            sb.AppendLine($"Titles: {TitlesInserted} inserted, {TitlesUpdated} updated, {TitlesSkippedDuplicate} duplicates skipped.");
            if (Errors.Count > 0)
            {
                sb.AppendLine($"Errors ({Errors.Count}):");
                foreach (var err in Errors)
                {
                    sb.AppendLine($"  - Item #{err.Index} [{err.SteamAppId ?? "unknown"}].{err.Property}: {err.Message}");
                }
            }
            return sb.ToString().TrimEnd();
        }
    }
}
