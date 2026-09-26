using System.Text.Json;
using GameSaves.Core.Sync;

namespace GameSaves.Infrastructure.WebDav
{
    /// <summary>
    /// The protected secret for a WebDAV profile: the password together with
    /// the server origin (scheme, host, port) it was entered for. Editing a
    /// profile to point at another server must never send the old password
    /// there, so a password stored for a different origin reads as absent.
    /// </summary>
    internal static class WebDavStoredCredential
    {
        public static byte[] Create(string serverUrl, string password) =>
            JsonSerializer.SerializeToUtf8Bytes(new Payload(Origin(serverUrl), password));

        public static string? ReadPassword(byte[] stored, string serverUrl)
        {
            try
            {
                Payload? payload = JsonSerializer.Deserialize<Payload>(stored);

                return payload is { Password.Length: > 0 } &&
                       string.Equals(payload.Origin, Origin(serverUrl), StringComparison.OrdinalIgnoreCase)
                    ? payload.Password
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public static string Origin(string serverUrl) =>
            new Uri(WebDavSyncRemoteSettings.NormalizeServerUrl(serverUrl))
                .GetLeftPart(UriPartial.Authority);

        private sealed record Payload(string Origin, string Password);
    }
}
