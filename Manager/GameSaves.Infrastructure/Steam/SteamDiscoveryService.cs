using GameSaves.Core.Steam;
using GameSaves.Infrastructure.Registry;

namespace GameSaves.Infrastructure.Steam
{
    public sealed class SteamDiscoveryService : ISteamDiscoveryService
    {
        private readonly ISteamRootLocator _steamRootLocator;
        private readonly ISteamLibraryFoldersReader _libraryFoldersReader;
        private readonly ISteamAppManifestReader _appManifestReader;
        private readonly ISteamFallbackScanner _fallbackScanner;

        public SteamDiscoveryService()
            : this(
                new RegistrySteamLocator(),
                new SteamLibraryFoldersReader(),
                new SteamAppManifestReader(),
                new SteamFallbackScanner())
        {
        }

        public SteamDiscoveryService(
            ISteamRootLocator steamRootLocator,
            ISteamLibraryFoldersReader libraryFoldersReader,
            ISteamAppManifestReader appManifestReader,
            ISteamFallbackScanner fallbackScanner)
        {
            _steamRootLocator = steamRootLocator;
            _libraryFoldersReader = libraryFoldersReader;
            _appManifestReader = appManifestReader;
            _fallbackScanner = fallbackScanner;
        }

        public SteamDiscoveryResult Discover(
            SteamDiscoveryOptions? options = null,
            IProgress<SteamFallbackScanProgress>? fallbackProgress = null,
            CancellationToken cancellationToken = default)
        {
            options ??= new SteamDiscoveryOptions();

            var result = new SteamDiscoveryResult();
            var libraryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            DiscoverFromRegistryAndSteamMetadata(result, libraryPaths);

            DiscoverLibrariesAndGames(
                result,
                libraryPaths,
                SteamDiscoveryConfidence.High);

            if (ShouldRunFallbackScan(options, result))
            {
                RunFallbackScan(
                    result,
                    libraryPaths,
                    options,
                    fallbackProgress,
                    cancellationToken);
            }

            return result;
        }

        private void DiscoverFromRegistryAndSteamMetadata(
            SteamDiscoveryResult result,
            HashSet<string> libraryPaths)
        {
            if (!_steamRootLocator.TryLocate(out string steamRoot))
            {
                result.Warnings.Add("Steam InstallPath was not found in the Windows registry.");
                return;
            }

            SteamRootValidationResult rootValidation = SteamRootValidator.Validate(steamRoot);

            if (!rootValidation.IsLikelySteamRoot)
            {
                result.Warnings.Add($"Registry path was found, but it does not look like a valid Steam root: {steamRoot}");
                return;
            }

            result.SteamRoot = steamRoot;
            result.SteamRootValidation = rootValidation;

            libraryPaths.Add(steamRoot);

            foreach (string libraryPath in _libraryFoldersReader.ReadLibraryPaths(steamRoot))
                libraryPaths.Add(libraryPath);
        }

        private void DiscoverLibrariesAndGames(
            SteamDiscoveryResult result,
            IEnumerable<string> libraryPaths,
            SteamDiscoveryConfidence confidenceWhenFolderExists)
        {
            var alreadyKnownLibraries = new HashSet<string>(
                result.Libraries.Select(library => library.LibraryPath),
                StringComparer.OrdinalIgnoreCase);

            foreach (string libraryPath in libraryPaths)
            {
                if (alreadyKnownLibraries.Contains(libraryPath))
                    continue;

                SteamLibraryInfo libraryInfo = SteamLibraryValidator.Validate(libraryPath);

                if (!libraryInfo.IsValid)
                {
                    result.Warnings.Add($"Invalid Steam library skipped: {libraryPath}");
                    continue;
                }

                result.Libraries.Add(libraryInfo);

                result.Games.AddRange(
                    _appManifestReader.ReadInstalledGames(
                        libraryPath,
                        confidenceWhenFolderExists));

                alreadyKnownLibraries.Add(libraryPath);
            }
        }

        private static bool ShouldRunFallbackScan(
            SteamDiscoveryOptions options,
            SteamDiscoveryResult result)
        {
            return options.FallbackScanMode switch
            {
                SteamFallbackScanMode.Never => false,

                SteamFallbackScanMode.Always => true,

                SteamFallbackScanMode.WhenNormalDiscoveryFails =>
                    result.SteamRoot is null ||
                    result.Libraries.Count == 0,

                _ => false
            };
        }

        private void RunFallbackScan(
            SteamDiscoveryResult result,
            HashSet<string> knownLibraryPaths,
            SteamDiscoveryOptions options,
            IProgress<SteamFallbackScanProgress>? fallbackProgress,
            CancellationToken cancellationToken)
        {
            result.Warnings.Add("Fallback disk scan started. This is slower than registry/VDF discovery.");

            using CancellationTokenSource? timeoutSource =
                options.FallbackTimeout is null
                    ? null
                    : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            if (timeoutSource is not null && options.FallbackTimeout is TimeSpan timeout)
                timeoutSource.CancelAfter(timeout);

            CancellationToken effectiveToken = timeoutSource?.Token ?? cancellationToken;

            SteamFallbackScanResult scanResult;

            try
            {
                scanResult = _fallbackScanner.Scan(
                    options,
                    fallbackProgress,
                    effectiveToken);
            }
            catch (OperationCanceledException)
            {
                result.Warnings.Add("Fallback disk scan was cancelled or timed out.");
                return;
            }

            var newLibraryPaths = new List<string>();

            foreach (string libraryPath in scanResult.LibraryPaths)
            {
                if (knownLibraryPaths.Add(libraryPath))
                    newLibraryPaths.Add(libraryPath);
            }

            DiscoverLibrariesAndGames(
                result,
                newLibraryPaths,
                SteamDiscoveryConfidence.Low);

            result.Warnings.Add(
                $"Fallback disk scan finished. Directories scanned: {scanResult.DirectoriesScanned}. Libraries found: {scanResult.LibraryPaths.Count}.");

            if (scanResult.SkippedDirectories.Count > 0)
            {
                result.Warnings.Add(
                    $"Fallback scan skipped {scanResult.SkippedDirectories.Count} inaccessible or unsafe directories.");
            }
        }
    }
}