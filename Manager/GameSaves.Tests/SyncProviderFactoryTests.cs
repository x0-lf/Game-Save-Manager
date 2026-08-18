using GameSaves.Core.Platform;
using GameSaves.Core.Secrets;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.DependencyInjection;
using GameSaves.Infrastructure.GoogleDrive;
using GameSaves.Infrastructure.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Reflection;

namespace GameSaves.Tests;

/// <summary>
/// Covers the concrete <see cref="SyncProviderFactory"/> itself. Until
/// Milestone U every test of these three cases went through a test double that
/// implemented <see cref="ISyncProviderFactory"/>, so the real construction
/// path had no direct regression coverage.
/// </summary>
public sealed class SyncProviderFactoryTests
{
    private const string SftpPassword = "sftp-password-marker";
    private const string SftpPassphrase = "sftp-passphrase-marker";
    private const string SftpKeyPath = @"C:\private\key-path-marker\id_rsa";

    // ---- Local folder ----

    [Fact]
    public void CreateLocalFolderProvider_BuildsALocalProviderOverTheGivenRoot()
    {
        using TempDirectory remote = TempDirectory.Create();
        using TempDirectory backups = TempDirectory.Create();

        using ISyncProvider provider =
            Factory(backups.Path).CreateLocalFolderProvider(remote.Path);

        Assert.IsType<LocalFolderSyncProvider>(provider);
        Assert.Equal("Local folder", provider.ProviderName);
        Assert.Equal(remote.Path, provider.RemoteRoot);
    }

    [Fact]
    public void CreateLocalFolderProvider_BuildsOneProviderPerCall()
    {
        using TempDirectory remote = TempDirectory.Create();
        using TempDirectory backups = TempDirectory.Create();
        ISyncProviderFactory factory = Factory(backups.Path);

        using ISyncProvider first = factory.CreateLocalFolderProvider(remote.Path);
        using ISyncProvider second = factory.CreateLocalFolderProvider(remote.Path);

        Assert.NotSame(first, second);
    }

    // ---- SFTP ----

    [Fact]
    public void CreateSftpProvider_BuildsAnSftpProviderWithoutConnecting()
    {
        using TempDirectory backups = TempDirectory.Create();

        using ISyncProvider provider =
            Factory(backups.Path).CreateSftpProvider(SftpSettings());

        Assert.IsType<SftpSyncProvider>(provider);
        Assert.Equal("SFTP", provider.ProviderName);
        Assert.Equal(
            "sftp://backup-user@sftp.example.invalid:2222/gamesave-sync",
            provider.RemoteRoot);
    }

    [Fact]
    public void SftpProviderRemoteRoot_CarriesNoSecret()
    {
        using TempDirectory backups = TempDirectory.Create();

        using ISyncProvider provider =
            Factory(backups.Path).CreateSftpProvider(SftpSettings());

        AssertCarriesNoSftpSecret(provider.RemoteRoot);
    }

    [Fact]
    public void SftpSettings_RedactEverySecretWhenFormatted()
    {
        SftpConnectionSettings settings = SftpSettings();

        // Non-vacuity: the sweep is only meaningful while the settings really
        // hold these values.
        Assert.Equal(SftpPassword, settings.Password);
        Assert.Equal(SftpPassphrase, settings.PrivateKeyPassphrase);
        Assert.Equal(SftpKeyPath, settings.PrivateKeyPath);

        // A positional record prints every member by default, so an
        // interpolated settings object would put the password straight into a
        // log line or an exception message.
        AssertCarriesNoSftpSecret(settings.ToString());
        AssertCarriesNoSftpSecret($"{settings}");
        AssertCarriesNoSftpSecret(settings.DisplayRoot);
    }

    [Fact]
    public void RedactedSftpSettings_StillShowTheNonSecretConnectionIdentity()
    {
        string text = SftpSettings().ToString();

        Assert.Contains("sftp.example.invalid", text, StringComparison.Ordinal);
        Assert.Contains("backup-user", text, StringComparison.Ordinal);
        Assert.Contains("2222", text, StringComparison.Ordinal);
        Assert.Contains("PrivateKey", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SftpSettingsEquality_IsUnchangedByRedactedFormatting()
    {
        // Redaction must not be implemented by dropping members from the
        // record, which would silently break value equality.
        Assert.Equal(SftpSettings(), SftpSettings());
        Assert.NotEqual(
            SftpSettings(),
            SftpSettings() with { Password = "different" });
        Assert.Equal(SftpSettings().GetHashCode(), SftpSettings().GetHashCode());
    }

    // ---- Google Drive ----

    [Fact]
    public void CreateGoogleDriveProvider_RefusesAnEmptyProfileIdBeforeAnyLookup()
    {
        using TempDirectory backups = TempDirectory.Create();
        var repository = new RecordingLookupProfileRepository();

        Assert.Throws<ArgumentException>(
            () => Factory(backups.Path, repository)
                .CreateGoogleDriveProvider(Guid.Empty));

        Assert.Equal(0, repository.LookupCalls);
    }

    [Fact]
    public void CreateGoogleDriveProvider_RefusesAnUnknownProfile()
    {
        using TempDirectory backups = TempDirectory.Create();

        GoogleDriveRemoteOperationException failure =
            Assert.Throws<GoogleDriveRemoteOperationException>(
                () => Factory(backups.Path).CreateGoogleDriveProvider(Guid.NewGuid()));

        Assert.Equal(
            GoogleDriveRemoteValidationStatus.ProfileNotFound,
            failure.Result.Status);
    }

    // ---- Dependency injection ----

    [Fact]
    public void Registration_ResolvesEveryProviderCaseFromTheContainer()
    {
        using ServiceProvider provider = BuildContainer();
        var factory = provider.GetRequiredService<ISyncProviderFactory>();

        Assert.IsType<SyncProviderFactory>(factory);
        Assert.Same(factory, provider.GetRequiredService<ISyncProviderFactory>());

        using TempDirectory remote = TempDirectory.Create();
        using ISyncProvider local = factory.CreateLocalFolderProvider(remote.Path);
        using ISyncProvider sftp = factory.CreateSftpProvider(SftpSettings());

        Assert.Equal("Local folder", local.ProviderName);
        Assert.Equal("SFTP", sftp.ProviderName);

        // The Drive case is reachable through the same seam and refuses before
        // any remote work, which is the only outcome available without an
        // account.
        Assert.Throws<ArgumentException>(
            () => factory.CreateGoogleDriveProvider(Guid.Empty));
    }

    [Fact]
    public void Registration_InjectsOAuthTokenAndSecretStorageIntoTheDriveChain()
    {
        using ServiceProvider provider = BuildContainer();

        // Google Drive authentication must run on injected abstractions, not on
        // types the provider constructs for itself.
        var sessionFactory =
            provider.GetRequiredService<IGoogleDriveAuthorizedSessionFactory>();
        Assert.NotNull(sessionFactory);
        Assert.NotNull(provider.GetRequiredService<ISecretStore>());
        Assert.NotNull(
            provider.GetRequiredService<IGoogleOAuthClientConfigurationProvider>());
        Assert.NotNull(provider.GetRequiredService<IGoogleSecretDataStoreFactory>());
        Assert.NotNull(provider.GetRequiredService<IGoogleInstalledAppAuthorizer>());

        // Each of those reaches the session factory by constructor injection.
        ConstructorInfo constructor = Assert.Single(
            sessionFactory.GetType().GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Type[] injected = constructor
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.Contains(typeof(ISecretStore), injected);
        Assert.Contains(typeof(IGoogleOAuthClientConfigurationProvider), injected);
        Assert.Contains(typeof(IGoogleSecretDataStoreFactory), injected);
        Assert.Contains(typeof(IGoogleInstalledAppAuthorizer), injected);
    }

    [Fact]
    public void Factory_TakesEveryDependencyExplicitlyAndNeverAServiceLocator()
    {
        ConstructorInfo constructor = Assert.Single(
            typeof(SyncProviderFactory).GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));

        Assert.DoesNotContain(
            constructor.GetParameters(),
            parameter => parameter.ParameterType == typeof(IServiceProvider));

        // No presentation-layer type may reach the construction boundary.
        Assert.DoesNotContain(
            constructor.GetParameters(),
            parameter =>
                parameter.ParameterType.Namespace?.StartsWith(
                    "GameSaves.App", StringComparison.Ordinal) == true ||
                parameter.ParameterType.Namespace?.StartsWith(
                    "Avalonia", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Factory_ReferencesNoPresentationAssembly()
    {
        AssemblyName[] referenced =
            typeof(SyncProviderFactory).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(
            referenced,
            assembly =>
                assembly.Name?.StartsWith("Avalonia", StringComparison.Ordinal) == true ||
                assembly.Name?.StartsWith("GameSaves.App", StringComparison.Ordinal) == true);
    }

    // ---- Unsupported and unavailable providers ----

    [Fact]
    public void UnimplementedProviders_StayUnavailableInTheCatalog()
    {
        var catalog = new SyncProviderCatalog();

        foreach (SyncProviderKind kind in
                 new[] { SyncProviderKind.WebDav, SyncProviderKind.OneDrive })
        {
            SyncProviderDescriptor descriptor = catalog.GetDescriptor(kind);

            Assert.False(descriptor.IsImplemented);
            Assert.False(string.IsNullOrWhiteSpace(descriptor.UnavailableMessage));
        }

        Assert.True(catalog.GetDescriptor(SyncProviderKind.LocalFolder).IsImplemented);
        Assert.True(catalog.GetDescriptor(SyncProviderKind.Sftp).IsImplemented);
        Assert.True(catalog.GetDescriptor(SyncProviderKind.GoogleDrive).IsImplemented);
    }

    [Fact]
    public void AnUnknownProviderValue_ResolvesToTheUnknownDescriptor()
    {
        SyncProviderDescriptor descriptor =
            new SyncProviderCatalog().GetDescriptor((SyncProviderKind)9999);

        Assert.Equal(SyncProviderKind.Unknown, descriptor.Kind);
        Assert.False(descriptor.IsImplemented);
    }

    // ---- Invalid configuration ----

    [Fact]
    public void CreateLocalFolderProvider_RejectsAnEmptyRoot()
    {
        using TempDirectory backups = TempDirectory.Create();

        Assert.ThrowsAny<ArgumentException>(
            () => Factory(backups.Path).CreateLocalFolderProvider("   "));
    }

    [Fact]
    public void LocalFolderProviderFailures_CarryNoSecretFromAnAdjacentSftpConfiguration()
    {
        using TempDirectory backups = TempDirectory.Create();
        ISyncProviderFactory factory = Factory(backups.Path);

        using ISyncProvider sftp = factory.CreateSftpProvider(SftpSettings());
        Exception failure = Record.Exception(
            () => factory.CreateLocalFolderProvider("   "));

        Assert.NotNull(failure);
        AssertCarriesNoSftpSecret(failure!.ToString());
    }

    // ---- Helpers ----

    private static void AssertCarriesNoSftpSecret(string surface)
    {
        Assert.DoesNotContain(SftpPassword, surface, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SftpPassphrase, surface, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SftpKeyPath, surface, StringComparison.OrdinalIgnoreCase);
    }

    private static SftpConnectionSettings SftpSettings() =>
        new(
            Host: "sftp.example.invalid",
            Port: 2222,
            Username: "backup-user",
            AuthMethod: SftpAuthMethod.PrivateKey,
            Password: SftpPassword,
            PrivateKeyPath: SftpKeyPath,
            PrivateKeyPassphrase: SftpPassphrase,
            RemotePath: "/gamesave-sync",
            TrustNewHostKey: false);

    private static ServiceProvider BuildContainer()
    {
        var services = new ServiceCollection();
        services.AddGameSavesInfrastructure();
        services.RemoveAll<IAppDatabasePathProvider>();
        services.AddSingleton<IAppDatabasePathProvider>(
            new TestDatabasePathProvider(
                Path.Combine(Path.GetTempPath(), "gamesaves-factory-tests.db")));
        services.RemoveAll<IBackupHistoryService>();
        services.AddSingleton<IBackupHistoryService>(
            new RootedBackupHistoryService(Path.GetTempPath()));
        services.RemoveAll<ISyncRemoteProfileRepository>();
        services.AddSingleton<ISyncRemoteProfileRepository>(
            new RecordingLookupProfileRepository());
        return services.BuildServiceProvider();
    }

    private static ISyncProviderFactory Factory(
        string backupBasePath,
        ISyncRemoteProfileRepository? profileRepository = null)
    {
        ISyncRemoteProfileRepository repository =
            profileRepository ?? new InMemorySyncRemoteProfileRepository();

        return new SyncProviderFactory(
            new RootedBackupHistoryService(backupBasePath),
            new RecordingHistoryRepository(),
            new TestDatabasePathProvider(
                Path.Combine(backupBasePath, "gamesaves.db")),
            new GoogleDriveSyncProviderFactory(
                repository,
                new RecordingRemoteFileSystemFactory(),
                new RootedBackupHistoryService(backupBasePath),
                new RecordingHistoryRepository()));
    }

    /// <summary>
    /// Reports a chosen backup base so the local-folder provider can be built,
    /// and no runs, so nothing is ever enumerated from a real backup directory.
    /// </summary>
    private sealed class RootedBackupHistoryService : IBackupHistoryService
    {
        private readonly string _backupBasePath;

        public RootedBackupHistoryService(string backupBasePath) =>
            _backupBasePath = backupBasePath;

        public Task<IReadOnlyList<TransferBackupRunInfo>> GetRunsAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<TransferBackupRunInfo>>([]);
        }

        public string GetBackupBasePath() => _backupBasePath;
    }

    /// <summary>
    /// Wraps the shared in-memory repository and counts profile lookups, so a
    /// test can prove a refusal happened before any lookup.
    /// </summary>
    private sealed class RecordingLookupProfileRepository : ISyncRemoteProfileRepository
    {
        private readonly InMemorySyncRemoteProfileRepository _inner = new();

        public int LookupCalls { get; private set; }

        public IReadOnlyList<SyncRemoteProfile> GetAll() => _inner.GetAll();

        public SyncRemoteProfile? GetById(Guid id)
        {
            LookupCalls++;
            return _inner.GetById(id);
        }

        public SyncRemoteProfile Create(SyncRemoteProfile profile) =>
            _inner.Create(profile);

        public SyncRemoteProfile Update(SyncRemoteProfile profile) =>
            _inner.Update(profile);

        public SyncRemoteProfile Rename(
            Guid id, string displayName, DateTimeOffset updatedUtc) =>
            _inner.Rename(id, displayName, updatedUtc);

        public SyncRemoteProfile UpdateLastUsed(Guid id, DateTimeOffset usedUtc) =>
            _inner.UpdateLastUsed(id, usedUtc);

        public SyncRemoteProfile UpdateLastSuccessfulConnection(
            Guid id, DateTimeOffset connectedUtc) =>
            _inner.UpdateLastSuccessfulConnection(id, connectedUtc);

        public void Delete(Guid id) => _inner.Delete(id);
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "gamesaves-factory-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            // Only the directory this test created is removed.
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
