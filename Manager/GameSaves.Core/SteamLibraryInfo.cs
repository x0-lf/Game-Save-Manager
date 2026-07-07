using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GameSaves.Core
{
    public sealed record SteamLibraryInfo(
            string LibraryPath,
            bool HasSteamApps,
            bool HasCommonFolder,
            int ManifestCount)
    {
        public bool IsValid => HasSteamApps;
    }
}
