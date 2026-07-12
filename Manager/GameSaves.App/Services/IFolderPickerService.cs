using System.Threading.Tasks;

namespace GameSaves.App.Services
{
    /// <summary>
    /// Opens a native folder picker. Returns the selected folder's local path,
    /// or null when the user cancels the dialog.
    /// </summary>
    public interface IFolderPickerService
    {
        Task<string?> PickFolderAsync(string title, string? startLocation = null);

        /// <summary>
        /// Opens a native open-file picker filtered to the given patterns
        /// (e.g. "*.zip"). Returns the selected file's local path, or null
        /// when the user cancels.
        /// </summary>
        Task<string?> PickFileAsync(string title, string filterName, string[] patterns);
    }
}
