using Gameloop.Vdf;
using Gameloop.Vdf.Linq;
using GameSaves.Core.Steam;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security;

namespace GameSaves.Infrastructure.Steam
{
    public sealed class SteamAppManifestReader : ISteamAppManifestReader
    {
        public IEnumerable<SteamGame> ReadInstalledGames(
            string libraryPath,
            SteamDiscoveryConfidence confidenceWhenFolderExists = SteamDiscoveryConfidence.High)
        {
            string steamAppsPath = Path.Combine(libraryPath, "steamapps");

            if (!Directory.Exists(steamAppsPath))
                yield break;

            IEnumerable<string> manifestPaths;

            try
            {
                manifestPaths = Directory.EnumerateFiles(
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
            VProperty root;

            try
            {
                root = VdfConvert.Deserialize(File.ReadAllText(manifestPath));
            }
            catch (Exception ex) when (
                ex is IOException ||
                ex is UnauthorizedAccessException ||
                ex is SecurityException ||
                ex is InvalidOperationException ||
                ex is ArgumentException)
            {
                return null;
            }

            if (root.Value is not VObject appState)
                return null;

            string appId = GetString(appState, "appid")
                ?? GetAppIdFromManifestFileName(manifestPath)
                ?? "unknown";

            string name = GetString(appState, "name")
                ?? $"Unknown Steam App {appId}";

            string? installDirectory = GetString(appState, "installdir");

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

        private static string? GetString(VObject source, string key)
        {
            return source[key] is VValue value
                ? value.ToString()
                : null;
        }

        private static string? GetAppIdFromManifestFileName(string manifestPath)
        {
            string fileName = Path.GetFileNameWithoutExtension(manifestPath);

            if (!fileName.StartsWith("appmanifest_", StringComparison.OrdinalIgnoreCase))
                return null;

            return fileName["appmanifest_".Length..];
        }
    }
}