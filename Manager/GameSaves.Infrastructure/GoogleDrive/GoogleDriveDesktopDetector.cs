using GameSaves.Core.Sync;

namespace GameSaves.Infrastructure.GoogleDrive
{
    /// <summary>
    /// Locates the virtual drive mounted by Google Drive for Desktop (for
    /// example "G:\My Drive"). Only a volume the client itself identifies,
    /// by its DriveFS file system or its "Google Drive" label, counts: a bare
    /// G: drive or a leftover "Google Drive" folder may be a USB stick or a
    /// plain local folder that nothing uploads, and pointing backups there
    /// would be a false off-site claim.
    /// </summary>
    public sealed class GoogleDriveDesktopDetector : IGoogleDriveDesktopDetector
    {
        private readonly Func<IEnumerable<(string Root, string Format, string Label)>> _getDrives;
        private readonly Func<string, bool> _directoryExists;

        // ponytail: detected once per process. The path is read on the UI
        // thread for every keystroke in the folder box, and a dead mapped
        // drive can block each probe for the SMB timeout. Mounting Drive
        // later needs an app restart to be noticed.
        private readonly Lazy<string?> _mountedDrivePath;

        public GoogleDriveDesktopDetector(
            Func<IEnumerable<(string Root, string Format, string Label)>>? getDrives = null,
            Func<string, bool>? directoryExists = null)
        {
            _getDrives = getDrives ?? ReadReadyDrives;
            _directoryExists = directoryExists ?? Directory.Exists;
            _mountedDrivePath = new Lazy<string?>(DetectMountedPath);
        }

        public string? MountedDrivePath => _mountedDrivePath.Value;

        public string? DefaultSyncFolderPath =>
            MountedDrivePath is { } mounted ? Path.Combine(mounted, "GameSaves") : null;

        private string? DetectMountedPath()
        {
            try
            {
                foreach ((string root, string format, string label) in _getDrives())
                {
                    bool isDriveFs = string.Equals(format, "DriveFS", StringComparison.OrdinalIgnoreCase);
                    bool hasDriveLabel = label.Contains("Google Drive", StringComparison.OrdinalIgnoreCase);

                    if (!isDriveFs && !hasDriveLabel)
                        continue;

                    string myDrive = Path.Combine(root, "My Drive");
                    return _directoryExists(myDrive) ? myDrive : root;
                }
            }
            catch
            {
                // Drive inspection is best-effort and must never throw.
            }

            return null;
        }

        private static IEnumerable<(string Root, string Format, string Label)> ReadReadyDrives()
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                (string, string, string)? entry = null;

                // One unreadable drive (a disconnected share, an ejected card)
                // must not end the scan for the others.
                try
                {
                    if (drive.IsReady)
                        entry = (drive.RootDirectory.FullName, drive.DriveFormat, drive.VolumeLabel ?? "");
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }

                if (entry is { } value)
                    yield return value;
            }
        }
    }
}
