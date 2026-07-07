using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GameSaves.Core
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
