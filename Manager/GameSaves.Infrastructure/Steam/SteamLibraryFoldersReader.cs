using GameSaves.Core.Steam;
using System.Globalization;
using System.Security;
using ValveKeyValue;

namespace GameSaves.Infrastructure.Steam;

public sealed class SteamLibraryFoldersReader : ISteamLibraryFoldersReader
{
    public IEnumerable<string> ReadLibraryPaths(string steamRoot)
    {
        var emittedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string vdfPath in GetPossibleLibraryFoldersFiles(steamRoot))
        {
            if (!File.Exists(vdfPath))
                continue;

            foreach (string libraryPath in ReadLibraryPathsFromFile(vdfPath))
            {
                if (emittedPaths.Add(libraryPath))
                    yield return libraryPath;
            }
        }
    }

    private static IEnumerable<string> GetPossibleLibraryFoldersFiles(string steamRoot)
    {
        yield return Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        yield return Path.Combine(steamRoot, "config", "libraryfolders.vdf");
    }

    private static IEnumerable<string> ReadLibraryPathsFromFile(string vdfPath)
    {
        KVDocument libraryFolders;

        try
        {
            using FileStream stream = File.OpenRead(vdfPath);
            libraryFolders = SteamKeyValuesParser.Deserialize(stream);
        }
        catch (Exception ex) when (
            ex is IOException ||
            ex is UnauthorizedAccessException ||
            ex is SecurityException ||
            ex is KeyValueException)
        {
            yield break;
        }

        if (libraryFolders.Value.ValueType != KVValueType.Collection)
            yield break;

        foreach (KVObject child in libraryFolders.Children)
        {
            if (!uint.TryParse(
                    child.Name,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out _))
            {
                continue;
            }

            string? rawPath = ExtractLibraryPath(child);

            if (!TryNormalizePath(rawPath, out string normalizedPath))
                continue;

            if (!Directory.Exists(normalizedPath))
                continue;

            yield return normalizedPath;
        }
    }

    private static string? ExtractLibraryPath(KVObject child)
    {
        // Older Steam format:
        // "1" "D:\\SteamLibrary"
        if (child.Value.ValueType == KVValueType.String)
            return (string)child.Value;

        // Modern Steam format:
        // "1"
        // {
        //     "path" "D:\\SteamLibrary"
        // }
        return child.Value.ValueType == KVValueType.Collection
            ? SteamKeyValuesParser.GetString(child, "path")
            : null;
    }

    private static bool TryNormalizePath(string? rawPath, out string normalizedPath)
    {
        normalizedPath = string.Empty;

        if (string.IsNullOrWhiteSpace(rawPath))
            return false;

        try
        {
            string expandedPath = Environment.ExpandEnvironmentVariables(
                rawPath.Trim().Trim('"'));
            normalizedPath = Path.GetFullPath(expandedPath);
            return true;
        }
        catch (Exception ex) when (
            ex is ArgumentException ||
            ex is NotSupportedException ||
            ex is PathTooLongException)
        {
            return false;
        }
    }
}
