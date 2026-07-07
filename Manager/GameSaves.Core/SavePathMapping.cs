using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GameSaves.Core
{
    public enum SavePathKind
    {
        Directory,
        File,
        Glob
    }

    public sealed record SavePathMapping(
        long Id,
        string SteamAppId,
        string? GameName,
        string Platform,
        string PathTemplate,
        SavePathKind PathKind,
        string SourceName,
        string? SourceUrl,
        string? SourceLicense,
        string? Notes,
        int Priority,
        bool Enabled);
}
