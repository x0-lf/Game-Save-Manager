using System.Collections.Generic;

namespace GameSave
{
    public sealed class SteamDiscoveryResult
    {
        public string? SteamRoot { get; set; }

        public SteamRootValidationResult? SteamRootValidation { get; set; }

        public List<SteamLibraryInfo> Libraries { get; } = new();

        public List<SteamGame> Games { get; } = new();

        public List<string> Warnings { get; } = new();
    }
}