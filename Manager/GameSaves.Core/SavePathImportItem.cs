using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GameSaves.Core
{
    public sealed record SavePathImportItem(
        string SteamAppId,
        string? GameName,
        string Platform,
        string PathTemplate,
        string PathKind,
        string SourceName,
        string? SourceUrl,
        string? SourceLicense,
        string? Notes,
        int Priority = 100);
}
