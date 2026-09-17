namespace GameSaves.Core.Sync;

/// <summary>
/// Detects local installations and mounted filesystem endpoints for the
/// official Google Drive for Desktop application.
/// </summary>
public interface IGoogleDriveDesktopDetector
{
    /// <summary>
    /// Indicates whether Google Drive for Desktop is detected on the local system.
    /// </summary>
    bool IsInstalled { get; }

    /// <summary>
    /// Gets the root path of the mounted Google Drive virtual filesystem (e.g. "G:\My Drive" or "G:\"),
    /// or <c>null</c> if not mounted or detected.
    /// </summary>
    string? MountedDrivePath { get; }

    /// <summary>
    /// Gets the recommended default sync folder inside the mounted Google Drive (e.g. "G:\My Drive\GameSaves"),
    /// or <c>null</c> if not mounted or detected.
    /// </summary>
    string? DefaultSyncFolderPath { get; }
}
