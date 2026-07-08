using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GameSaves.Core.Steam
{
    public interface ISteamRootLocator
    {
        bool TryLocate(out string steamPath);
    }
}
