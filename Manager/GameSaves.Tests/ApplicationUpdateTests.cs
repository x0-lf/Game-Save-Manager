using GameSaves.App.Services;
using GameSaves.App.ViewModels;
using GameSaves.Core.Platform;
using GameSaves.Core.Updates;
using GameSaves.Infrastructure.Updates;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace GameSaves.Tests;

/// <summary>
/// RELEASE-002: the opt-in update check and the verified download. Every test
/// runs against a fake feed, a fake downloader or an in-memory HTTP handler;
/// nothing reaches GitHub, nothing starts an installer, and the staging folder
/// is a temporary directory.
/// </summary>
public sealed class ApplicationUpdateTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Version Running = new(0, 1, 0);

    private readonly TemporaryDirectory _temp = new();
    private readonly FakeUpdateFeed _feed = new();
    private readonly FakeUpdateDownloader _downloader = new();
    private readonly List<string> _started = new();

    public void Dispose() => _temp.Dispose();

    private static UpdateRelease Release(string version, long bytes = 150 * 1024 * 1024) => new(
        Version.Parse(version),
        $"GameSaveManager-Setup-{version}.exe",
        new Uri($"https://github.com/x0-lf/Game-Save-Manager/releases/download/v{version}/GameSaveManager-Setup-{version}.exe"),
        bytes,
        new Uri($"https://github.com/x0-lf/Game-Save-Manager/releases/download/v{version}/GameSaveManager-Setup-{version}.exe.sha256"),
        new Uri($"https://github.com/x0-lf/Game-Save-Manager/releases/tag/v{version}"));

    private UiSettingsStore Store(bool checkOnStartup = false, DateTimeOffset? lastCheck = null)
    {
        var store = new UiSettingsStore(_temp.GetPath("ui-settings.json"));
        store.Save(AppUiSettings.Default with
        {
            CheckForUpdatesOnStartup = checkOnStartup,
            LastUpdateCheckUtc = lastCheck,
        });
        return store;
    }

    private ApplicationUpdateViewModel ViewModel(UiSettingsStore store, Version? running = null) =>
        new(_feed, _downloader, store, new FixedUtcClock(Now), running ?? Running, _started.Add);

    // ---- when it checks ----

    [Fact]
    public async Task OffByDefault_TheStartupCheckRequestsNothing()
    {
        Assert.False(AppUiSettings.Default.CheckForUpdatesOnStartup);
        ApplicationUpdateViewModel viewModel = ViewModel(Store());

        await viewModel.InitializeAsync();

        Assert.Equal(0, _feed.Calls);
        Assert.False(viewModel.IsUpdateAvailable);
        Assert.Contains("Nothing is sent", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnabledStartupCheck_RunsOnce_AndNotAgainWithinADay()
    {
        _feed.Release = Release("0.1.0");
        UiSettingsStore store = Store(checkOnStartup: true);
        ApplicationUpdateViewModel viewModel = ViewModel(store);

        await viewModel.InitializeAsync();
        await viewModel.InitializeAsync();

        Assert.Equal(1, _feed.Calls);
        Assert.Equal(Now, store.Load().LastUpdateCheckUtc);

        await ViewModel(Store(checkOnStartup: true, lastCheck: Now.AddHours(-23))).InitializeAsync();
        Assert.Equal(1, _feed.Calls);

        await ViewModel(Store(checkOnStartup: true, lastCheck: Now.AddHours(-25))).InitializeAsync();
        Assert.Equal(2, _feed.Calls);
    }

    [Fact]
    public void TheHeadlessHost_NeverAsksTheFeed_WhateverTheSetting()
    {
        string database = MigratedDatabase.Create(_temp, "app.db");
        UiSettingsStore store = Store(checkOnStartup: true);

        int? exitCode = GameSaves.App.Program.RunScheduledJob(
            ["--run-job", Guid.NewGuid().ToString()],
            services =>
            {
                services.AddSingleton<IAppDatabasePathProvider>(new TestDatabasePathProvider(database));
                services.AddSingleton<IUiSettingsStore>(store);
                services.AddSingleton<IUpdateFeed>(_feed);
            });

        Assert.NotNull(exitCode);
        Assert.Equal(0, _feed.Calls);
    }

    [Fact]
    public void TheSwitch_PersistsThroughTheStore_AndLoadsBackWithTheCheckTime()
    {
        UiSettingsStore store = Store();
        ApplicationUpdateViewModel viewModel = ViewModel(store);

        viewModel.CheckOnStartup = true;
        Assert.True(store.Load().CheckForUpdatesOnStartup);
        Assert.True(ViewModel(store).CheckOnStartup);

        viewModel.CheckOnStartup = false;
        Assert.False(store.Load().CheckForUpdatesOnStartup);

        store.Save(store.Load() with { LastUpdateCheckUtc = Now });
        Assert.Equal(Now, new UiSettingsStore(store.FilePath).Load().LastUpdateCheckUtc);
    }

    // ---- what a check reports ----

    [Fact]
    public async Task ANewerRelease_IsOffered_WithVersionSizeAndNotes()
    {
        _feed.Release = Release("0.2.0", bytes: 157_286_400);
        ApplicationUpdateViewModel viewModel = ViewModel(Store());

        await viewModel.CheckNowCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsUpdateAvailable);
        Assert.Equal("0.2.0", viewModel.AvailableVersion);
        Assert.Equal("150 MB", viewModel.AvailableSizeText);
        Assert.Contains("0.2.0", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("SHA-256", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.True(viewModel.OpenReleaseNotesCommand.CanExecute(null));
        Assert.True(viewModel.DownloadCommand.CanExecute(null));
        Assert.False(viewModel.InstallNowCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("0.1.0")]
    [InlineData("0.0.9")]
    public async Task TheSameOrAnOlderRelease_IsUpToDate_NeverAnUpdate(string published)
    {
        _feed.Release = Release(published);
        ApplicationUpdateViewModel viewModel = ViewModel(Store());

        await viewModel.CheckNowCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsUpdateAvailable);
        Assert.StartsWith("Up to date", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.False(viewModel.DownloadCommand.CanExecute(null));
        Assert.False(viewModel.InstallNowCommand.CanExecute(null));
    }

    [Fact]
    public async Task NoPublishedRelease_IsSaidInWords()
    {
        ApplicationUpdateViewModel viewModel = ViewModel(Store());

        await viewModel.CheckNowCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsUpdateAvailable);
        Assert.StartsWith("No release has been published yet", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOfflineFeed_ShowsAMessage_AndNeverThrows()
    {
        _feed.Failure = new HttpRequestException("No such host is known.");
        UiSettingsStore store = Store(checkOnStartup: true);
        ApplicationUpdateViewModel viewModel = ViewModel(store);

        await viewModel.InitializeAsync();

        Assert.False(viewModel.IsUpdateAvailable);
        Assert.Equal(
            "Update check failed: No such host is known. Nothing was downloaded.",
            viewModel.StatusMessage);
        Assert.Equal(Now, store.Load().LastUpdateCheckUtc);
        Assert.True(viewModel.CheckNowCommand.CanExecute(null));
    }

    [Fact]
    public void TheRunningVersion_IsTheInformationalVersionWithoutTheCommit()
    {
        Assert.Equal(new Version(0, 1, 0), ApplicationUpdateViewModel.ParseRunningVersion("0.1.0+f19899e9d07c0f691aae1cf99be8b0867745b7aa"));
        Assert.Equal(new Version(1, 2, 3), ApplicationUpdateViewModel.ParseRunningVersion("1.2.3"));
        Assert.Equal(new Version(0, 0, 0), ApplicationUpdateViewModel.ParseRunningVersion("not a version"));
        Assert.Equal(
            Version.Parse(ReleasePackagingTests.ProjectVersion()),
            ApplicationUpdateViewModel.RunningVersionOfThisAssembly());
    }

    // ---- download, verification and the hand-off (fake downloader) ----

    [Fact]
    public async Task InstallNow_IsUnavailableUntilVerified_ThenHandsOffExactlyTheVerifiedFile()
    {
        _feed.Release = Release("0.2.0");
        _downloader.Result = new VerifiedUpdate(_temp.GetPath("updates", "GameSaveManager-Setup-0.2.0.exe"), "unsigned");
        ApplicationUpdateViewModel viewModel = ViewModel(Store());

        await viewModel.CheckNowCommand.ExecuteAsync(null);
        Assert.False(viewModel.InstallNowCommand.CanExecute(null));
        viewModel.InstallNowCommand.Execute(null);
        Assert.Empty(_started);

        await viewModel.DownloadCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsVerified);
        Assert.Equal(_downloader.Result.FilePath, viewModel.VerifiedFilePath);
        Assert.Equal("unsigned", viewModel.SignatureSummary);
        Assert.StartsWith("Verified:", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("Authenticode: unsigned", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.False(viewModel.DownloadCommand.CanExecute(null));
        Assert.True(viewModel.InstallNowCommand.CanExecute(null));

        viewModel.InstallNowCommand.Execute(null);

        Assert.Equal(new[] { _downloader.Result.FilePath }, _started);
    }

    [Fact]
    public async Task ARefusedDownload_IsReportedWithItsReason_AndOffersNoInstall()
    {
        _feed.Release = Release("0.2.0");
        _downloader.Failure = new InvalidDataException("The downloaded file's SHA-256 does not match the digest published with the release.");
        ApplicationUpdateViewModel viewModel = ViewModel(Store());

        await viewModel.CheckNowCommand.ExecuteAsync(null);
        await viewModel.DownloadCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsVerified);
        Assert.StartsWith("Update refused: The downloaded file's SHA-256 does not match", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.False(viewModel.InstallNowCommand.CanExecute(null));
        Assert.True(viewModel.DownloadCommand.CanExecute(null));
    }

    [Fact]
    public async Task ACancelledDownload_SaysNothingWasKept()
    {
        _feed.Release = Release("0.2.0");
        _downloader.Failure = new OperationCanceledException();
        ApplicationUpdateViewModel viewModel = ViewModel(Store());

        await viewModel.CheckNowCommand.ExecuteAsync(null);
        await viewModel.DownloadCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsVerified);
        Assert.StartsWith("Download cancelled", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.False(viewModel.IsDownloading);
    }

    // ---- the GitHub feed (in-memory HTTP) ----

    private const string ReleaseJson = """
        {
          "tag_name": "v0.2.0",
          "html_url": "https://github.com/x0-lf/Game-Save-Manager/releases/tag/v0.2.0",
          "draft": false,
          "assets": [
            { "name": "GameSaveManager-Setup-0.2.0.exe.sha256", "size": 101,
              "browser_download_url": "https://github.com/x0-lf/Game-Save-Manager/releases/download/v0.2.0/GameSaveManager-Setup-0.2.0.exe.sha256" },
            { "name": "GameSaveManager-Setup-0.2.0.exe", "size": 123456789,
              "browser_download_url": "https://github.com/x0-lf/Game-Save-Manager/releases/download/v0.2.0/GameSaveManager-Setup-0.2.0.exe" }
          ]
        }
        """;

    [Fact]
    public async Task TheGitHubFeed_ReadsTagNotesAndTheTwoAssets_WithOneAnonymousGet()
    {
        var requests = new List<HttpRequestMessage>();
        var feed = new GitHubReleaseFeed(new HttpClient(new MockHttpMessageHandler(request =>
        {
            requests.Add(request);
            return Json(ReleaseJson);
        })));

        UpdateRelease? release = await feed.GetLatestAsync();

        HttpRequestMessage request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(GitHubReleaseFeed.DefaultLatestReleaseUrl, request.RequestUri!.AbsoluteUri);
        Assert.Null(request.Headers.Authorization);
        Assert.Equal("GameSaveManager", request.Headers.UserAgent.ToString());

        Assert.NotNull(release);
        Assert.Equal(new Version(0, 2, 0), release.Version);
        Assert.Equal("GameSaveManager-Setup-0.2.0.exe", release.InstallerName);
        Assert.Equal(123456789, release.InstallerBytes);
        Assert.EndsWith("/GameSaveManager-Setup-0.2.0.exe", release.InstallerUri.AbsoluteUri, StringComparison.Ordinal);
        Assert.EndsWith("/GameSaveManager-Setup-0.2.0.exe.sha256", release.DigestUri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("https://github.com/x0-lf/Game-Save-Manager/releases/tag/v0.2.0", release.ReleaseNotesUri.AbsoluteUri);
    }

    [Fact]
    public async Task TheGitHubFeed_ReportsNoRelease_OnNotFound()
    {
        var feed = new GitHubReleaseFeed(new HttpClient(new MockHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.NotFound))));

        Assert.Null(await feed.GetLatestAsync());
    }

    [Theory]
    [InlineData("\"tag_name\": \"v0.2.0\"", "\"tag_name\": \"release-2\"", "The release tag 'release-2' is not v<major>.<minor>.<patch>")]
    [InlineData("\"tag_name\": \"v0.2.0\"", "\"tag_name\": \"v0.2\"", "The release tag 'v0.2' is not v<major>.<minor>.<patch>")]
    [InlineData("GameSaveManager-Setup-0.2.0.exe.sha256", "GameSaveManager-Setup-0.2.0.exe.sha512", "has no GameSaveManager-Setup-0.2.0.exe.sha256 digest")]
    [InlineData("\"name\": \"GameSaveManager-Setup-0.2.0.exe\"", "\"name\": \"Setup.exe\"", "has no GameSaveManager-Setup-0.2.0.exe asset")]
    [InlineData("https://github.com/x0-lf/Game-Save-Manager/releases/download/v0.2.0/GameSaveManager-Setup-0.2.0.exe\"", "http://github.com/x0-lf/Game-Save-Manager/releases/download/v0.2.0/GameSaveManager-Setup-0.2.0.exe\"", "is not an https address")]
    public async Task TheGitHubFeed_RefusesInWords_WhatItCannotVerify(string find, string replace, string expectedReason)
    {
        string json = ReleaseJson.Replace(find, replace, StringComparison.Ordinal);
        Assert.NotEqual(ReleaseJson, json);
        var feed = new GitHubReleaseFeed(new HttpClient(new MockHttpMessageHandler(_ => Json(json))));

        FormatException refusal = await Assert.ThrowsAsync<FormatException>(() => feed.GetLatestAsync());

        Assert.Contains(expectedReason, refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnparsableTag_ReachesTheUserAsWords_NotAsAnUpdate()
    {
        var feed = new GitHubReleaseFeed(new HttpClient(new MockHttpMessageHandler(
            _ => Json(ReleaseJson.Replace("\"tag_name\": \"v0.2.0\"", "\"tag_name\": \"nightly\"", StringComparison.Ordinal)))));
        var viewModel = new ApplicationUpdateViewModel(feed, _downloader, Store(), new FixedUtcClock(Now), Running, _started.Add);

        await viewModel.CheckNowCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsUpdateAvailable);
        Assert.Contains("The release tag 'nightly' is not v<major>.<minor>.<patch>", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    // ---- the verifying downloader (in-memory HTTP, real staging folder) ----

    private string Staging => _temp.GetPath("updates");

    private VerifiedInstallerDownloader Downloader(byte[] installer, string digestText, Func<Stream>? installerStream = null) =>
        new(
            new TestDatabasePathProvider(_temp.GetPath("gamesave.db")),
            new FixedUtcClock(Now),
            new HttpClient(new MockHttpMessageHandler(request =>
                request.RequestUri!.AbsoluteUri.EndsWith(".sha256", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(digestText) }
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = installerStream is null
                            ? new ByteArrayContent(installer)
                            : new StreamContent(installerStream())
                    })));

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] Payload(int length, byte seed = 7)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(seed + i * 31);
        return bytes;
    }

    [Fact]
    public async Task AMatchingDigest_KeepsTheFileUnderItsFinalName_MarkedAsDownloaded()
    {
        byte[] installer = Payload(200_000);
        UpdateRelease release = Release("0.2.0", installer.Length);
        var reported = new List<long>();
        VerifiedInstallerDownloader downloader = Downloader(installer, $"{Sha256Hex(installer)}  {release.InstallerName}\n");

        VerifiedUpdate verified = await downloader.DownloadAsync(release, new SynchronousProgress(reported.Add));

        Assert.Equal(Path.Combine(Staging, release.InstallerName), verified.FilePath);
        Assert.Equal(installer, await File.ReadAllBytesAsync(verified.FilePath));
        Assert.Equal("unsigned", verified.SignatureSummary);
        Assert.Equal(new[] { release.InstallerName }, Directory.GetFiles(Staging).Select(Path.GetFileName));
        Assert.Equal(installer.Length, reported[^1]);
        Assert.Equal("[ZoneTransfer]\r\nZoneId=3\r\n", File.ReadAllText(verified.FilePath + ":Zone.Identifier"));
    }

    [Theory]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000", "does not match the digest")]
    [InlineData("not a digest at all", "does not contain a SHA-256 digest")]
    public async Task AMismatchedOrUnreadableDigest_DeletesTheFile_AndRefuses(string digestText, string expectedReason)
    {
        byte[] installer = Payload(50_000);
        UpdateRelease release = Release("0.2.0", installer.Length);
        VerifiedInstallerDownloader downloader = Downloader(installer, digestText);

        InvalidDataException refusal = await Assert.ThrowsAsync<InvalidDataException>(
            () => downloader.DownloadAsync(release, null));

        Assert.Contains(expectedReason, refusal.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.Exists(Staging) ? Directory.GetFiles(Staging) : []);
    }

    [Fact]
    public async Task ATruncatedDownload_DeletesTheFile_AndRefuses()
    {
        byte[] installer = Payload(50_000);
        UpdateRelease release = Release("0.2.0", installer.Length + 1);
        VerifiedInstallerDownloader downloader = Downloader(installer, Sha256Hex(installer));

        InvalidDataException refusal = await Assert.ThrowsAsync<InvalidDataException>(
            () => downloader.DownloadAsync(release, null));

        Assert.Contains("incomplete", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Staging));
    }

    [Fact]
    public async Task ACancelledDownload_LeavesNoFile()
    {
        byte[] installer = Payload(1_000_000);
        UpdateRelease release = Release("0.2.0", installer.Length);
        using var cancellation = new CancellationTokenSource();
        VerifiedInstallerDownloader downloader = Downloader(
            installer,
            Sha256Hex(installer),
            () => new CancelAfterFirstReadStream(installer, cancellation));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => downloader.DownloadAsync(release, null, cancellation.Token));

        Assert.Empty(Directory.GetFiles(Staging));
    }

    [Fact]
    public async Task StalePartials_ArePurged_AndNothingElseIsTouched()
    {
        Directory.CreateDirectory(Staging);
        string stale = _temp.GetPath("updates", "GameSaveManager-Setup-0.1.5.exe.partial");
        string fresh = _temp.GetPath("updates", "GameSaveManager-Setup-0.1.9.exe.partial");
        string other = _temp.GetPath("updates", "notes.txt");
        string outside = _temp.GetPath("gamesave.db.partial");
        foreach (string path in new[] { stale, fresh, other, outside })
            File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(stale, Now.AddDays(-2).UtcDateTime);
        File.SetLastWriteTimeUtc(fresh, Now.AddHours(-23).UtcDateTime);
        File.SetLastWriteTimeUtc(outside, Now.AddDays(-30).UtcDateTime);

        byte[] installer = Payload(10_000);
        UpdateRelease release = Release("0.2.0", installer.Length);
        await Downloader(installer, Sha256Hex(installer)).DownloadAsync(release, null);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(outside));
    }

    [Theory]
    [InlineData("ABCDEF0123456789abcdef0123456789ABCDEF0123456789abcdef0123456789  GameSaveManager-Setup-0.2.0.exe\n", true)]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", true)]
    [InlineData("  abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789 *name.exe\r\n", true)]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef012345678", false)]
    [InlineData("<html>Not found</html>", false)]
    [InlineData("", false)]
    public void TheDigestFile_IsHexFollowedByAnOptionalName(string text, bool parses)
    {
        Assert.Equal(parses, VerifiedInstallerDownloader.TryParseDigest(text, out string digest));

        if (parses)
            Assert.Equal("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", digest);
    }

    // ---- fakes ----

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(_handler(request));
    }

    private sealed class SynchronousProgress : IProgress<long>
    {
        private readonly Action<long> _report;

        public SynchronousProgress(Action<long> report)
        {
            _report = report;
        }

        public void Report(long value) => _report(value);
    }

    // Serves the first chunk, then cancels the caller's token so the next
    // read throws the way a user pressing Cancel mid-download does.
    private sealed class CancelAfterFirstReadStream : MemoryStream
    {
        private readonly CancellationTokenSource _cancellation;
        private bool _served;

        public CancelAfterFirstReadStream(byte[] bytes, CancellationTokenSource cancellation)
            : base(bytes, writable: false)
        {
            _cancellation = cancellation;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_served)
            {
                _cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            _served = true;
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}

/// <summary>A feed that counts its calls; also stands in wherever a Settings page is built in tests.</summary>
internal sealed class FakeUpdateFeed : IUpdateFeed
{
    public UpdateRelease? Release { get; set; }
    public Exception? Failure { get; set; }
    public int Calls { get; private set; }

    public Task<UpdateRelease?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        Calls++;
        return Failure is null
            ? Task.FromResult(Release)
            : Task.FromException<UpdateRelease?>(Failure);
    }
}

internal sealed class FakeUpdateDownloader : IUpdateDownloader
{
    public VerifiedUpdate Result { get; set; } = new(@"C:\unused\GameSaveManager-Setup-0.2.0.exe", "unsigned");
    public Exception? Failure { get; set; }

    public Task<VerifiedUpdate> DownloadAsync(
        UpdateRelease release,
        IProgress<long>? bytesWritten,
        CancellationToken cancellationToken = default)
    {
        bytesWritten?.Report(release.InstallerBytes / 2);
        return Failure is null
            ? Task.FromResult(Result)
            : Task.FromException<VerifiedUpdate>(Failure);
    }
}
