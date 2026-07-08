namespace GameSaves.Core.Save
{
    public sealed record SavePathVerificationResult(
        SavePathMapping Mapping,
        string SteamAppId,
        string GameName,
        string ExpandedPath,
        string NormalizedPath,
        bool Exists,
        bool IsDirectory,
        int FileCount,
        long TotalBytes,
        int Confidence,
        string? Error);
}