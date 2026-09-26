using System.Net;
using System.Net.Http.Headers;
using System.Security;
using System.Text;
using GameSaves.App.Services;
using GameSaves.App.ViewModels;
using GameSaves.Core.Secrets;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Sync;
using GameSaves.Infrastructure.Transfers;
using GameSaves.Infrastructure.WebDav;

namespace GameSaves.Tests;

/// <summary>
/// SYNC-001. The WebDAV provider against an in-memory RFC 4918 server on a
/// stub HttpMessageHandler: no network, no live server, and every request the
/// client makes is visible to the assertions.
/// </summary>
public sealed class WebDavSyncProviderTests
{
    private const string Folder = "GameSave Manager Backups";
    private const string Password = "s3cret";

    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-09-26T10:00:00Z");

    private static readonly SyncOptions Execute = new() { DryRun = false, ConfirmExecution = true };

    // ---- settings and persistence ----

    [Theory]
    [InlineData("http://dav.example.test/", "https://")]
    [InlineData("https://alice:pw@dav.example.test/", "must not contain")]
    [InlineData("https://dav.example.test/?x=1", "must not contain")]
    [InlineData("not a url", "not a valid address")]
    [InlineData("", "Enter the WebDAV server URL")]
    public void Settings_RefuseAnythingButAPlainHttpsUrl(string url, string expected)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new WebDavSyncRemoteSettings(url, "alice", Folder));

        Assert.Contains(expected, exception.Message);
    }

    [Theory]
    [InlineData("alice", "../escape")]
    [InlineData("alice", "a//b")]
    [InlineData("al:ice", Folder)]
    [InlineData("  ", Folder)]
    public void Settings_RefuseUnsafeUserNamesAndFolders(string username, string folder)
    {
        Assert.Throws<ArgumentException>(
            () => new WebDavSyncRemoteSettings(FakeWebDavServer.ServerUrl, username, folder));
    }

    [Fact]
    public void Settings_AreNormalized()
    {
        var settings = new WebDavSyncRemoteSettings(
            " https://dav.example.test/remote.php/dav/files/alice ",
            " alice ",
            @"\Games\Saves/");

        Assert.Equal("https://dav.example.test/remote.php/dav/files/alice/", settings.ServerUrl);
        Assert.Equal("alice", settings.Username);
        Assert.Equal("Games/Saves", settings.RemoteFolder);
        Assert.Equal("https://dav.example.test/remote.php/dav/files/alice/Games/Saves", settings.DisplayRoot);
    }

    [Fact]
    public void Serializer_RoundTripsWebDavSettings_AndRefusesAHandEditedHttpRow()
    {
        var serializer = new SyncRemoteProfileSettingsSerializer();
        var settings = new WebDavSyncRemoteSettings(FakeWebDavServer.ServerUrl, "alice", Folder);

        string json = serializer.Serialize(SyncProviderKind.WebDav, settings);
        SyncRemoteProfileSettingsReadResult read = serializer.Deserialize(SyncProviderKind.WebDav, 1, json);

        Assert.Null(read.Error);
        Assert.Equal(settings, read.Settings);
        Assert.DoesNotContain(Password, json);

        string downgraded = json.Replace("https://", "http://", StringComparison.Ordinal);
        SyncRemoteProfileSettingsReadResult refused =
            serializer.Deserialize(SyncProviderKind.WebDav, 1, downgraded);

        Assert.Null(refused.Settings);
        Assert.Contains("corrupted", refused.Error);
    }

    // ---- multistatus parsing ----

    [Fact]
    public void Multistatus_DecodesHrefsAndSkipsMembersThatAreNotThere()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <d:multistatus xmlns:d="DAV:">
              <d:response>
                <d:href>/dav/Game%20Saves/</d:href>
                <d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
              </d:response>
              <d:response>
                <d:href>https://dav.example.test/dav/Game%20Saves/%E4%BF%9D%E5%AD%98.7z</d:href>
                <d:propstat><d:prop><d:resourcetype/><d:getcontentlength>42</d:getcontentlength></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
                <d:propstat><d:prop><d:quota-used-bytes/></d:prop><d:status>HTTP/1.1 404 Not Found</d:status></d:propstat>
              </d:response>
              <d:response>
                <d:href>/dav/Game%20Saves/vanished</d:href>
                <d:status>HTTP/1.1 404 Not Found</d:status>
              </d:response>
            </d:multistatus>
            """;

        IReadOnlyList<(string Path, WebDavEntry Entry)> entries = WebDavMultistatus.Parse(xml);

        Assert.Equal(2, entries.Count);
        Assert.Equal("/dav/Game Saves/", entries[0].Path);
        Assert.True(entries[0].Entry.IsCollection);
        Assert.Equal("/dav/Game Saves/保存.7z", entries[1].Path);
        Assert.Equal(new WebDavEntry("保存.7z", false, 42), entries[1].Entry);
    }

    // A listing is untrusted input: entity expansion and external entities
    // must never be processed.
    [Fact]
    public void Multistatus_RefusesADocumentTypeDeclaration()
    {
        const string xml = """
            <?xml version="1.0"?>
            <!DOCTYPE d:multistatus [ <!ENTITY x SYSTEM "file:///c:/windows/win.ini"> ]>
            <d:multistatus xmlns:d="DAV:"><d:response><d:href>&x;</d:href></d:response></d:multistatus>
            """;

        Assert.Throws<InvalidDataException>(() => WebDavMultistatus.Parse(xml));
    }

    [Fact]
    public async Task Listing_KeepsOnlyDirectChildren_WhateverFormTheHrefsTake()
    {
        var server = new FakeWebDavServer { AbsoluteHrefs = true };
        server.AddFile($"{Folder}/run one/manifest.json", "{}"u8.ToArray());
        server.AddFile($"{Folder}/run one.7z", [1, 2, 3]);
        WebDavRemoteFileSystem remote = Remote(server);

        Assert.Equal(new[] { "run one" }, await remote.ListRunFolderNamesAsync());
        Assert.Equal(new[] { "run one.7z" }, await remote.ListRunArchiveNamesAsync());
        Assert.Equal(new[] { "manifest.json" }, await remote.ListFilesAsync("run one"));
    }

    // ---- create-only, metadata allowlist, no local overwrite ----

    [Fact]
    public async Task AnExistingManifest_IsNeverReplaced_EvenByAServerThatIgnoresIfNoneMatch()
    {
        var server = new FakeWebDavServer { IgnoreIfNoneMatch = true };
        server.AddFile($"{Folder}/run/manifest.json", "original"u8.ToArray());
        WebDavRemoteFileSystem remote = Remote(server);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => remote.CreateTextFileIfMissingAsync("run/manifest.json", "replacement"));

        Assert.Equal("original", Encoding.UTF8.GetString(server.File($"{Folder}/run/manifest.json")!));
        Assert.DoesNotContain(server.Requests, request => request.Method == "PUT");
    }

    [Fact]
    public async Task APayloadThatAppeared_IsRefusedByTheServer_AndLeftAsItWas()
    {
        using var temp = new TemporaryDirectory();
        var server = new FakeWebDavServer();
        server.AddFile($"{Folder}/run/files/a.sav", "theirs"u8.ToArray());
        string local = temp.GetPath("a.sav");
        File.WriteAllText(local, "ours");
        WebDavRemoteFileSystem remote = Remote(server);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => remote.UploadFileAsync(local, "run/files/a.sav"));

        Assert.Contains("create-only", refusal.Message);
        Assert.Equal("theirs", Encoding.UTF8.GetString(server.File($"{Folder}/run/files/a.sav")!));
        Assert.True(server.Requests.Single(request => request.Method == "PUT").IfNoneMatchAny);
    }

    [Fact]
    public async Task OnlyTheSyncLog_CanBeReplaced()
    {
        var server = new FakeWebDavServer();
        WebDavRemoteFileSystem remote = Remote(server);

        await Assert.ThrowsAsync<ArgumentException>(
            () => remote.ReplaceProviderMetadataAsync("run/manifest.json", "{}"));

        await remote.ReplaceProviderMetadataAsync(RemoteProviderMetadataPath.SyncLog, "[1]");
        await remote.ReplaceProviderMetadataAsync(RemoteProviderMetadataPath.SyncLog, "[1,2]");

        Assert.Equal("[1,2]", await remote.ReadProviderMetadataAsync(RemoteProviderMetadataPath.SyncLog));
        Assert.All(
            server.Requests.Where(request => request.Method == "PUT"),
            request => Assert.False(request.IfNoneMatchAny));
    }

    [Fact]
    public async Task ADownload_NeverReplacesAnExistingLocalFile()
    {
        using var temp = new TemporaryDirectory();
        var server = new FakeWebDavServer();
        server.AddFile($"{Folder}/run/files/a.sav", "remote"u8.ToArray());
        string local = temp.GetPath("a.sav");
        File.WriteAllText(local, "local");

        await Assert.ThrowsAsync<IOException>(
            () => Remote(server).DownloadFileAsync("run/files/a.sav", local));

        Assert.Equal("local", File.ReadAllText(local));
    }

    // ---- authentication, TLS, redirects, retries ----

    [Fact]
    public async Task Requests_CarryBasicAuthentication()
    {
        var server = new FakeWebDavServer();

        Assert.Null(await Remote(server).ValidateAsync());
        Assert.All(server.Requests, request =>
            Assert.Equal(FakeWebDavServer.Basic("alice", Password), request.Authorization));
    }

    [Fact]
    public async Task AWrongPassword_IsAPreviewError_NotACrash()
    {
        var server = new FakeWebDavServer();

        TransferPreviewWarning? warning = await Remote(server, password: "wrong").ValidateAsync();

        Assert.NotNull(warning);
        Assert.Equal("WebDavAuthenticationFailed", warning.Code);
        Assert.Equal(TransferWarningSeverity.Error, warning.Severity);
    }

    // Redirects are not followed, so the password is never re-sent to
    // another host or over http; the user is told to enter the final URL.
    [Fact]
    public async Task ARedirect_IsReportedAndNotFollowed()
    {
        var server = new FakeWebDavServer
        {
            Intercept = _ =>
            {
                var moved = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
                moved.Headers.Location = new Uri("http://elsewhere.example.test/dav/");
                return moved;
            }
        };

        TransferPreviewWarning? warning = await Remote(server).ValidateAsync();

        Assert.Equal("WebDavAccessRefused", warning?.Code);
        Assert.Contains("redirect", warning!.Message);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task ThrottlingAndServerErrors_AreRetried_HonouringRetryAfter()
    {
        using var workspace = new Workspace();
        await workspace.StorePasswordAsync();
        bool failed = false;
        workspace.Server.Intercept = request =>
        {
            if (failed || request.Method.Method != "PROPFIND")
                return null;

            failed = true;
            var busy = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            busy.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
            return busy;
        };

        using ISyncProvider provider = workspace.Provider();
        SyncPlan plan = await provider.CreatePreviewAsync(new SyncOptions());

        Assert.True(plan.ProviderValidationSucceeded);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2) }, workspace.Delay.Requested);
    }

    // A profile edited to point at another server must not send the password
    // that was stored for the first one.
    [Fact]
    public async Task APasswordStoredForOneServer_IsNeverSentToAnother()
    {
        using var workspace = new Workspace();
        await workspace.StorePasswordAsync();
        workspace.Profiles.Update(Profile(workspace.ProfileId, "https://elsewhere.example.test/dav/"));

        using ISyncProvider provider = workspace.Provider();
        SyncPlan plan = await provider.CreatePreviewAsync(new SyncOptions());

        TransferPreviewWarning refused = Assert.Single(
            plan.Warnings,
            warning => warning.Code == "WebDavAuthenticationFailed");
        Assert.Contains("No WebDAV password is stored", refused.Message);
        Assert.Empty(workspace.Server.Requests);
    }

    // ---- the whole engine through the real factory ----

    [Fact]
    public async Task Upload_PutsThePayloadBeforeTheManifest_AndTheNextPreviewIsInSync()
    {
        using var workspace = new Workspace();
        await workspace.StorePasswordAsync();
        workspace.LocalRun("run-1");

        using ISyncProvider provider = workspace.Provider();
        SyncPlan plan = await provider.CreatePreviewAsync(new SyncOptions());
        Assert.Equal(SyncItemAction.UploadToRemote, Assert.Single(plan.Items).Action);

        SyncResult result = await provider.ExecuteAsync(plan, Execute);

        SyncItemResult uploaded = Assert.Single(result.Items);
        Assert.True(uploaded.Status == SyncItemStatus.Uploaded, uploaded.Error);
        string[] runPuts = workspace.Server.Requests
            .Where(request => request.Method == "PUT" && request.Path.Contains("/run-1/", StringComparison.Ordinal))
            .Select(request => request.Path)
            .ToArray();
        Assert.True(runPuts.Length >= 2);
        Assert.EndsWith("/run-1/manifest.json", runPuts[^1], StringComparison.Ordinal);
        Assert.All(
            workspace.Server.Requests.Where(request =>
                request.Method == "PUT" && request.Path.Contains("/run-1/", StringComparison.Ordinal)),
            request => Assert.True(request.IfNoneMatchAny));
        Assert.Equal("WebDAV", provider.ProviderName);
        Assert.DoesNotContain(Password, provider.RemoteRoot);

        using ISyncProvider again = workspace.Provider();
        SyncPlan next = await again.CreatePreviewAsync(new SyncOptions());
        Assert.Equal(SyncItemAction.InSync, Assert.Single(next.Items).Action);
    }

    [Fact]
    public async Task ArchiveSync_UploadsOneContainerAndItsSidecar_ThenRoundTripsTheRun()
    {
        using var workspace = new Workspace();
        await workspace.StorePasswordAsync();
        TransferBackupRunInfo run = workspace.LocalRun("run-7");
        var archive = new SyncOptions { ArchiveSync = true };
        var executeArchive = new SyncOptions { DryRun = false, ConfirmExecution = true, ArchiveSync = true };

        using (ISyncProvider provider = workspace.Provider())
        {
            SyncPlan plan = await provider.CreatePreviewAsync(archive);
            SyncResult result = await provider.ExecuteAsync(plan, executeArchive);
            SyncItemResult uploaded = Assert.Single(result.Items);
            Assert.True(uploaded.Status == SyncItemStatus.Uploaded, uploaded.Error);
        }

        Assert.NotNull(workspace.Server.File($"{Folder}/run-7.7z"));
        Assert.NotNull(workspace.Server.File($"{Folder}/run-7.7z.manifest.json"));
        Assert.False(workspace.Server.IsCollection($"{Folder}/run-7"));
        string[] puts = workspace.Server.Requests
            .Where(request => request.Method == "PUT")
            .Select(request => request.Path)
            .ToArray();
        Assert.True(
            Array.FindIndex(puts, path => path.EndsWith("/run-7.7z", StringComparison.Ordinal)) <
            Array.FindIndex(puts, path => path.EndsWith("/run-7.7z.manifest.json", StringComparison.Ordinal)));

        using var other = new TemporaryDirectory();
        var emptyBase = new BackupHistoryService(
            new TestDatabasePathProvider(other.GetPath("app", "gamesave.db")));

        using ISyncProvider download = workspace.Provider(emptyBase);
        SyncPlan downloadPlan = await download.CreatePreviewAsync(archive);
        Assert.Equal(SyncItemAction.DownloadToLocal, Assert.Single(downloadPlan.Items).Action);

        SyncResult downloaded = await download.ExecuteAsync(downloadPlan, executeArchive);

        SyncItemResult imported = Assert.Single(downloaded.Items);
        Assert.True(imported.Status == SyncItemStatus.Downloaded, imported.Error);
        Assert.Contains(
            await emptyBase.GetRunsAsync(),
            candidate => candidate.Manifest.Game == run.Manifest.Game);
    }

    [Fact]
    public void Factory_RefusesAProfileOfAnotherKind()
    {
        using var workspace = new Workspace();
        var localId = Guid.NewGuid();
        workspace.Profiles.Create(new SyncRemoteProfile(
            localId, "Local", SyncProviderKind.LocalFolder, null, null,
            new LocalFolderSyncRemoteSettings(@"D:\Sync"), T0, T0, null, null, null));

        var factory = new WebDavSyncProviderFactory(
            workspace.Profiles, workspace.Secrets, workspace.History,
            new RecordingHistoryRepository(), workspace.Delay, null,
            new HttpClient(workspace.Server, disposeHandler: false));

        Assert.Throws<InvalidOperationException>(() => factory.Create(localId));
    }

    [Fact]
    public async Task TheProfileService_StoresAPasswordOnlyForAWebDavProfile()
    {
        using var workspace = new Workspace();
        var localId = Guid.NewGuid();
        workspace.Profiles.Create(new SyncRemoteProfile(
            localId, "Local", SyncProviderKind.LocalFolder, null, null,
            new LocalFolderSyncRemoteSettings(@"D:\Sync"), T0, T0, null, null, null));
        var service = new SyncRemoteProfileService(workspace.Profiles, workspace.Secrets);

        Assert.False((await service.StoreWebDavPasswordAsync(localId, Password)).Succeeded);
        Assert.False(await workspace.Secrets.ExistsAsync(new SecretKey(localId, SecretNames.WebDavPassword)));

        Assert.True((await service.StoreWebDavPasswordAsync(workspace.ProfileId, Password)).Succeeded);
        SecretReadResult stored = await workspace.Secrets.ReadAsync(
            new SecretKey(workspace.ProfileId, SecretNames.WebDavPassword));
        Assert.Equal(Password, WebDavStoredCredential.ReadPassword(stored.Value!, FakeWebDavServer.ServerUrl));
    }

    // ---- the Sync page ----

    [Fact]
    public async Task SyncPage_StoresThePassword_ThenPreviewsOnlyTheSavedSettings()
    {
        var id = Guid.NewGuid();
        var repository = new InMemorySyncRemoteProfileRepository();
        repository.Create(Profile(id));
        var secrets = new InMemorySecretStore();
        var factory = new SyncProviderSelectionTests.RecordingSyncProviderFactory();
        SyncUiSettings settings = SyncUiSettings.Default with
        {
            SelectedProviderKind = SyncProviderKind.WebDav,
            SelectedRemoteProfileId = id
        };
        var viewModel = new SyncViewModel(
            factory,
            new SyncProviderCatalog(),
            new SyncProviderSelectionTests.NullFolderPickerService(),
            new SyncProviderSelectionTests.InMemorySyncSettingsStore(settings),
            repository,
            new SyncRemoteProfileService(repository, secrets),
            new StubSyncRemoteProfileMigrationService(settings),
            new FixedUtcClock(T0),
            new StubGoogleDriveOAuthService(),
            SyncProviderSelectionTests.NewWorkspaceLayout());

        Assert.True(viewModel.IsWebDavSelected);
        Assert.Equal(FakeWebDavServer.ServerUrl, viewModel.WebDavServerUrl);
        Assert.False(viewModel.HasStoredAuthentication);

        await viewModel.PreviewSyncCommand.ExecuteAsync(null);
        Assert.Equal(
            "Enter the WebDAV password or app password and choose Store password first.",
            viewModel.StatusMessage);

        Assert.False(viewModel.CanStoreWebDavPassword);
        viewModel.WebDavPassword = Password;
        Assert.True(viewModel.CanStoreWebDavPassword);

        await viewModel.StoreWebDavPasswordCommand.ExecuteAsync(null);

        Assert.Equal("", viewModel.WebDavPassword);
        Assert.True(viewModel.HasStoredAuthentication);
        Assert.True(await secrets.ExistsAsync(new SecretKey(id, SecretNames.WebDavPassword)));

        await viewModel.PreviewSyncCommand.ExecuteAsync(null);
        Assert.Equal(1, factory.WebDavCreateCount);
        Assert.Equal(id, factory.LastWebDavProfileId);

        // The provider reads the saved profile, so unsaved edits block the
        // preview instead of silently not applying.
        viewModel.WebDavRemoteFolder = "Somewhere else";
        await viewModel.PreviewSyncCommand.ExecuteAsync(null);
        Assert.Equal(
            "Save the WebDAV profile first, so the preview uses the settings shown.",
            viewModel.StatusMessage);
        Assert.Equal(1, factory.WebDavCreateCount);
    }

    [Fact]
    public void SyncView_HasAWebDavPanelWithAMaskedPasswordAndNamedFields()
    {
        string xaml = CancelSyncTests.ReadSyncView();

        Assert.Contains("IsVisible=\"{Binding IsWebDavSelected}\"", xaml);
        Assert.Contains("Command=\"{Binding StoreWebDavPasswordCommand}\"", xaml);

        int password = xaml.IndexOf("Text=\"{Binding WebDavPassword}\"", StringComparison.Ordinal);
        Assert.True(password > 0);
        Assert.Contains("PasswordChar=", xaml.Substring(password, 200));

        foreach (string name in new[] { "WebDAV server URL", "WebDAV user name", "WebDAV folder for backups", "WebDAV password or app password" })
            Assert.Contains($"AutomationProperties.Name=\"{name}\"", xaml);
    }

    // ---- helpers ----

    private static SyncRemoteProfile Profile(Guid id, string serverUrl = FakeWebDavServer.ServerUrl) =>
        new(
            id,
            "Nextcloud",
            SyncProviderKind.WebDav,
            "alice",
            null,
            new WebDavSyncRemoteSettings(serverUrl, "alice", Folder),
            T0,
            T0,
            null,
            null,
            null);

    private static WebDavRemoteFileSystem Remote(FakeWebDavServer server, string password = Password)
    {
        var client = new WebDavClient(
            new HttpClient(server, disposeHandler: false),
            FakeWebDavServer.ServerUrl,
            "alice",
            _ => Task.FromResult<string?>(password));

        return new WebDavRemoteFileSystem(client, Folder, FakeWebDavServer.ServerUrl + Folder);
    }

    private sealed class Workspace : IDisposable
    {
        private readonly TemporaryDirectory _temp = new();

        public Workspace()
        {
            Profiles.Create(Profile(ProfileId));
            History = new BackupHistoryService(
                new TestDatabasePathProvider(_temp.GetPath("app", "gamesave.db")));
        }

        public Guid ProfileId { get; } = Guid.NewGuid();

        public FakeWebDavServer Server { get; } = new();

        public InMemorySyncRemoteProfileRepository Profiles { get; } = new();

        public InMemorySecretStore Secrets { get; } = new();

        public RecordingDelayProvider Delay { get; } = new();

        public BackupHistoryService History { get; }

        public async Task StorePasswordAsync()
        {
            SecretOperationResult result = await new SyncRemoteProfileService(Profiles, Secrets)
                .StoreWebDavPasswordAsync(ProfileId, Password);
            Assert.True(result.Succeeded);
        }

        public TransferBackupRunInfo LocalRun(string name) =>
            TestData.CreateBackupRun(
                Path.Combine(History.GetBackupBasePath(), name),
                _temp.GetPath("original", $"{name}.sav"),
                $"save data for {name}");

        public ISyncProvider Provider(IBackupHistoryService? history = null) =>
            new WebDavSyncProviderFactory(
                Profiles,
                Secrets,
                history ?? History,
                new RecordingHistoryRepository(),
                Delay,
                backoffNotifier: null,
                new HttpClient(Server, disposeHandler: false)).Create(ProfileId);

        public void Dispose() => _temp.Dispose();
    }
}

/// <summary>
/// An in-memory RFC 4918 server: PROPFIND (depth 0 and 1), MKCOL, PUT with
/// If-None-Match, and GET, over absolute paths below one user root.
/// </summary>
internal sealed class FakeWebDavServer : HttpMessageHandler
{
    public const string Origin = "https://dav.example.test";
    public const string BasePath = "/remote.php/dav/files/alice";
    public const string ServerUrl = Origin + BasePath + "/";

    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _collections = new(StringComparer.Ordinal) { BasePath };

    public string ExpectedAuthorization { get; set; } = Basic("alice", "s3cret");

    public bool IgnoreIfNoneMatch { get; set; }

    public bool AbsoluteHrefs { get; set; }

    public Func<HttpRequestMessage, HttpResponseMessage?>? Intercept { get; set; }

    public List<RecordedRequest> Requests { get; } = [];

    public static string Basic(string user, string password) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));

    public byte[]? File(string relative) => _files.GetValueOrDefault($"{BasePath}/{relative}");

    public bool IsCollection(string relative) => _collections.Contains($"{BasePath}/{relative}");

    public void AddFile(string relative, byte[] content)
    {
        string path = $"{BasePath}/{relative}";
        for (string parent = Parent(path); !_collections.Contains(parent); parent = Parent(parent))
            _collections.Add(parent);

        _files[path] = content;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath).TrimEnd('/');
        bool ifNoneMatchAny = request.Headers.IfNoneMatch.Any(tag => tag.Tag == "*");
        Requests.Add(new RecordedRequest(
            request.Method.Method,
            path,
            ifNoneMatchAny,
            request.Headers.Authorization?.ToString()));

        if (Intercept?.Invoke(request) is { } intercepted)
            return intercepted;

        if (request.Headers.Authorization?.ToString() != ExpectedAuthorization)
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);

        switch (request.Method.Method)
        {
            case "PROPFIND":
                if (!_collections.Contains(path) && !_files.ContainsKey(path))
                    return new HttpResponseMessage(HttpStatusCode.NotFound);

                var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\">");
                AppendResponse(xml, path);

                if (request.Headers.GetValues("Depth").Single() == "1" && _collections.Contains(path))
                {
                    foreach (string child in _collections.Concat(_files.Keys)
                                 .Where(candidate => candidate != path && Parent(candidate) == path)
                                 .Order(StringComparer.Ordinal))
                    {
                        AppendResponse(xml, child);
                    }
                }

                xml.Append("</d:multistatus>");
                return new HttpResponseMessage(HttpStatusCode.MultiStatus)
                {
                    Content = new StringContent(xml.ToString(), Encoding.UTF8, "application/xml")
                };

            case "MKCOL":
                if (_collections.Contains(path) || _files.ContainsKey(path))
                    return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);

                if (!_collections.Contains(Parent(path)))
                    return new HttpResponseMessage(HttpStatusCode.Conflict);

                _collections.Add(path);
                return new HttpResponseMessage(HttpStatusCode.Created);

            case "PUT":
                if (_collections.Contains(path))
                    return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);

                if (!_collections.Contains(Parent(path)))
                    return new HttpResponseMessage(HttpStatusCode.Conflict);

                bool existed = _files.ContainsKey(path);
                if (existed && ifNoneMatchAny && !IgnoreIfNoneMatch)
                    return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);

                _files[path] = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                return new HttpResponseMessage(existed ? HttpStatusCode.NoContent : HttpStatusCode.Created);

            case "GET":
                return _files.TryGetValue(path, out byte[]? bytes)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound);

            default:
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }
    }

    private void AppendResponse(StringBuilder xml, string path)
    {
        bool collection = _collections.Contains(path);
        string escaped = string.Join('/', path.Split('/').Select(Uri.EscapeDataString)) +
                         (collection ? "/" : "");
        string href = AbsoluteHrefs ? Origin + escaped : escaped;

        xml.Append("<d:response><d:href>")
            .Append(SecurityElement.Escape(href))
            .Append("</d:href><d:propstat><d:prop>")
            .Append(collection
                ? "<d:resourcetype><d:collection/></d:resourcetype>"
                : $"<d:resourcetype/><d:getcontentlength>{_files[path].Length}</d:getcontentlength>")
            .Append("</d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>");
    }

    private static string Parent(string path) => path[..path.LastIndexOf('/')];

    internal sealed record RecordedRequest(
        string Method,
        string Path,
        bool IfNoneMatchAny,
        string? Authorization);
}
