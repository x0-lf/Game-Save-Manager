namespace GameSaves.Core.Updates
{
    /// <summary>
    /// One published release as the update feed describes it: the version to
    /// compare with the running one, the installer asset and its size, the
    /// SHA-256 digest published beside it, and the page with the release notes.
    /// </summary>
    public sealed record UpdateRelease(
        Version Version,
        string InstallerName,
        Uri InstallerUri,
        long InstallerBytes,
        Uri DigestUri,
        Uri ReleaseNotesUri);

    /// <summary>
    /// An installer that is on disk only because its SHA-256 matched the
    /// published digest. <see cref="SignatureSummary"/> describes its
    /// Authenticode state in words; it is shown, never required, because the
    /// packages are unsigned until a certificate exists (RELEASE-001).
    /// </summary>
    public sealed record VerifiedUpdate(string FilePath, string SignatureSummary);

    /// <summary>
    /// Reads the release feed. Null means no release has been published. A
    /// feed that cannot be reached or does not describe a usable release
    /// throws, and the caller reports the message in words and offers nothing.
    /// Nothing calls this unless the user opted in or pressed Check now, and
    /// the headless scheduled-run host never constructs a caller.
    /// </summary>
    public interface IUpdateFeed
    {
        Task<UpdateRelease?> GetLatestAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Downloads a release's installer into the updater's own staging folder,
    /// hashing while writing, and keeps the file only when its SHA-256 matches
    /// the published digest. A mismatched, truncated or cancelled download is
    /// deleted before the call returns or throws, so a file this returns is
    /// the only kind that can ever be handed to the installer.
    /// </summary>
    public interface IUpdateDownloader
    {
        Task<VerifiedUpdate> DownloadAsync(
            UpdateRelease release,
            IProgress<long>? bytesWritten,
            CancellationToken cancellationToken = default);
    }
}
