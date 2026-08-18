using System.Text;

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
        private const string Redacted = "[redacted]";

        public string DisplayRoot =>
            $"sftp://{Username}@{Host}:{Port}{(RemotePath.StartsWith('/') ? RemotePath : "/" + RemotePath)}";

        /// <summary>
        /// A positional record prints every member from its generated
        /// <see cref="object.ToString"/>, so interpolating these settings into a
        /// log line or an exception message would emit the password, the key
        /// passphrase, and the key path in clear text. Print the connection
        /// identity and redact the rest. Equality and hashing still consider
        /// every member, so redaction cannot mask a real difference.
        /// </summary>
        private bool PrintMembers(StringBuilder builder)
        {
            builder.Append($"{nameof(Host)} = {Host}, ");
            builder.Append($"{nameof(Port)} = {Port}, ");
            builder.Append($"{nameof(Username)} = {Username}, ");
            builder.Append($"{nameof(AuthMethod)} = {AuthMethod}, ");
            builder.Append($"{nameof(RemotePath)} = {RemotePath}, ");
            builder.Append($"{nameof(TrustNewHostKey)} = {TrustNewHostKey}, ");
            builder.Append($"{nameof(Password)} = {Describe(Password)}, ");
            builder.Append($"{nameof(PrivateKeyPath)} = {Describe(PrivateKeyPath)}, ");
            builder.Append($"{nameof(PrivateKeyPassphrase)} = {Describe(PrivateKeyPassphrase)}");
            return true;
        }

        /// <summary>
        /// Reports whether a secret was supplied without revealing it, so a
        /// diagnostic can still distinguish "no password" from "wrong password".
        /// </summary>
        private static string Describe(string? secret) =>
            string.IsNullOrEmpty(secret) ? "(none)" : Redacted;
    }
}
