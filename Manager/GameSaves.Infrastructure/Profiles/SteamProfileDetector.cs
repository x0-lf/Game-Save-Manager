using GameSaves.Core.Profiles;
using GameSaves.Core.Steam;
using System.Security;

namespace GameSaves.Infrastructure.Profiles
{
    public sealed class SteamProfileDetector : ISteamProfileDetector
    {
        public IReadOnlyList<SteamProfile> DetectProfiles(
            SteamDiscoveryResult discovery,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(discovery.SteamRoot))
                return Array.Empty<SteamProfile>();

            return DetectProfiles(discovery.SteamRoot, cancellationToken);
        }

        public IReadOnlyList<SteamProfile> DetectProfiles(
            string steamRoot,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(steamRoot))
                return Array.Empty<SteamProfile>();

            string userDataRoot = Path.Combine(steamRoot, "userdata");

            if (!Directory.Exists(userDataRoot))
                return Array.Empty<SteamProfile>();

            var profiles = new List<SteamProfile>();

            foreach (string profileDirectory in SafeEnumerateDirectories(userDataRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string accountId = Path.GetFileName(profileDirectory);

                if (string.IsNullOrWhiteSpace(accountId))
                    continue;

                if (!accountId.All(char.IsDigit))
                    continue;

                int appFolderCount = CountNumericAppFolders(profileDirectory);

                profiles.Add(new SteamProfile(
                    AccountId: accountId,
                    SteamId64: null,
                    DisplayName: null,
                    UserDataPath: profileDirectory,
                    AppFolderCount: appFolderCount,
                    IsCurrentUser: false));
            }

            return profiles
                .OrderByDescending(profile => profile.AppFolderCount)
                .ThenBy(profile => profile.AccountId)
                .ToList();
        }

        private static int CountNumericAppFolders(string profileDirectory)
        {
            int count = 0;

            foreach (string directory in SafeEnumerateDirectories(profileDirectory))
            {
                string folderName = Path.GetFileName(directory);

                if (!string.IsNullOrWhiteSpace(folderName) &&
                    folderName.All(char.IsDigit))
                {
                    count++;
                }
            }

            return count;
        }

        private static IEnumerable<string> SafeEnumerateDirectories(string path)
        {
            IEnumerable<string> directories;

            try
            {
                directories = Directory.EnumerateDirectories(
                    path,
                    "*",
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = false,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint
                    });
            }
            catch (Exception ex) when (
                ex is IOException ||
                ex is UnauthorizedAccessException ||
                ex is SecurityException)
            {
                yield break;
            }

            foreach (string directory in directories)
                yield return directory;
        }
    }
}