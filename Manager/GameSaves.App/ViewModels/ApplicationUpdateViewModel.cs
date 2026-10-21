using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameSaves.App.Services;
using GameSaves.Core.Sync;
using GameSaves.Core.Updates;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace GameSaves.App.ViewModels
{
    /// <summary>
    /// The opt-in update check (RELEASE-002), shown in Settings > Diagnostics
    /// and, while a newer version exists, as a notice on the Dashboard. It
    /// contacts the release feed only from <see cref="CheckNowCommand"/> or,
    /// with the Settings switch on, once after startup and at most once a
    /// day; the headless scheduled-run host never constructs it. A download
    /// is offered for installation only after <see cref="IUpdateDownloader"/>
    /// verified its SHA-256 against the published digest, and Install now
    /// hands that file to the installer and closes the app: the app never
    /// replaces its own files and never touches the data folder beyond the
    /// downloader's own staging directory.
    /// </summary>
    public partial class ApplicationUpdateViewModel : ViewModelBase, IInitializableViewModel
    {
        internal static readonly TimeSpan StartupCheckInterval = TimeSpan.FromHours(24);

        private readonly IUpdateFeed _feed;
        private readonly IUpdateDownloader _downloader;
        private readonly IUiSettingsStore _settings;
        private readonly IUtcClock _clock;
        private readonly Action<string> _startInstaller;
        private bool _startupCheckDone;
        private UpdateRelease? _release;
        private CancellationTokenSource? _downloadCts;

        public ApplicationUpdateViewModel(
            IUpdateFeed feed,
            IUpdateDownloader downloader,
            IUiSettingsStore settings,
            IUtcClock clock,
            Version runningVersion,
            Action<string>? startInstaller = null)
        {
            _feed = feed ?? throw new ArgumentNullException(nameof(feed));
            _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            RunningVersion = runningVersion ?? throw new ArgumentNullException(nameof(runningVersion));
            _startInstaller = startInstaller ?? StartInstallerAndExit;
            checkOnStartup = settings.Load().CheckForUpdatesOnStartup;
        }

        /// <summary>The running MAJOR.MINOR.PATCH, compared against the feed.</summary>
        public Version RunningVersion { get; }

        /// <summary>
        /// The informational version without the SDK's <c>+commit</c> suffix
        /// (RELEASE-001 made it <c>MAJOR.MINOR.PATCH+&lt;sha&gt;</c>).
        /// </summary>
        public static Version ParseRunningVersion(string? informationalVersion)
        {
            ReadOnlySpan<char> text = informationalVersion.AsSpan();
            int plus = text.IndexOf('+');

            if (plus >= 0)
                text = text[..plus];

            return Version.TryParse(text, out Version? version) ? version : new Version(0, 0, 0);
        }

        public static Version RunningVersionOfThisAssembly() => ParseRunningVersion(
            typeof(ApplicationUpdateViewModel).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(ApplicationUpdateViewModel).Assembly.GetName().Version?.ToString(3));

        /// <summary>
        /// Settings > Behaviour: ask the feed once after startup, at most once
        /// a day. Off by default; persisted in the UI settings.
        /// </summary>
        [ObservableProperty]
        private bool checkOnStartup;

        partial void OnCheckOnStartupChanged(bool value) =>
            _settings.Save(_settings.Load() with { CheckForUpdatesOnStartup = value });

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CheckNowCommand))]
        [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
        private bool isChecking;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CheckNowCommand))]
        [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
        [NotifyCanExecuteChangedFor(nameof(CancelDownloadCommand))]
        private bool isDownloading;

        /// <summary>The one line of words the Diagnostics card shows for the check, the download and the verification.</summary>
        [ObservableProperty]
        private string statusMessage =
            "Not checked. Nothing is sent until you press Check now or switch on the startup check in Behaviour.";

        /// <summary>The newer version offered by the feed, or null when none is.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsUpdateAvailable))]
        [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
        [NotifyCanExecuteChangedFor(nameof(OpenReleaseNotesCommand))]
        private string? availableVersion;

        public bool IsUpdateAvailable => AvailableVersion is not null;

        [ObservableProperty]
        private string availableSizeText = string.Empty;

        [ObservableProperty]
        private long downloadedBytes;

        [ObservableProperty]
        private long totalBytes;

        /// <summary>The installer on disk whose SHA-256 matched the published digest; null until then.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsVerified))]
        [NotifyCanExecuteChangedFor(nameof(InstallNowCommand))]
        [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
        private string? verifiedFilePath;

        public bool IsVerified => VerifiedFilePath is not null;

        [ObservableProperty]
        private string signatureSummary = string.Empty;

        /// <summary>
        /// The startup check: runs once per process, only with the switch on,
        /// and only when no check finished within the last day. Never throws;
        /// a cancelled or failed check leaves the status text and nothing else.
        /// </summary>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (_startupCheckDone)
                return;

            _startupCheckDone = true;
            AppUiSettings settings = _settings.Load();

            if (!settings.CheckForUpdatesOnStartup)
                return;

            if (settings.LastUpdateCheckUtc is DateTimeOffset last &&
                _clock.UtcNow - last < StartupCheckInterval)
            {
                StatusMessage = "Checked within the last day; the next startup check runs tomorrow. Check now asks the feed at once.";
                return;
            }

            await CheckAsync(cancellationToken);
        }

        [RelayCommand(CanExecute = nameof(CanCheckNow))]
        private Task CheckNowAsync() => CheckAsync(CancellationToken.None);

        private bool CanCheckNow() => !IsChecking && !IsDownloading;

        private async Task CheckAsync(CancellationToken cancellationToken)
        {
            IsChecking = true;
            StatusMessage = "Checking the release feed...";

            try
            {
                UpdateRelease? release = await _feed.GetLatestAsync(cancellationToken);
                Offer(release);
            }
            catch (OperationCanceledException)
            {
                // Shutdown during the startup check: nothing to report and no
                // check time to remember.
                StatusMessage = "Update check cancelled.";
                IsChecking = false;
                return;
            }
            catch (Exception exception)
            {
                Offer(null, $"Update check failed: {exception.Message} Nothing was downloaded.");
            }

            IsChecking = false;

            // Remembered after every completed attempt, success or failure, so
            // an unreachable feed is asked once a day, not once per start.
            _settings.Save(_settings.Load() with { LastUpdateCheckUtc = _clock.UtcNow });
        }

        private void Offer(UpdateRelease? release, string? failure = null)
        {
            _release = null;
            AvailableVersion = null;
            AvailableSizeText = string.Empty;
            VerifiedFilePath = null;
            SignatureSummary = string.Empty;
            DownloadedBytes = 0;
            TotalBytes = 0;

            if (failure is not null)
            {
                StatusMessage = failure;
                return;
            }

            if (release is null)
            {
                StatusMessage = $"No release has been published yet. You run {RunningVersion}.";
                return;
            }

            // Equal and older releases are "up to date": the feed can never
            // offer a downgrade or a reinstall.
            if (release.Version <= RunningVersion)
            {
                StatusMessage = $"Up to date: you run {RunningVersion} and the newest release is {release.Version}.";
                return;
            }

            _release = release;
            AvailableSizeText = FormatSize(release.InstallerBytes);
            TotalBytes = release.InstallerBytes;
            AvailableVersion = release.Version.ToString();
            StatusMessage =
                $"Version {release.Version} is available ({AvailableSizeText}); you run {RunningVersion}. " +
                "Download and verify fetches the installer and checks its SHA-256 against the digest published " +
                "with the release before anything can run.";
        }

        [RelayCommand(CanExecute = nameof(CanDownload))]
        private async Task DownloadAsync()
        {
            if (_release is null)
                return;

            UpdateRelease release = _release;
            _downloadCts = new CancellationTokenSource();
            IsDownloading = true;
            VerifiedFilePath = null;
            SignatureSummary = string.Empty;
            DownloadedBytes = 0;
            StatusMessage = $"Downloading {release.InstallerName} ({AvailableSizeText})...";

            // Progress<T> posts to the UI thread the command runs on, so the
            // bytes the downloader reports from its read loop bind safely.
            var progress = new Progress<long>(bytes => DownloadedBytes = bytes);

            try
            {
                VerifiedUpdate verified = await _downloader.DownloadAsync(release, progress, _downloadCts.Token);
                VerifiedFilePath = verified.FilePath;
                SignatureSummary = verified.SignatureSummary;
                DownloadedBytes = TotalBytes;
                StatusMessage =
                    $"Verified: the SHA-256 of {release.InstallerName} matches the digest published with the release. " +
                    $"Authenticode: {verified.SignatureSummary}. Install now closes Game Save Manager and starts the " +
                    "installer, which upgrades the program in place; your backups, settings, sign-ins and scheduled " +
                    "jobs stay where they are.";
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Download cancelled. The partial file was deleted; nothing was kept.";
            }
            catch (Exception exception)
            {
                StatusMessage = $"Update refused: {exception.Message} The downloaded file was deleted.";
            }
            finally
            {
                IsDownloading = false;
                _downloadCts.Dispose();
                _downloadCts = null;
            }
        }

        private bool CanDownload() => IsUpdateAvailable && !IsDownloading && !IsChecking && !IsVerified;

        [RelayCommand(CanExecute = nameof(IsDownloading))]
        private void CancelDownload() => _downloadCts?.Cancel();

        [RelayCommand(CanExecute = nameof(IsVerified))]
        private void InstallNow()
        {
            if (VerifiedFilePath is null)
                return;

            try
            {
                _startInstaller(VerifiedFilePath);
            }
            catch (Exception exception)
            {
                StatusMessage = $"The installer could not be started: {exception.Message}";
            }
        }

        [RelayCommand(CanExecute = nameof(IsUpdateAvailable))]
        private void OpenReleaseNotes()
        {
            if (_release is null)
                return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _release.ReleaseNotesUri.AbsoluteUri,
                    UseShellExecute = true
                });
            }
            catch (Exception exception)
            {
                StatusMessage = $"The release notes could not be opened: {exception.Message}";
            }
        }

        // The hand-off: the installer's own wizard, not /SILENT, so the user
        // sees the install location and Windows shows its SmartScreen prompt
        // for an unsigned package. The app closes only after the installer
        // process exists; Inno Setup's CloseApplications handles the rest.
        private static void StartInstallerAndExit(string installerPath)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = true
            });

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        }

        private static string FormatSize(long bytes) =>
            bytes <= 0
                ? "size unknown"
                : string.Create(CultureInfo.InvariantCulture, $"{bytes / 1048576.0:0.#} MB");
    }
}
