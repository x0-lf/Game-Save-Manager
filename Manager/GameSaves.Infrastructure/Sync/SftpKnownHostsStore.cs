using System.Text.Json;

namespace GameSaves.Infrastructure.Sync
{
    /// <summary>
    /// Stores accepted SFTP host-key fingerprints, one per host:port - the
    /// same trust-on-first-use model as OpenSSH's known_hosts. Fingerprints
    /// are public information, not secrets, so plain JSON is appropriate.
    /// </summary>
    internal sealed class SftpKnownHostsStore
    {
        private readonly string _filePath;
        private readonly object _gate = new();

        public SftpKnownHostsStore(string filePath)
        {
            _filePath = filePath;
        }

        public string? GetFingerprint(string host, int port)
        {
            lock (_gate)
            {
                return Load().GetValueOrDefault(MakeKey(host, port));
            }
        }

        public void SaveFingerprint(string host, int port, string fingerprint)
        {
            lock (_gate)
            {
                Dictionary<string, string> entries = Load();
                entries[MakeKey(host, port)] = fingerprint;
                Save(entries);
            }
        }

        public void Forget(string host, int port)
        {
            lock (_gate)
            {
                Dictionary<string, string> entries = Load();

                if (entries.Remove(MakeKey(host, port)))
                    Save(entries);
            }
        }

        private static string MakeKey(string host, int port)
        {
            return $"{host.Trim().ToLowerInvariant()}:{port}";
        }

        private Dictionary<string, string> Load()
        {
            try
            {
                if (!File.Exists(_filePath))
                    return new Dictionary<string, string>();

                return JsonSerializer.Deserialize<Dictionary<string, string>>(
                           File.ReadAllText(_filePath))
                       ?? new Dictionary<string, string>();
            }
            catch
            {
                // An unreadable store is treated as empty; the user simply
                // re-trusts hosts on the next connect.
                return new Dictionary<string, string>();
            }
        }

        private void Save(Dictionary<string, string> entries)
        {
            string? directory = Path.GetDirectoryName(_filePath);

            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(
                _filePath,
                JsonSerializer.Serialize(
                    entries,
                    new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
