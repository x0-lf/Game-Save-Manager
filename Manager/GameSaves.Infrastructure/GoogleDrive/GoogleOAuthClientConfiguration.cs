using GameSaves.Core.Sync;

namespace GameSaves.Infrastructure.GoogleDrive
{
    internal sealed class GoogleOAuthClientConfiguration
    {
        public GoogleOAuthClientConfiguration(string clientId, string? clientSecret = null)
        {
            ClientId = clientId;
            ClientSecret = clientSecret;
        }

        public string ClientId { get; }
        public string? ClientSecret { get; }

        public override string ToString() => nameof(GoogleOAuthClientConfiguration);
    }

    internal sealed record GoogleOAuthClientConfigurationReadResult(
        GoogleDriveOAuthClientConfigurationState State,
        GoogleOAuthClientConfiguration? Configuration);

    internal interface IGoogleOAuthClientConfigurationProvider
    {
        GoogleOAuthClientConfigurationReadResult Read();
    }

    internal sealed class EnvironmentGoogleOAuthClientConfigurationProvider
        : IGoogleOAuthClientConfigurationProvider
    {
        internal const string ClientIdVariable = "GAMESAVES_GOOGLE_CLIENT_ID";
        internal const string ClientSecretVariable = "GAMESAVES_GOOGLE_CLIENT_SECRET";
        private readonly Func<string, string?> _readProcessEnvironmentVariable;
        private readonly Func<string, string?> _readUserEnvironmentVariable;

        public EnvironmentGoogleOAuthClientConfigurationProvider()
            : this(
                Environment.GetEnvironmentVariable,
                ReadWindowsUserEnvironmentVariable)
        {
        }

        internal EnvironmentGoogleOAuthClientConfigurationProvider(
            Func<string, string?> readEnvironmentVariable)
            : this(readEnvironmentVariable, _ => null)
        {
        }

        internal EnvironmentGoogleOAuthClientConfigurationProvider(
            Func<string, string?> readProcessEnvironmentVariable,
            Func<string, string?> readUserEnvironmentVariable)
        {
            _readProcessEnvironmentVariable = readProcessEnvironmentVariable;
            _readUserEnvironmentVariable = readUserEnvironmentVariable;
        }

        public GoogleOAuthClientConfigurationReadResult Read()
        {
            string? clientId = ReadValue(ClientIdVariable);

            if (string.IsNullOrWhiteSpace(clientId))
            {
                return Missing(
                    "Set GAMESAVES_GOOGLE_CLIENT_ID before connecting Google Drive.");
            }

            if (!IsValidClientId(clientId))
            {
                return Invalid(
                    "The configured Google OAuth desktop Client ID is invalid.");
            }

            string? clientSecret = ReadValue(ClientSecretVariable);

            if (clientSecret is not null && LooksLikePlaceholder(clientSecret))
            {
                return Invalid(
                    "The configured Google OAuth desktop client secret is invalid.");
            }

            return new GoogleOAuthClientConfigurationReadResult(
                new GoogleDriveOAuthClientConfigurationState(
                    GoogleDriveOAuthClientConfigurationStatus.Available),
                new GoogleOAuthClientConfiguration(clientId, clientSecret));
        }

        private string? ReadValue(string name)
        {
            string? value = Normalize(_readProcessEnvironmentVariable(name));
            return value ?? Normalize(_readUserEnvironmentVariable(name));
        }

        private static string? Normalize(string? value)
        {
            string? normalized = value?.Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        private static string? ReadWindowsUserEnvironmentVariable(string name) =>
            OperatingSystem.IsWindows()
                ? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
                : null;

        private static bool IsValidClientId(string value) =>
            value.Length <= 512 &&
            !LooksLikePlaceholder(value) &&
            value.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal) &&
            value[..^".apps.googleusercontent.com".Length].All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

        private static bool LooksLikePlaceholder(string value) =>
            value.Contains("YOUR_", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase) ||
            value.Contains('<') || value.Contains('>');

        private static GoogleOAuthClientConfigurationReadResult Missing(string message) =>
            new(
                new GoogleDriveOAuthClientConfigurationState(
                    GoogleDriveOAuthClientConfigurationStatus.Missing,
                    GoogleDriveOAuthErrorCodes.ClientIdMissing,
                    message),
                null);

        private static GoogleOAuthClientConfigurationReadResult Invalid(string message) =>
            new(
                new GoogleDriveOAuthClientConfigurationState(
                    GoogleDriveOAuthClientConfigurationStatus.Invalid,
                    GoogleDriveOAuthErrorCodes.ClientIdInvalid,
                    message),
                null);
    }
}
