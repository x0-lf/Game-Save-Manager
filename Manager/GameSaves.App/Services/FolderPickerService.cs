using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace GameSaves.App.Services
{
    public sealed class FolderPickerService : IFolderPickerService
    {
        public async Task<string?> PickFolderAsync(string title, string? startLocation = null)
        {
            var lifetime = Application.Current?.ApplicationLifetime
                as IClassicDesktopStyleApplicationLifetime;

            var window = lifetime?.MainWindow;

            if (window is null)
                return null;

            var options = new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false
            };

            if (!string.IsNullOrWhiteSpace(startLocation) &&
                Directory.Exists(startLocation))
            {
                try
                {
                    options.SuggestedStartLocation =
                        await window.StorageProvider.TryGetFolderFromPathAsync(
                            new Uri(startLocation));
                }
                catch
                {
                    // A bad start location never prevents opening the picker.
                }
            }

            var folders = await window.StorageProvider.OpenFolderPickerAsync(options);

            return folders.FirstOrDefault()?.TryGetLocalPath();
        }

        public async Task<string?> PickFileAsync(string title, string filterName, string[] patterns)
        {
            var lifetime = Application.Current?.ApplicationLifetime
                as IClassicDesktopStyleApplicationLifetime;

            var window = lifetime?.MainWindow;

            if (window is null)
                return null;

            var files = await window.StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = title,
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType(filterName)
                        {
                            Patterns = patterns
                        }
                    }
                });

            return files.FirstOrDefault()?.TryGetLocalPath();
        }
    }
}
