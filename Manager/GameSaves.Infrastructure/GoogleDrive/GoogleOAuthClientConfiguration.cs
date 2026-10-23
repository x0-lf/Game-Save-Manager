using System.Reflection;
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

    // Reads the client from the environment first (a developer's own client:
    // the process value, then the Windows user value) and otherwise from the
    // public desktop client id baked into release builds (OBS-027): the
    // assembly metadata "GoogleDesktopClientId" that build/publish-release.ps1
    // sets through the MSBuild property of the same name, empty in development
    // and test builds. The optional client secret is read only together with
    // an environment client id, because it belongs to that developer client;
    // the built-in public client is used without one.
    internal sealed class EnvironmentGoogleOAuthClientConfigurationProvider
        : IGoogleOAuthClientConfigurationProvider
    {
        internal const string ClientIdVariable = "GAMESAVES_GOOGLE_CLIENT_ID";
        internal const string ClientSecretVariable = "GAMESAVES_GOOGLE_CLIENT_SECRET";
        internal const string BuiltInClientIdMetadataKey = "GoogleDesktopClientId";
        private readonly Func<string, string?> _readProcessEnvironmentVariable;
        private readonly Func<string, string?> _readUserEnvironmentVariable;
        private readonly Func<string?> _readBuiltInClientId;

        public EnvironmentGoogleOAuthClientConfigurationProvider()
            : this(
                Environment.GetEnvironmentVariable,
                ReadWindowsUserEnvironmentVariable,
                ReadBuiltInClientId)
        {
        }

        internal EnvironmentGoogleOAuthClientConfigurationProvider(
            Func<string, string?> readEnvironmentVariable)
            : this(readEnvironmentVariable, _ => null)
        {
        }

        internal EnvironmentGoogleOAuthClientConfigurationProvider(
            Func<string, string?> readProcessEnvironmentVariable,
            Func<string, string?> readUserEnvironmentVariable,
            Func<string?>? readBuiltInClientId = null)
        {
            _readProcessEnvironmentVariable = readProcessEnvironmentVariable;
            _readUserEnvironmentVariable = readUserEnvironmentVariable;
            _readBuiltInClientId = readBuiltInClientId ?? (() => null);
        }

        public GoogleOAuthClientConfigurationReadResult Read()
        {
            string? environmentClientId = ReadValue(ClientIdVariable);
            string? clientId = environmentClientId ?? Normalize(_readBuiltInClientId());

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

            string? clientSecret = environmentClientId is null ? null : ReadValue(ClientSecretVariable);

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

        /// <summary>The client id this build carries, raw: empty in development and test builds.</summary>
        internal static string? ReadBuiltInClientId() =>
            typeof(EnvironmentGoogleOAuthClientConfigurationProvider).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == BuiltInClientIdMetadataKey)?.Value;

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
