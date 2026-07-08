using GameSaves.Core.Save;
using GameSaves.Core.Steam;

namespace GameSaves.Core.Save
{
    public interface ISavePathVerifier
    {
        List<SavePathVerificationResult> Verify(SteamGame game, string? steamRoot, IEnumerable<SavePathMapping> mappings);
    }
}