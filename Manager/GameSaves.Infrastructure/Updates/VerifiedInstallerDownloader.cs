using GameSaves.Core.Platform;
using GameSaves.Core.Sync;
using GameSaves.Core.Updates;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace GameSaves.Infrastructure.Updates
{
    /// <summary>
    /// Downloads an installer into <c>updates\</c> beside the database, the
    /// only place in the data folder the updater owns. The SHA-256 is computed
    /// while the bytes are written to <c>&lt;name&gt;.partial</c>; the file is
    /// renamed to its final name only when the digest matches what the release
    /// published, and on any other outcome (mismatch, short download, cancel,
    /// network failure) the partial file is deleted before the exception
    /// leaves. The only other thing this class ever deletes is a
    /// <c>*.partial</c> in that folder that has not been written to for a day,
    /// the same rule the transfer engine applies to its own staging. Nothing
    /// else in the data folder is listed or touched.
    /// </summary>
    public sealed class VerifiedInstallerDownloader : IUpdateDownloader
    {
        public const string StagingFolderName = "updates";
        public const string PartialExtension = ".partial";
        internal static readonly TimeSpan StalePartialAge = TimeSpan.FromDays(1);

        // A digest file is 64 hex characters, optionally followed by the file
        // name as sha256sum writes it; anything larger is not a digest.
        private const int MaxDigestFileBytes = 4096;
        private static readonly Regex HexDigest = new("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);

        private readonly HttpClient _httpClient;
        private readonly IAppDatabasePathProvider _databasePathProvider;
        private readonly IUtcClock _clock;

        public VerifiedInstallerDownloader(
            IAppDatabasePathProvider databasePathProvider,
            IUtcClock clock,
            HttpClient? httpClient = null)
        {
            _databasePathProvider = databasePathProvider ?? throw new ArgumentNullException(nameof(databasePathProvider));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _httpClient = httpClient ?? new HttpClient();
        }

        public async Task<VerifiedUpdate> DownloadAsync(
            UpdateRelease release,
            IProgress<long>? bytesWritten,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(release);

            string expectedDigest = await ReadPublishedDigestAsync(release.DigestUri, cancellationToken)
                .ConfigureAwait(false);

            string staging = Path.Combine(
                Path.GetDirectoryName(_databasePathProvider.GetDatabasePath())!,
                StagingFolderName);
            Directory.CreateDirectory(staging);
            PurgeStalePartials(staging);

            string partialPath = Path.Combine(staging, release.InstallerName + PartialExtension);
            string finalPath = Path.Combine(staging, release.InstallerName);

            try
            {
                long written = 0;
                string actualDigest;

                using (var request = new HttpRequestMessage(HttpMethod.Get, release.InstallerUri))
                {
                    request.Headers.UserAgent.ParseAdd(GitHubReleaseFeed.UserAgent);

                    using HttpResponseMessage response = await _httpClient
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                        .ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    using Stream body = await response.Content
                        .ReadAsStreamAsync(cancellationToken)
                        .ConfigureAwait(false);
                    using var file = new FileStream(
                        partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                    byte[] buffer = new byte[1 << 16];
                    int read;

                    while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        hash.AppendData(buffer, 0, read);
                        written += read;
                        bytesWritten?.Report(written);
                    }

                    actualDigest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                }

                if (release.InstallerBytes > 0 && written != release.InstallerBytes)
                {
                    throw new InvalidDataException(
                        $"The download was incomplete: {written} of {release.InstallerBytes} bytes arrived.");
                }

                if (!string.Equals(actualDigest, expectedDigest, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The downloaded file's SHA-256 does not match the digest published with the release.");
                }

                File.Move(partialPath, finalPath, overwrite: true);
            }
            catch
            {
                TryDelete(partialPath);
                throw;
            }

            MarkAsDownloadedFromTheInternet(finalPath);

            return new VerifiedUpdate(finalPath, DescribeSignature(finalPath));
        }

        /// <summary>
        /// Accepts the digest file as the packaging script writes it,
        /// <c>&lt;64 lowercase hex&gt;  &lt;file name&gt;</c>, and a bare hex
        /// digest; returns it lowercased.
        /// </summary>
        internal static bool TryParseDigest(string? text, out string digest)
        {
            digest = string.Empty;
            string first = (text ?? string.Empty).Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? string.Empty;

            if (!HexDigest.IsMatch(first))
                return false;

            digest = first.ToLowerInvariant();
            return true;
        }

        private async Task<string> ReadPublishedDigestAsync(Uri digestUri, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, digestUri);
            request.Headers.UserAgent.ParseAdd(GitHubReleaseFeed.UserAgent);

            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength > MaxDigestFileBytes)
                throw new InvalidDataException("The published digest file is too large to be a SHA-256 digest.");

            string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (text.Length > MaxDigestFileBytes || !TryParseDigest(text, out string digest))
                throw new InvalidDataException("The published digest file does not contain a SHA-256 digest.");

            return digest;
        }

        private void PurgeStalePartials(string staging)
        {
            foreach (string path in Directory.EnumerateFiles(staging, "*" + PartialExtension))
            {
                if (path.EndsWith(PartialExtension, StringComparison.OrdinalIgnoreCase) &&
                    _clock.UtcNow - File.GetLastWriteTimeUtc(path) > StalePartialAge)
                {
                    TryDelete(path);
                }
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A file still held open is left for the stale-partial purge.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        // The Zone.Identifier stream is what a browser writes, so Windows
        // applies the same SmartScreen and "open file" checks to this installer
        // as to one the user downloaded by hand; without it an unsigned
        // package would start with no warning at all. Best effort: a volume
        // without alternate data streams simply has no mark.
        private static void MarkAsDownloadedFromTheInternet(string path)
        {
            try
            {
                File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        // shortcut: the signer's certificate chain is validated, not the
        // Authenticode hash over the file (that needs WinVerifyTrust); the
        // SHA-256 digest above is the integrity gate. Upgrade when signed
        // packages exist. CreateFromSignedFile is obsoleted in favour of
        // X509CertificateLoader, which has no reader for a signed file's
        // embedded certificate, so the call stays with the warning off.
        private static string DescribeSignature(string path)
        {
            X509Certificate2 certificate;

            try
            {
#pragma warning disable SYSLIB0057
                certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            }
            catch (CryptographicException)
            {
                return "unsigned";
            }
            catch (PlatformNotSupportedException)
            {
                return "signature not checked on this platform";
            }

            using (certificate)
            using (var chain = new X509Chain())
            {
                string signer = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);

                return chain.Build(certificate)
                    ? $"signed by {signer}"
                    : $"signed by {signer}, but the certificate chain is not trusted";
            }
        }
    }
}
