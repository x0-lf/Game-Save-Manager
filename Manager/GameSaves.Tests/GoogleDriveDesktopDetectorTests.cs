using GameSaves.Core.Sync;
using GameSaves.Infrastructure.GoogleDrive;

namespace GameSaves.Tests;

public sealed class GoogleDriveDesktopDetectorTests
{
    [Fact]
    public void MountedDrivePath_WhenDefaultMyDriveExists_ReturnsDefaultMyDrive()
    {
        var detector = new GoogleDriveDesktopDetector(
            getDrives: () => [],
            directoryExists: path => path == @"G:\My Drive");

        Assert.Equal(@"G:\My Drive", detector.MountedDrivePath);
        Assert.True(detector.IsInstalled);
        Assert.Equal(@"G:\My Drive\GameSaves", detector.DefaultSyncFolderPath);
    }

    [Fact]
    public void MountedDrivePath_WhenDefaultGExists_ReturnsDefaultG()
    {
        var detector = new GoogleDriveDesktopDetector(
            getDrives: () => [],
            directoryExists: path => path == @"G:\");

        Assert.Equal(@"G:\", detector.MountedDrivePath);
        Assert.True(detector.IsInstalled);
        Assert.Equal(@"G:\GameSaves", detector.DefaultSyncFolderPath);
    }

    [Fact]
    public void MountedDrivePath_WhenUserHomeFolderExists_ReturnsUserHomeGoogleDrive()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string expected = Path.Combine(userProfile, "Google Drive");

        var detector = new GoogleDriveDesktopDetector(
            getDrives: () => [],
            directoryExists: path => path.Equals(expected, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(expected, detector.MountedDrivePath);
        Assert.True(detector.IsInstalled);
        Assert.Equal(Path.Combine(expected, "GameSaves"), detector.DefaultSyncFolderPath);
    }

    [Fact]
    public void MountedDrivePath_WhenNoDriveOrFolderExists_ReturnsNull()
    {
        var detector = new GoogleDriveDesktopDetector(
            getDrives: () => [],
            directoryExists: _ => false);

        Assert.Null(detector.MountedDrivePath);
        Assert.Null(detector.DefaultSyncFolderPath);
    }

    [Fact]
    public void IsInstalled_WhenMarkerDirectoryExists_ReturnsTrueEvenIfUnmounted()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string markerDir = Path.Combine(localAppData, "Google", "DriveFS");

        var detector = new GoogleDriveDesktopDetector(
            getDrives: () => [],
            directoryExists: path => path.Equals(markerDir, StringComparison.OrdinalIgnoreCase));

        Assert.Null(detector.MountedDrivePath);
        Assert.True(detector.IsInstalled);
    }

    [Fact]
    public void IsInstalled_WhenNeitherMountedNorMarkerExists_ReturnsFalse()
    {
        var detector = new GoogleDriveDesktopDetector(
            getDrives: () => [],
            directoryExists: _ => false);

        Assert.False(detector.IsInstalled);
    }

    [Fact]
    public void MountedDrivePath_WhenGetDrivesThrows_ReturnsNullGracefully()
    {
        var detector = new GoogleDriveDesktopDetector(
            getDrives: () => throw new IOException("Disk error"),
            directoryExists: _ => false);

        Assert.Null(detector.MountedDrivePath);
        Assert.False(detector.IsInstalled);
    }

    [Fact]
    public void DefaultConstructor_UsesSystemDelegatesWithoutThrowing()
    {
        var detector = new GoogleDriveDesktopDetector();

        // Must evaluate properties without throwing exceptions on any environment
        _ = detector.IsInstalled;
        _ = detector.MountedDrivePath;
        _ = detector.DefaultSyncFolderPath;
    }
}
