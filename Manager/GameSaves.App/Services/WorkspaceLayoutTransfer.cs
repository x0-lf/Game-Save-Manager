using System;
using System.Collections.Generic;
using System.Text.Json;

namespace GameSaves.App.Services
{
    /// <summary>
    /// Exchanges saved workspace layouts with a user-chosen JSON file. The
    /// payload is one JSON array of layouts and carries layout names, stable
    /// tab keys, and window numbers only — never paths, account values, or
    /// identifiers. Imported payloads pass through the same forgiving
    /// normalization as the settings store, so unknown tab keys and malformed
    /// entries are dropped instead of imported.
    /// </summary>
    public static class WorkspaceLayoutTransfer
    {
        public static string Serialize(
            IReadOnlyList<UiWorkspaceLayoutSettings> layouts) =>
            JsonSerializer.Serialize(
                layouts,
                new JsonSerializerOptions { WriteIndented = true });

        public static IReadOnlyList<UiWorkspaceLayoutSettings> Deserialize(string json)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);

                if (document.RootElement.ValueKind != JsonValueKind.Array)
                    return Array.Empty<UiWorkspaceLayoutSettings>();

                return UiSettingsStore.ParseWorkspaceLayouts(document.RootElement);
            }
            catch (JsonException)
            {
                return Array.Empty<UiWorkspaceLayoutSettings>();
            }
        }
    }
}
