using System.Collections.Generic;

namespace GameSaves.Core.Transfers
{
    /// <summary>
    /// Standardized verification strength levels for backup runs and transfer operations.
    /// Distinguishes between unverified copies, manifest-only metadata comparisons (sidecar vs embedded),
    /// and full cryptographic payload hash verification.
    /// </summary>
    public enum VerificationStrength
    {
        /// <summary>No verification has been performed or requested.</summary>
        None = 0,

        /// <summary>
        /// The backup files or containers were copied or written, but neither side
        /// has been re-queried or matched against descriptors after the transfer.
        /// </summary>
        Copied = 1,

        /// <summary>
        /// The destination manifest was matched using an unauthenticated sidecar descriptor
        /// (.manifest.json) beside an archive. The container payload and embedded manifest
        /// have not been verified.
        /// </summary>
        SidecarManifestMatch = 2,

        /// <summary>
        /// Manifests on both sides match on file counts, file sizes, timestamps, relative paths,
        /// and recorded SHA-256 hashes, based on directory or embedded archive manifests.
        /// Payload bytes were not re-read or re-hashed.
        /// </summary>
        ManifestMatch = 3,

        /// <summary>
        /// All payload files or archive entries were read and verified byte-for-byte
        /// against their recorded cryptographic SHA-256 hashes.
        /// </summary>
        PayloadVerified = 4,

        /// <summary>
        /// Manifests exist on both sides but disagree on file count, sizes, timestamps, or recorded hashes.
        /// </summary>
        ManifestMismatch = 5,

        /// <summary>
        /// A payload file or archive entry failed cryptographic SHA-256 verification (corrupted or tampered).
        /// </summary>
        PayloadMismatch = 6,

        /// <summary>
        /// The remote endpoint could not be reached or queried during verification.
        /// </summary>
        EndpointUnavailable = 7,

        /// <summary>
        /// The backup run is missing locally during verification.
        /// </summary>
        MissingLocally = 8,

        /// <summary>
        /// The backup run is missing on the remote side during verification.
        /// </summary>
        MissingRemotely = 9,

        /// <summary>
        /// The backup run is missing on both sides during verification.
        /// </summary>
        MissingBothSides = 10,

        /// <summary>
        /// Verification was cancelled by the user.
        /// </summary>
        Cancelled = 11
    }

    /// <summary>
    /// The result of an integrity or verification check on a backup run or payload.
    /// </summary>
    public sealed record VerificationStrengthResult(
        VerificationStrength Strength,
        string? Error = null,
        int VerifiedFiles = 0,
        int TotalFiles = 0,
        IReadOnlyDictionary<string, bool>? FileResults = null)
    {
        public static VerificationStrengthResult Success(
            VerificationStrength strength,
            int verifiedFiles,
            int totalFiles,
            IReadOnlyDictionary<string, bool>? fileResults = null) =>
            new(strength, null, verifiedFiles, totalFiles, fileResults);

        public static VerificationStrengthResult Failure(
            VerificationStrength strength,
            string error,
            int verifiedFiles = 0,
            int totalFiles = 0,
            IReadOnlyDictionary<string, bool>? fileResults = null) =>
            new(strength, error, verifiedFiles, totalFiles, fileResults);
    }
}
