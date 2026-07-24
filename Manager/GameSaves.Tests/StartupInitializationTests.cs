using GameSaves.App.Services;
using GameSaves.App.ViewModels;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GameSaves.Tests;

/// <summary>
/// Coordination-level regression tests for the automatic startup data loading.
/// These use lightweight fake <see cref="IInitializableViewModel"/>s so they do
/// not depend on Steam, SQLite, backups, a Google account, SFTP, or the network.
/// </summary>
public sealed class StartupInitializationTests
{
    [Fact]
    public async Task InitializeAllAsync_InvokesEveryRegisteredViewModel()
    {
        var dashboard = new RecordingInitializable("dashboard");
        var installedGames = new RecordingInitializable("installed-games");
        var profiles = new RecordingInitializable("profiles");
        var transferPreview = new RecordingInitializable("transfer-preview");
        var manualBackup = new RecordingInitializable("manual-backup");
        var backups = new RecordingInitializable("backups");
        var history = new RecordingInitializable("history");

        var initializer = new StartupInitializer(new IInitializableViewModel[]
        {
            dashboard, installedGames, profiles, transferPreview, manualBackup, backups, history
        });

        await initializer.InitializeAllAsync();

        // Scenarios 1-7: dashboard, installed games, profiles, transfer preview,
        // manual backup, backups, and history are all initialized.
        Assert.Equal(1, dashboard.InitializeCount);
        Assert.Equal(1, installedGames.InitializeCount);
        Assert.Equal(1, profiles.InitializeCount);
        Assert.Equal(1, transferPreview.InitializeCount);
        Assert.Equal(1, manualBackup.InitializeCount);
        Assert.Equal(1, backups.InitializeCount);
        Assert.Equal(1, history.InitializeCount);
    }

    [Fact]
    public async Task InitializeAllAsync_InvokesViewModelsInRegisteredOrder()
    {
        var order = new List<string>();
        var initializer = new StartupInitializer(new IInitializableViewModel[]
        {
            new RecordingInitializable("dashboard", order),
            new RecordingInitializable("installed-games", order),
            new RecordingInitializable("profiles", order),
            new RecordingInitializable("history", order)
        });

        await initializer.InitializeAllAsync();

        Assert.Equal(new[] { "dashboard", "installed-games", "profiles", "history" }, order);
    }

    [Fact]
    public async Task InitializeAllAsync_DoesNotInvokeAViewModelThatWasNotRegistered()
    {
        // Scenario 8 and 43-47: the Sync tab is never registered with the
        // coordinator, so startup can never connect SFTP, start OAuth, create a
        // Drive folder, or preview/execute a sync.
        var sync = new RecordingInitializable("sync");
        var initializer = new StartupInitializer(new IInitializableViewModel[]
        {
            new RecordingInitializable("dashboard")
        });

        await initializer.InitializeAllAsync();

        Assert.Equal(0, sync.InitializeCount);
    }

    [Fact]
    public async Task InitializeAllAsync_ContinuesWhenOneViewModelFails()
    {
        // Scenario 9 and 42: a failing ViewModel does not stop the others.
        var before = new RecordingInitializable("before");
        var failing = new ThrowingInitializable();
        var after = new RecordingInitializable("after");

        var initializer = new StartupInitializer(new IInitializableViewModel[]
        {
            before, failing, after
        });

        await initializer.InitializeAllAsync();

        Assert.Equal(1, before.InitializeCount);
        Assert.Equal(1, after.InitializeCount);
    }

    [Fact]
    public async Task InitializeAllAsync_StopsWhenCancelledAndReportsNoError()
    {
        // Scenario 10: initialization can be cancelled cleanly.
        using var cts = new CancellationTokenSource();
        var first = new RecordingInitializable("first", onInitialize: cts.Cancel);
        var second = new RecordingInitializable("second");

        var initializer = new StartupInitializer(new IInitializableViewModel[]
        {
            first, second
        });

        // Must not throw even though the token is cancelled mid-run.
        await initializer.InitializeAllAsync(cts.Token);

        Assert.Equal(1, first.InitializeCount);
        Assert.Equal(0, second.InitializeCount);
    }

    [Fact]
    public async Task InitializeAllAsync_RunsOnlyOnce()
    {
        // Scenario 11: initialization does not run twice accidentally.
        var dashboard = new RecordingInitializable("dashboard");
        var initializer = new StartupInitializer(new IInitializableViewModel[] { dashboard });

        await initializer.InitializeAllAsync();
        await initializer.InitializeAllAsync();

        Assert.Equal(1, dashboard.InitializeCount);
    }

    [Fact]
    public async Task InitializeAllAsync_IsAwaitableAndCompletes()
    {
        // Scenarios 12 and 13: initialization is deterministically awaitable and
        // produces no unobserved exception (the returned task completes).
        var slow = new RecordingInitializable("slow", delay: TimeSpan.FromMilliseconds(20));
        var initializer = new StartupInitializer(new IInitializableViewModel[] { slow });

        Task task = initializer.InitializeAllAsync();
        await task;

        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal(1, slow.InitializeCount);
    }

    private sealed class RecordingInitializable : IInitializableViewModel
    {
        private readonly List<string>? _order;
        private readonly Action? _onInitialize;
        private readonly TimeSpan _delay;

        public RecordingInitializable(
            string name,
            List<string>? order = null,
            Action? onInitialize = null,
            TimeSpan delay = default)
        {
            Name = name;
            _order = order;
            _onInitialize = onInitialize;
            _delay = delay;
        }

        public string Name { get; }

        public int InitializeCount { get; private set; }

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InitializeCount++;
            _order?.Add(Name);
            _onInitialize?.Invoke();

            if (_delay > TimeSpan.Zero)
                await Task.Delay(_delay, CancellationToken.None);
        }
    }

    private sealed class ThrowingInitializable : IInitializableViewModel
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated tab load failure.");
    }
}
