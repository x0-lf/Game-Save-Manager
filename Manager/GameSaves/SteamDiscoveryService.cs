using System.Collections.Generic;

namespace GameSave
{
    public sealed class SteamDiscoveryService
    {
        private readonly RegistrySteamLocator _registrySteamLocator = new();
        private readonly SteamLibraryFoldersReader _libraryFoldersReader = new();
        private readonly SteamAppManifestReader _appManifestReader = new();

        public SteamDiscoveryResult Discover()
        {
            var result = new SteamDiscoveryResult();

            if (!_registrySteamLocator.TryLocate(out string steamRoot))
            {
                result.Warnings.Add("Steam InstallPath was not found in the Windows registry.");
                return result;
            }

            SteamRootValidationResult rootValidation = SteamRootValidator.Validate(steamRoot);

            if (!rootValidation.IsLikelySteamRoot)
            {
                result.Warnings.Add($"Registry path was found, but it does not look like a valid Steam root: {steamRoot}");
                return result;
            }

            result.SteamRoot = steamRoot;
            result.SteamRootValidation = rootValidation;

            var libraryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                steamRoot
            };

            foreach (string libraryPath in _libraryFoldersReader.ReadLibraryPaths(steamRoot))
                libraryPaths.Add(libraryPath);

            foreach (string libraryPath in libraryPaths)
            {
                SteamLibraryInfo libraryInfo = SteamLibraryValidator.Validate(libraryPath);

                if (!libraryInfo.IsValid)
                {
                    result.Warnings.Add($"Invalid Steam library skipped: {libraryPath}");
                    continue;
                }

                result.Libraries.Add(libraryInfo);
                result.Games.AddRange(_appManifestReader.ReadInstalledGames(libraryPath));
            }

            return result;
        }
    }
}