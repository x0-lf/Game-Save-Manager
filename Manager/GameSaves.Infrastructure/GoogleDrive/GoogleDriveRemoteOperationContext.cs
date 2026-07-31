using GameSaves.Core.Sync;

namespace GameSaves.Infrastructure.GoogleDrive
{
    internal interface IGoogleDriveRemoteOperationContextFactory
    {
        Task<GoogleDriveRemoteOperationContext> CreateAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Owns the authenticated, profile-scoped state required by one Google
    /// Drive remote-filesystem operation. The context owns and disposes its
    /// short-lived credential; it performs no Drive request itself.
    /// </summary>
    internal sealed class GoogleDriveRemoteOperationContext : IDisposable
    {
        private GoogleAuthorizedCredential? _credential;
        private readonly IGoogleDriveObjectPathResolver _resolver;

        internal GoogleDriveRemoteOperationContext(
            Guid remoteProfileId,
            string rootFolderId,
            GoogleAuthorizedCredential credential,
            IGoogleDriveObjectPathResolver resolver)
        {
            if (remoteProfileId == Guid.Empty)
            {
                throw new ArgumentException(
                    "A saved remote profile ID is required.",
                    nameof(remoteProfileId));
            }
            if (string.IsNullOrWhiteSpace(rootFolderId))
            {
                throw new ArgumentException(
                    "An authoritative Google Drive root-folder ID is required.",
                    nameof(rootFolderId));
            }

            RemoteProfileId = remoteProfileId;
            RootFolderId = rootFolderId;
            _credential = credential ??
                throw new ArgumentNullException(nameof(credential));
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        }

        public Guid RemoteProfileId { get; }

        public string RootFolderId { get; }

        public GoogleAuthorizedCredential Credential
        {
            get
            {
                ObjectDisposedException.ThrowIf(_credential is null, this);
                return _credential;
            }
        }

        public IGoogleDriveObjectPathResolver Resolver
        {
            get
            {
                ObjectDisposedException.ThrowIf(_credential is null, this);
                return _resolver;
            }
        }

        internal bool IsDisposed => _credential is null;

        public void Dispose()
        {
            GoogleAuthorizedCredential? credential = Interlocked.Exchange(
                ref _credential,
                null);
            credential?.Dispose();
        }
    }

    /// <summary>
    /// Safe failure boundary for creating an authenticated operation context.
    /// The embedded result uses the existing validation taxonomy and contains
    /// no profile name, account data, credential, or Drive object ID.
    /// </summary>
    internal sealed class GoogleDriveRemoteOperationContextException : Exception
    {
        public GoogleDriveRemoteOperationContextException(
            GoogleDriveRemoteValidationResult result)
            : base("A Google Drive remote operation could not be started.")
        {
            ArgumentNullException.ThrowIfNull(result);
            if (result.Status == GoogleDriveRemoteValidationStatus.Valid)
            {
                throw new ArgumentException(
                    "A context failure cannot contain a valid result.",
                    nameof(result));
            }

            Result = result;
        }

        public GoogleDriveRemoteValidationResult Result { get; }
    }

    /// <summary>
    /// Loads one saved Google Drive profile, restores its protected
    /// authentication silently, and creates a credential-scoped resolver.
    /// No browser, root validation, listing, content access, or mutation is
    /// performed while creating the context.
    /// </summary>
    internal sealed class GoogleDriveRemoteOperationContextFactory
        : IGoogleDriveRemoteOperationContextFactory
    {
        private readonly ISyncRemoteProfileRepository _profileRepository;
        private readonly IGoogleDriveAuthorizedSessionFactory _sessionFactory;
        private readonly IGoogleDriveObjectPathResolverFactory _resolverFactory;

        public GoogleDriveRemoteOperationContextFactory(
            ISyncRemoteProfileRepository profileRepository,
            IGoogleDriveAuthorizedSessionFactory sessionFactory,
            IGoogleDriveObjectPathResolverFactory resolverFactory)
        {
            _profileRepository = profileRepository ??
                throw new ArgumentNullException(nameof(profileRepository));
            _sessionFactory = sessionFactory ??
                throw new ArgumentNullException(nameof(sessionFactory));
            _resolverFactory = resolverFactory ??
                throw new ArgumentNullException(nameof(resolverFactory));
        }

        public async Task<GoogleDriveRemoteOperationContext> CreateAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            if (remoteProfileId == Guid.Empty)
                throw Failure(GoogleDriveRemoteValidationStatus.ProfileNotFound);

            cancellationToken.ThrowIfCancellationRequested();

            SyncRemoteProfile? profile;
            try
            {
                profile = _profileRepository.GetById(remoteProfileId);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                throw Failure(GoogleDriveRemoteValidationStatus.Failed);
            }

            if (profile is null)
                throw Failure(GoogleDriveRemoteValidationStatus.ProfileNotFound);

            GoogleDriveRemoteValidationResult? profileFailure =
                GoogleDriveRemoteProfileValidator.Validate(profile);
            if (profileFailure is not null)
                throw new GoogleDriveRemoteOperationContextException(profileFailure);

            GoogleDriveAuthorizedSession session;
            try
            {
                session = await _sessionFactory.RestoreAsync(
                    profile,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (GoogleDriveAuthorizedSessionException ex)
            {
                throw new GoogleDriveRemoteOperationContextException(
                    GoogleDriveRemoteValidationMapper.FromSessionFailure(ex.Failure));
            }
            catch
            {
                throw Failure(GoogleDriveRemoteValidationStatus.Failed);
            }

            GoogleAuthorizedCredential credential = session.Credential;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                IGoogleDriveObjectPathResolver resolver = _resolverFactory.Create(
                    profile.Id,
                    credential);

                return new GoogleDriveRemoteOperationContext(
                    profile.Id,
                    profile.RemoteFolderId!,
                    credential,
                    resolver);
            }
            catch (OperationCanceledException)
            {
                credential.Dispose();
                throw;
            }
            catch
            {
                credential.Dispose();
                throw Failure(GoogleDriveRemoteValidationStatus.Failed);
            }
        }

        private static GoogleDriveRemoteOperationContextException Failure(
            GoogleDriveRemoteValidationStatus status) =>
            new(GoogleDriveRemoteValidationMapper.FromStatus(status));
    }

    /// <summary>
    /// One profile/settings/root preflight shared by validation and all future
    /// authenticated remote operations.
    /// </summary>
    internal static class GoogleDriveRemoteProfileValidator
    {
        public static GoogleDriveRemoteValidationResult? Validate(
            SyncRemoteProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);

            if (profile.ProviderKind != SyncProviderKind.GoogleDrive)
            {
                return GoogleDriveRemoteValidationMapper.FromStatus(
                    GoogleDriveRemoteValidationStatus.WrongProviderKind);
            }

            if (profile.SettingsError is not null ||
                profile.ProviderSettings is not GoogleDriveSyncRemoteSettings settings ||
                settings.SchemaVersion != GoogleDriveSyncRemoteSettings.CurrentSchemaVersion ||
                !string.Equals(
                    settings.RequestedScope,
                    GoogleDriveAuthorizationScopes.DriveFile,
                    StringComparison.Ordinal))
            {
                return GoogleDriveRemoteValidationMapper.FromStatus(
                    GoogleDriveRemoteValidationStatus.UnsupportedScope);
            }

            if (string.IsNullOrWhiteSpace(profile.RemoteFolderId))
            {
                return GoogleDriveRemoteValidationMapper.FromStatus(
                    GoogleDriveRemoteValidationStatus.RootNotConfigured);
            }

            return null;
        }
    }
}
