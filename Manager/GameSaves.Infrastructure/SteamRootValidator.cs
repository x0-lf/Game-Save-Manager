using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GameSaves.Core;

namespace GameSaves.Infrastructure
{
    public static class SteamRootValidator
    {
        public static SteamRootValidationResult Validate(string steamRoot)
        {
            bool hasSteamExe = File.Exists(Path.Combine(steamRoot, "steam.exe"));
            bool hasSteamDll = File.Exists(Path.Combine(steamRoot, "steam.dll"));
            bool hasSteamApps = Directory.Exists(Path.Combine(steamRoot, "steamapps"));
            bool hasConfig = Directory.Exists(Path.Combine(steamRoot, "config"));

            return new SteamRootValidationResult(
                steamRoot,
                hasSteamExe,
                hasSteamDll,
                hasSteamApps,
                hasConfig);
        }
    }
}
