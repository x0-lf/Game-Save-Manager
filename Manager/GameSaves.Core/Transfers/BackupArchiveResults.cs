namespace GameSaves.Core.Transfers
{
    public sealed record BackupArchiveExportResult(
        bool Success,
        string? ArchivePath,
        long Bytes,
        string Message);

    public sealed record BackupArchiveImportResult(
        bool Success,
        string? RunPath,
        int FileCount,
        string Message);
}
