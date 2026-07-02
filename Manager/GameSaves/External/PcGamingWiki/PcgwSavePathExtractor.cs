using GameSave.SavePaths;
using System.Text.RegularExpressions;

namespace GameSave.External
{
    public sealed class PcgwSavePathExtractor
    {
        private static readonly Regex SaveSectionHeadingRegex = new(
            @"(?im)^={2,6}\s*Save game data location\s*={2,6}\s*$",
            RegexOptions.Compiled);

        private static readonly Regex AnyHeadingRegex = new(
            @"(?im)^={2,6}\s*.+?\s*={2,6}\s*$",
            RegexOptions.Compiled);

        private static readonly Regex PathCandidateRegex = new(
            @"(?i)(%APPDATA%|%LOCALAPPDATA%|%USERPROFILE%|%PROGRAMDATA%|%DOCUMENTS%|\{UserProfile\}|\{AppData\}|\{LocalAppData\}|\{ProgramData\}|\{Documents\}|\{SavedGames\}|\{SteamRoot\}|\{SteamUserData\}|\{GameInstallPath\}|\{LibraryRoot\}|\$HOME|\$XDG_CONFIG_HOME|~/|[A-Z]:\\)[^|\r\n<>\]]*",
            RegexOptions.Compiled);

        public List<SavePathImportItem> ExtractCandidates(
            PcgwTitle title,
            string wikitext)
        {
            string? saveSection = ExtractSaveSection(wikitext);

            if (string.IsNullOrWhiteSpace(saveSection))
                return new List<SavePathImportItem>();

            string normalizedText = NormalizePcgwWikitext(saveSection);

            var results = new List<SavePathImportItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string[] lines = normalizedText.Split(
                new[] { "\r\n", "\n" },
                StringSplitOptions.None);

            foreach (string line in lines)
            {
                string platform = InferPlatformFromLine(line);

                foreach (Match match in PathCandidateRegex.Matches(line))
                {
                    string path = CleanPathCandidate(match.Value);

                    if (!LooksLikeUsablePath(path))
                        continue;

                    platform = platform == "unknown"
                        ? InferPlatformFromPath(path)
                        : platform;

                    foreach (string steamAppId in title.SteamAppIds)
                    {
                        string key = $"{steamAppId}|{platform}|{path}";

                        if (!seen.Add(key))
                            continue;

                        results.Add(new SavePathImportItem(
                            SteamAppId: steamAppId,
                            GameName: title.DisplayTitle ?? title.PageName.Replace('_', ' '),
                            Platform: platform,
                            PathTemplate: path,
                            PathKind: "Directory",
                            SourceName: "PCGamingWiki-AutoExtracted",
                            SourceUrl: title.SourceUrl,
                            SourceLicense: "CC-BY-NC-SA unless otherwise noted",
                            Notes: "Auto-extracted from PCGamingWiki wikitext. Disabled by default; review before enabling.",
                            Priority: 80));
                    }
                }
            }

            return results
                .OrderBy(result => result.SteamAppId)
                .ThenBy(result => result.Platform)
                .ThenBy(result => result.PathTemplate)
                .ToList();
        }

        private static string? ExtractSaveSection(string wikitext)
        {
            Match heading = SaveSectionHeadingRegex.Match(wikitext);

            if (!heading.Success)
                return null;

            int start = heading.Index + heading.Length;
            Match nextHeading = AnyHeadingRegex.Match(wikitext, start);

            int end = nextHeading.Success
                ? nextHeading.Index
                : wikitext.Length;

            return wikitext[start..end];
        }

        private static string NormalizePcgwWikitext(string value)
        {
            string result = value;

            result = ReplacePathTemplate(result, "appdata", "%APPDATA%");
            result = ReplacePathTemplate(result, "localappdata", "%LOCALAPPDATA%");
            result = ReplacePathTemplate(result, "userprofile", "%USERPROFILE%");
            result = ReplacePathTemplate(result, "winuserprofile", "%USERPROFILE%");
            result = ReplacePathTemplate(result, "programdata", "%PROGRAMDATA%");
            result = ReplacePathTemplate(result, "documents", "%DOCUMENTS%");
            result = ReplacePathTemplate(result, "savedgames", "{SavedGames}");
            result = ReplacePathTemplate(result, "steam", "{SteamRoot}");
            result = ReplacePathTemplate(result, "steamapps", "{LibraryRoot}\\steamapps");
            result = ReplacePathTemplate(result, "uid", "*");
            result = ReplacePathTemplate(result, "game", "{GameInstallPath}");
            result = ReplacePathTemplate(result, "path-to-game", "{GameInstallPath}");
            result = ReplacePathTemplate(result, "linuxhome", "$HOME");
            result = ReplacePathTemplate(result, "xdgconfig", "$XDG_CONFIG_HOME");
            result = ReplacePathTemplate(result, "macoshome", "$HOME");
            result = ReplacePathTemplate(result, "macosappsupport", "$HOME/Library/Application Support");

            result = result.Replace("<path-to-game>", "{GameInstallPath}", StringComparison.OrdinalIgnoreCase);
            result = result.Replace("<Steam-folder>", "{SteamRoot}", StringComparison.OrdinalIgnoreCase);
            result = result.Replace("<SteamLibrary-folder>", "{LibraryRoot}", StringComparison.OrdinalIgnoreCase);
            result = result.Replace("<user-id>", "*", StringComparison.OrdinalIgnoreCase);
            result = result.Replace("<Steam-user-id>", "*", StringComparison.OrdinalIgnoreCase);

            result = Regex.Replace(
                result,
                @"\[\[[^\]|]+\|([^\]]+)\]\]",
                "$1",
                RegexOptions.IgnoreCase);

            result = Regex.Replace(
                result,
                @"\[\[([^\]]+)\]\]",
                "$1",
                RegexOptions.IgnoreCase);

            result = Regex.Replace(
                result,
                @"<ref[^>]*>.*?</ref>",
                "",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            result = Regex.Replace(
                result,
                @"<ref[^/]*/>",
                "",
                RegexOptions.IgnoreCase);

            result = result.Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase);
            result = result.Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase);
            result = result.Replace("<br />", "\n", StringComparison.OrdinalIgnoreCase);

            return result;
        }

        private static string ReplacePathTemplate(
            string value,
            string pcgwToken,
            string replacement)
        {
            return Regex.Replace(
                value,
                @"\{\{\s*p\s*\|\s*" + Regex.Escape(pcgwToken) + @"\s*\}\}",
                replacement,
                RegexOptions.IgnoreCase);
        }

        private static string CleanPathCandidate(string value)
        {
            string result = value.Trim();

            int commentIndex = result.IndexOf("<!--", StringComparison.Ordinal);
            if (commentIndex >= 0)
                result = result[..commentIndex];

            result = result.Trim();
            result = result.TrimEnd('.', ',', ';', ':');
            result = result.Replace("/", "\\");

            while (result.Contains("\\\\", StringComparison.Ordinal))
                result = result.Replace("\\\\", "\\", StringComparison.Ordinal);

            result = result.Replace("{{", string.Empty).Replace("}}", string.Empty);

            return result.Trim();
        }

        private static bool LooksLikeUsablePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            if (path.Length < 4)
                return false;

            if (path.Contains("citation needed", StringComparison.OrdinalIgnoreCase))
                return false;

            if (path.Contains("unknown", StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        private static string InferPlatformFromLine(string line)
        {
            if (line.Contains("Windows", StringComparison.OrdinalIgnoreCase))
                return "windows";

            if (line.Contains("Linux", StringComparison.OrdinalIgnoreCase))
                return "linux";

            if (line.Contains("macOS", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("OS X", StringComparison.OrdinalIgnoreCase))
                return "macos";

            return "unknown";
        }

        private static string InferPlatformFromPath(string pathTemplate)
        {
            if (pathTemplate.StartsWith("$HOME", StringComparison.OrdinalIgnoreCase) ||
                pathTemplate.StartsWith("$XDG_", StringComparison.OrdinalIgnoreCase) ||
                pathTemplate.StartsWith("~/", StringComparison.OrdinalIgnoreCase))
            {
                return "linux";
            }

            if (pathTemplate.Contains("Library\\Application Support", StringComparison.OrdinalIgnoreCase))
                return "macos";

            return "windows";
        }
    }
}