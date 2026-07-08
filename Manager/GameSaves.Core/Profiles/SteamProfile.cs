using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GameSaves.Core.Profiles
{
    public sealed record SteamProfile(
        string AccountId,
        string? SteamId64,
        string? DisplayName,
        string UserDataPath,
        int AppFolderCount,
        bool IsCurrentUser);
}
