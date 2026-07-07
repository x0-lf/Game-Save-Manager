using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GameSaves.Core
{
    public sealed record SteamRootValidationResult(
            string SteamRoot,
            bool HasSteamExe,
            bool HasSteamDll,
            bool HasSteamAppsDirectory,
            bool HasConfigDirectory)
    {
        public bool IsLikelySteamRoot =>
            HasSteamExe &&
            HasSteamDll &&
            HasSteamAppsDirectory;
    }
}
