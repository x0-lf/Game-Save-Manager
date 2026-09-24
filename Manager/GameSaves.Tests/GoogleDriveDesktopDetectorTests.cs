using GameSaves.Infrastructure.GoogleDrive;

namespace GameSaves.Tests;

public sealed class GoogleDriveDesktopDetectorTests
{
    [Fact]
    public void MountedDrivePath_WhenDriveFsVolumeHasMyDrive_ReturnsMyDrive()
    {
        var detector = new GoogleDriveDesktopDetector(
            getDrives: () => [(@"C:\", "NTFS", "Windows"), (@"G:\", "DriveFS", "")],
            directoryExists: path => path == @"G:\My Drive");

        Assert.Equal(@"G:\My Drive", detector.MountedDrivePath);
        Assert.Equal(@"G:\My Drive\GameSaves", detector.DefaultSyncFolderPath);
    }

    [Fact]
    public void MountedDrivePath_WhenVolumeIsLabelledGoogleDrive_ReturnsItsRoot()
    {
        var detector = new GoogleDriveDesktopDetector(
            getDrives: () => [(@"H:\", "FAT32", "Google Drive")],
            directoryExists: _ => false);

        Assert.Equal(@"H:\", detector.MountedDrivePath);
    }

    // A USB stick or second disk on G:, or a leftover "Google Drive" folder,
    // is not a Drive mount: nothing uploads what is written there.
    [Fact]
    public void MountedDrivePath_IgnoresPlainGDriveAndLegacyHomeFolder()
    {
        var detector = new GoogleDriveDesktopDetector(
            getDrives: () => [(@"G:\", "NTFS", "USB")],
            directoryExists: _ => true);

        Assert.Null(detector.MountedDrivePath);
        Assert.Null(detector.DefaultSyncFolderPath);
    }

    [Fact]
    public void MountedDrivePath_WhenGetDrivesThrows_ReturnsNullGracefully()
    {
        var detector = new GoogleDriveDesktopDetector(
            getDrives: () => throw new IOException("Disk error"),
            directoryExists: _ => false);

        Assert.Null(detector.MountedDrivePath);
    }

    [Fact]
    public void MountedDrivePath_ProbesTheDisksOnlyOnce()
    {
        int scans = 0;
        var detector = new GoogleDriveDesktopDetector(
            getDrives: () =>
            {
                scans++;
                return [(@"G:\", "DriveFS", "")];
            },
            directoryExists: _ => false);

        _ = detector.MountedDrivePath;
        _ = detector.DefaultSyncFolderPath;
        _ = detector.MountedDrivePath;

        Assert.Equal(1, scans);
    }

    [Fact]
    public void DefaultConstructor_UsesSystemDelegatesWithoutThrowing()
    {
        var detector = new GoogleDriveDesktopDetector();

        // Must evaluate properties without throwing exceptions on any environment
        _ = detector.MountedDrivePath;
        _ = detector.DefaultSyncFolderPath;
    }
}
