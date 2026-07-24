using System.Threading;
using System.Threading.Tasks;

namespace GameSaves.App.ViewModels
{
    /// <summary>
    /// A ViewModel whose read-only startup data can be loaded automatically by
    /// the startup coordinator. Initialization runs the same authoritative load
    /// path as the ViewModel's manual Refresh command, must be awaitable, must
    /// support cancellation, and must not run its load more than once.
    /// </summary>
    public interface IInitializableViewModel
    {
        Task InitializeAsync(CancellationToken cancellationToken = default);
    }
}
