using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GameSaves.Core
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
