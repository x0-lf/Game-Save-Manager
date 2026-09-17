using GameSaves.Core.Sync;

namespace GameSaves.Infrastructure.GoogleDrive
{
    /// <summary>
    /// Detects whether Google Drive for Desktop is installed and locates any mounted
    /// virtual drives or sync folders (e.g. "G:\My Drive").
    /// </summary>
    public sealed class GoogleDriveDesktopDetector : IGoogleDriveDesktopDetector
    {
        private readonly Func<IEnumerable<DriveInfo>> _getDrives;
        private readonly Func<string, bool> _directoryExists;

        public GoogleDriveDesktopDetector(
            Func<IEnumerable<DriveInfo>>? getDrives = null,
            Func<string, bool>? directoryExists = null)
        {
            _getDrives = getDrives ?? (() => DriveInfo.GetDrives());
            _directoryExists = directoryExists ?? Directory.Exists;
        }

        public bool IsInstalled => DetectMountedPath() is not null || DetectInstalledMarker();

        public string? MountedDrivePath => DetectMountedPath();

        public string? DefaultSyncFolderPath
        {
            get
            {
                string? mounted = MountedDrivePath;
                return mounted is not null ? Path.Combine(mounted, "GameSaves") : null;
            }
        }

        private string? DetectMountedPath()
        {
            try
            {
                // 1. Check known default mount paths on Windows
                string defaultMyDrive = @"G:\My Drive";
                if (_directoryExists(defaultMyDrive))
                    return defaultMyDrive;

                string defaultG = @"G:\";
                if (_directoryExists(defaultG))
                    return defaultG;

                // 2. Enumerate system drives looking for Google Drive / DriveFS
                foreach (DriveInfo drive in _getDrives())
                {
                    if (!drive.IsReady)
                        continue;

                    bool isDriveFs = string.Equals(drive.DriveFormat, "DriveFS", StringComparison.OrdinalIgnoreCase);
                    bool hasDriveLabel = !string.IsNullOrWhiteSpace(drive.VolumeLabel) &&
                        drive.VolumeLabel.Contains("Google Drive", StringComparison.OrdinalIgnoreCase);

                    if (isDriveFs || hasDriveLabel)
                    {
                        string root = drive.RootDirectory.FullName;
                        string candidateMyDrive = Path.Combine(root, "My Drive");
                        if (_directoryExists(candidateMyDrive))
                            return candidateMyDrive;

                        return root;
                    }
                }

                // 3. User home folder fallback
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(userProfile))
                {
                    string userGoogleDrive = Path.Combine(userProfile, "Google Drive");
                    if (_directoryExists(userGoogleDrive))
                        return userGoogleDrive;
                }
            }
            catch
            {
                // File-system and drive inspection is best-effort and must never throw.
            }

            return null;
        }

        private bool DetectInstalledMarker()
        {
            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrWhiteSpace(localAppData))
                {
                    string driveFsDir = Path.Combine(localAppData, "Google", "DriveFS");
                    if (_directoryExists(driveFsDir))
                        return true;
                }
            }
            catch
            {
            }

            return false;
        }
    }
}
