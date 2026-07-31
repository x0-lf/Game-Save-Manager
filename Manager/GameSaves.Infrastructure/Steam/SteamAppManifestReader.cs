using GameSaves.Core.Steam;
using System.Security;
using ValveKeyValue;

namespace GameSaves.Infrastructure.Steam;

public sealed class SteamAppManifestReader : ISteamAppManifestReader
{
    public IEnumerable<SteamGame> ReadInstalledGames(
        string libraryPath,
        SteamDiscoveryConfidence confidenceWhenFolderExists = SteamDiscoveryConfidence.High)
    {
        string steamAppsPath = Path.Combine(libraryPath, "steamapps");

        if (!Directory.Exists(steamAppsPath))
            yield break;

        string[] manifestPaths;

        try
        {
            manifestPaths = Directory.GetFiles(
                steamAppsPath,
                "appmanifest_*.acf",
                SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (
            ex is IOException ||
            ex is UnauthorizedAccessException ||
            ex is SecurityException)
        {
            yield break;
        }

        foreach (string manifestPath in manifestPaths)
        {
            SteamGame? game = TryReadGameManifest(
                libraryPath,
                manifestPath,
                confidenceWhenFolderExists);

            if (game is not null)
                yield return game;
        }
    }

    private static SteamGame? TryReadGameManifest(
        string libraryPath,
        string manifestPath,
        SteamDiscoveryConfidence confidenceWhenFolderExists)
    {
        KVDocument appState;

        try
        {
            using FileStream stream = File.OpenRead(manifestPath);
            appState = SteamKeyValuesParser.Deserialize(stream);
        }
        catch (Exception ex) when (
            ex is IOException ||
            ex is UnauthorizedAccessException ||
            ex is SecurityException ||
            ex is KeyValueException)
        {
            return null;
        }

        if (appState.Value.ValueType != KVValueType.Collection)
            return null;

        string appId = SteamKeyValuesParser.GetString(appState, "appid")
            ?? GetAppIdFromManifestFileName(manifestPath)
            ?? "unknown";

        string name = SteamKeyValuesParser.GetString(appState, "name")
            ?? $"Unknown Steam App {appId}";

        string? installDirectory = SteamKeyValuesParser.GetString(
            appState,
            "installdir");

        if (string.IsNullOrWhiteSpace(installDirectory))
            return null;

        string gamePath = Path.Combine(
            libraryPath,
            "steamapps",
            "common",
            installDirectory);

        bool folderExists = Directory.Exists(gamePath);

        return new SteamGame(
            appId,
            name,
            installDirectory,
            libraryPath,
            manifestPath,
            gamePath,
            folderExists,
            folderExists ? confidenceWhenFolderExists : SteamDiscoveryConfidence.Medium);
    }

    private static string? GetAppIdFromManifestFileName(string manifestPath)
    {
        string fileName = Path.GetFileNameWithoutExtension(manifestPath);

        if (!fileName.StartsWith("appmanifest_", StringComparison.OrdinalIgnoreCase))
            return null;

        return fileName["appmanifest_".Length..];
    }
}
