using System.Security.Cryptography;

namespace GameSaves.Infrastructure.Transfers
{
    /// <summary>
    /// The one SHA-256 routine behind every manifest hash: backup writers,
    /// restore integrity checks and payload verification all compare the same
    /// upper-case hex form.
    /// </summary>
    internal static class Sha256Hasher
    {
        public static string HashFile(string filePath)
        {
            using FileStream stream = File.OpenRead(filePath);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        /// <summary>
        /// Hashes a stream that may yield more bytes than it claims. An archive entry's
        /// declared size is written by whoever built the archive, so a hostile one can
        /// under-declare and inflate without limit. Returns null as soon as the stream
        /// passes <paramref name="maxBytes"/>, having read at most one buffer beyond it.
        /// </summary>
        public static string? HashBounded(Stream stream, long maxBytes, CancellationToken cancellationToken)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[81920];
            long total = 0;
            int read;

            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                total += read;
                if (total > maxBytes)
                    return null;

                hash.AppendData(buffer, 0, read);
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }
    }
}
