namespace GameSaves.Core.Backup
{
    public sealed record BackupItemResult(
        string SteamAppId,
        string GameName,
        string SourcePath,
        string DestinationPath,
        bool Copied,
        long Bytes,
        string? Sha256,
        string? Error);
}