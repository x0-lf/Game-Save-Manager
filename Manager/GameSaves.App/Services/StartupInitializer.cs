using GameSaves.App.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GameSaves.App.Services
{
    public interface IStartupInitializer
    {
        /// <summary>
        /// Initializes each registered ViewModel once, in dependency order, so
        /// primary read-only data is present without the user pressing Refresh.
        /// Isolated failures never stop the remaining ViewModels, and the whole
        /// operation is a clean no-op when cancelled. Never throws.
        /// </summary>
        Task InitializeAllAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Runs the tab ViewModels' <see cref="IInitializableViewModel.InitializeAsync"/>
    /// sequentially. Sequential ordering lets the profile/installed-game data
    /// populate the shared ViewModels first, so the downstream tabs reuse that
    /// result instead of repeating Steam discovery. The Sync tab is deliberately
    /// excluded: its connection, OAuth, and remote-profile state are managed by
    /// its own milestones and must not be driven by startup loading.
    /// </summary>
    public sealed class StartupInitializer : IStartupInitializer
    {
        private readonly IReadOnlyList<IInitializableViewModel> _viewModels;
        private bool _started;

        public StartupInitializer(IEnumerable<IInitializableViewModel> orderedViewModels)
        {
            _viewModels = orderedViewModels?.ToList()
                ?? throw new ArgumentNullException(nameof(orderedViewModels));
        }

        public async Task InitializeAllAsync(CancellationToken cancellationToken = default)
        {
            // Guard against accidental double invocation (for example if the
            // shell were reinitialized). Manual Refresh remains unaffected.
            if (_started)
                return;

            _started = true;

            foreach (IInitializableViewModel viewModel in _viewModels)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                try
                {
                    await viewModel.InitializeAsync(cancellationToken).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    // Cancellation during shutdown is expected, not a failure.
                    return;
                }
                catch
                {
                    // A single tab failing to load must not prevent the others.
                    // Each ViewModel surfaces its own failure in its status text;
                    // manual Refresh remains the retry path.
                }
            }
        }
    }
}
