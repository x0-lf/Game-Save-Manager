using System.ComponentModel;
using GameSaves.Infrastructure.GoogleDrive;
using GameSaves.Infrastructure.OneDrive;
using Google.Apis.Util.Store;
using Microsoft.Extensions.DependencyInjection;

namespace GameSaves.Infrastructure.DependencyInjection
{
    public static class UnattendedServiceCollectionExtensions
    {
        /// <summary>
        /// For a host that runs with nobody at the keyboard, such as a scheduled
        /// task. Stored sign-ins still refresh silently, but every path that
        /// would open a browser or wait for a sign-in callback fails at once,
        /// and each provider reports it as "the browser could not be opened".
        /// Call after <c>AddGameSavesInfrastructure</c>: the last registration wins.
        /// </summary>
        public static IServiceCollection AddUnattendedSignInGuards(this IServiceCollection services)
        {
            services.AddSingleton<IGoogleInstalledAppAuthorizer>(
                new NoBrowserGoogleAuthorizer(new GoogleInstalledAppAuthorizer()));
            services.AddSingleton<IOneDriveInteractiveAuthorizer, NoBrowserOneDriveAuthorizer>();

            return services;
        }

        private sealed class NoBrowserGoogleAuthorizer(IGoogleInstalledAppAuthorizer inner)
            : IGoogleInstalledAppAuthorizer
        {
            public Task<GoogleAuthorizedCredential> ConnectAsync(
                GoogleOAuthClientConfiguration configuration,
                Guid profileId,
                IDataStore dataStore,
                IReadOnlyList<string> scopes,
                CancellationToken cancellationToken) =>
                Task.FromException<GoogleAuthorizedCredential>(
                    new GoogleAuthorizationException(GoogleAuthorizationFailure.BrowserFailed));

            public Task<GoogleAuthorizedCredential?> RestoreAsync(
                GoogleOAuthClientConfiguration configuration,
                Guid profileId,
                IDataStore dataStore,
                IReadOnlyList<string> scopes,
                CancellationToken cancellationToken) =>
                inner.RestoreAsync(configuration, profileId, dataStore, scopes, cancellationToken);
        }

        private sealed class NoBrowserOneDriveAuthorizer : IOneDriveInteractiveAuthorizer
        {
            public Task<string> AuthorizeAsync(
                string authorizationUrl,
                string redirectUri,
                string expectedState,
                CancellationToken cancellationToken = default) =>
                Task.FromException<string>(
                    new Win32Exception("An unattended run never opens a browser."));
        }
    }
}
