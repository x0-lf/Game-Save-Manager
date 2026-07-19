namespace GameSaves.Core.Sync
{
    public enum SftpAuthMethod
    {
        Password = 0,
        PrivateKey = 1
    }

    /// <summary>
    /// Connection settings for an SFTP sync remote. Password and passphrase
    /// are session-only: they are entered in the UI, held in memory, and are
    /// never persisted anywhere.
    /// </summary>
    public sealed record SftpConnectionSettings(
        string Host,
        int Port,
        string Username,
        SftpAuthMethod AuthMethod,
        string? Password,
        string? PrivateKeyPath,
        string? PrivateKeyPassphrase,
        string RemotePath,
        bool TrustNewHostKey)
    {
        public string DisplayRoot =>
            $"sftp://{Username}@{Host}:{Port}{(RemotePath.StartsWith('/') ? RemotePath : "/" + RemotePath)}";
    }
}
