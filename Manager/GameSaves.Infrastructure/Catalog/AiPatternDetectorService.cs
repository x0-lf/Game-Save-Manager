using GameSaves.Core.Catalog;
using GameSaves.Core.Save;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GameSaves.Infrastructure.Catalog
{
    /// <summary>
    /// AI-assisted save path pattern detector service.
    /// Combines engine fingerprint heuristics with pluggable AI completion models
    /// to generate tokenized candidate save paths strictly under the Pending trust lifecycle.
    /// </summary>
    public sealed class AiPatternDetectorService : IAiPatternDetectorService
    {
        private readonly IAiCompletionClient? _aiClient;
        private readonly GameEngineFingerprinter _fingerprinter = new();
        private readonly DirectoryTreeSanitizer _sanitizer = new();

        public AiPatternDetectorService(IAiCompletionClient? aiClient = null)
        {
            _aiClient = aiClient;
        }

        public async Task<AiDetectionResult> DetectSavePathsAsync(
            AiDetectionRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            // 1. Sanitize directory tree
            SanitizedTreeResult sanitizedTree = _sanitizer.Sanitize(
                request.GameDirectory,
                request.MaxDirectoryDepth,
                request.MaxFilesToInspect);

            // 2. Detect engine fingerprint
            EngineFingerprintResult fingerprint = _fingerprinter.Detect(
                sanitizedTree.RelativePaths,
                request.GameDirectory);

            // 3. Determine effective game name
            string effectiveGameName = DetermineGameName(request, fingerprint);

            // 4. Generate engine-specific heuristic proposals
            var proposals = GenerateEngineHeuristics(
                fingerprint,
                effectiveGameName,
                request.SteamAppId);

            string modelVersion = "offline-heuristic";
            string promptHash = string.Empty;
            bool isOffline = true;

            // 5. Query AI model if available and not offline-only
            if (!request.OfflineOnly && _aiClient != null)
            {
                try
                {
                    string systemPrompt =
                        "You are an expert game save-path analyst for a PC game save manager application. " +
                        "Your job is to inspect sanitized game directory layouts and game engine signatures, " +
                        "and propose candidate save game locations on Windows using standard environment tokens: " +
                        "%LOCALAPPDATA%, %APPDATA%, %USERPROFILE%, {Documents}, {SavedGames}, {SteamRoot}, {GameInstallPath}. " +
                        "Always respond strictly with a valid JSON array of objects.";

                    string userPrompt = BuildAiPrompt(
                        effectiveGameName,
                        request.SteamAppId,
                        fingerprint,
                        sanitizedTree.FormattedTree);

                    promptHash = ComputeSha256(userPrompt);
                    modelVersion = string.IsNullOrWhiteSpace(request.ModelName) ? "ai-model" : request.ModelName;

                    string completion = await _aiClient.GenerateCompletionAsync(
                        userPrompt,
                        systemPrompt,
                        cancellationToken);

                    var aiProposals = ParseAiResponse(completion, fingerprint.Engine);
                    if (aiProposals.Count > 0)
                    {
                        proposals = MergeProposals(proposals, aiProposals);
                        isOffline = false;
                    }
                }
                catch
                {
                    // Fall back cleanly to offline heuristics on AI failures
                    isOffline = true;
                }
            }

            if (string.IsNullOrEmpty(promptHash))
            {
                promptHash = ComputeSha256(sanitizedTree.FormattedTree + "|" + fingerprint.Engine);
            }

            return new AiDetectionResult(
                GameDirectory: request.GameDirectory,
                SteamAppId: request.SteamAppId,
                GameName: effectiveGameName,
                DetectedEngine: fingerprint.Engine,
                EngineConfidence: fingerprint.Confidence,
                EngineEvidence: fingerprint.Evidence,
                Proposals: proposals,
                SanitizedDirectoryTree: sanitizedTree.FormattedTree,
                ModelVersion: modelVersion,
                PromptHash: promptHash,
                GeneratedUtc: DateTimeOffset.UtcNow,
                IsOfflineHeuristic: isOffline);
        }

        public List<SavePathImportItem> ToImportItems(
            AiDetectionResult result,
            int? overridePriority = null)
        {
            if (result == null)
                throw new ArgumentNullException(nameof(result));

            var items = new List<SavePathImportItem>();

            foreach (var proposal in result.Proposals)
            {
                int priority = overridePriority ?? proposal.Priority;
                string promptShort = result.PromptHash.Length >= 8 ? result.PromptHash[..8] : result.PromptHash;
                string notes = $"AI-assisted candidate ({result.DetectedEngine}, {result.ModelVersion}, prompt: {promptShort}). {proposal.Rationale}. Pending human review.";

                items.Add(new SavePathImportItem(
                    SteamAppId: result.SteamAppId ?? "0",
                    GameName: result.GameName,
                    Platform: proposal.Platform,
                    PathTemplate: proposal.PathTemplate,
                    PathKind: proposal.PathKind,
                    SourceName: "AI-PatternDetector",
                    SourceUrl: null,
                    SourceLicense: "Proprietary/Internal",
                    Notes: notes,
                    Priority: priority));
            }

            return items;
        }

        public MappingImportDocument ToImportDocument(
            AiDetectionResult result,
            int? overridePriority = null)
        {
            if (result == null)
                throw new ArgumentNullException(nameof(result));

            var titles = new List<TitleImportEntry>();
            if (!string.IsNullOrWhiteSpace(result.SteamAppId))
            {
                titles.Add(new TitleImportEntry(
                    SteamAppId: result.SteamAppId,
                    Title: result.GameName,
                    PlatformHint: "windows",
                    SourceName: "AI-PatternDetector",
                    Notes: $"Detected engine: {result.DetectedEngine} ({result.EngineConfidence})"));
            }

            var mappings = new List<MappingImportEntry>();
            foreach (var proposal in result.Proposals)
            {
                int priority = overridePriority ?? proposal.Priority;
                string promptShort = result.PromptHash.Length >= 8 ? result.PromptHash[..8] : result.PromptHash;
                string notes = $"AI-assisted candidate ({result.DetectedEngine}, {result.ModelVersion}, prompt: {promptShort}). {proposal.Rationale}.";

                mappings.Add(new MappingImportEntry(
                    SteamAppId: result.SteamAppId ?? "0",
                    GameName: result.GameName,
                    Platform: proposal.Platform,
                    PathTemplate: proposal.PathTemplate,
                    PathKind: proposal.PathKind,
                    SourceName: "AI-PatternDetector",
                    SourceUrl: null,
                    SourceLicense: "Proprietary/Internal",
                    Notes: notes,
                    Priority: priority,
                    ReviewStatus: "Pending")); // Strictly Pending
            }

            return new MappingImportDocument(
                SchemaVersion: 1,
                Titles: titles,
                Mappings: mappings);
        }

        private static string DetermineGameName(AiDetectionRequest request, EngineFingerprintResult fingerprint)
        {
            if (!string.IsNullOrWhiteSpace(request.GameName))
                return request.GameName;

            if (!string.IsNullOrWhiteSpace(fingerprint.ProductName))
                return fingerprint.ProductName;

            string dirName = Path.GetFileName(request.GameDirectory.TrimEnd('/', '\\'));
            if (!string.IsNullOrWhiteSpace(dirName))
                return dirName;

            return "Game";
        }

        private static List<AiCandidateProposal> GenerateEngineHeuristics(
            EngineFingerprintResult fingerprint,
            string gameName,
            string? steamAppId)
        {
            var list = new List<AiCandidateProposal>();

            switch (fingerprint.Engine)
            {
                case GameEngineKind.Unreal:
                    list.Add(new AiCandidateProposal(
                        PathTemplate: $@"%LOCALAPPDATA%\{gameName}\Saved\SaveGames",
                        Platform: "windows",
                        PathKind: "Directory",
                        Confidence: DetectionConfidence.High,
                        Engine: GameEngineKind.Unreal,
                        Rationale: "Standard Unreal Engine 4/5 local AppData save container.",
                        Priority: 75));

                    list.Add(new AiCandidateProposal(
                        PathTemplate: $@"{ "{Documents}" }\My Games\{gameName}\Saved\SaveGames",
                        Platform: "windows",
                        PathKind: "Directory",
                        Confidence: DetectionConfidence.Medium,
                        Engine: GameEngineKind.Unreal,
                        Rationale: "Unreal Engine My Games Documents save container.",
                        Priority: 70));

                    list.Add(new AiCandidateProposal(
                        PathTemplate: $@"{ "{GameInstallPath}" }\{gameName}\Saved\SaveGames",
                        Platform: "windows",
                        PathKind: "Directory",
                        Confidence: DetectionConfidence.Low,
                        Engine: GameEngineKind.Unreal,
                        Rationale: "Unreal Engine in-tree installation save folder.",
                        Priority: 60));
                    break;

                case GameEngineKind.Unity:
                    if (!string.IsNullOrWhiteSpace(fingerprint.CompanyName))
                    {
                        list.Add(new AiCandidateProposal(
                            PathTemplate: $@"%USERPROFILE%\AppData\LocalLow\{fingerprint.CompanyName}\{gameName}",
                            Platform: "windows",
                            PathKind: "Directory",
                            Confidence: DetectionConfidence.High,
                            Engine: GameEngineKind.Unity,
                            Rationale: $"Standard Unity persistentDataPath for {fingerprint.CompanyName}/{gameName}.",
                            Priority: 80));
                    }

                    list.Add(new AiCandidateProposal(
                        PathTemplate: $@"%USERPROFILE%\AppData\LocalLow\{gameName}\{gameName}",
                        Platform: "windows",
                        PathKind: "Directory",
                        Confidence: DetectionConfidence.Medium,
                        Engine: GameEngineKind.Unity,
                        Rationale: "Unity persistentDataPath default company/product pattern.",
                        Priority: 75));

                    list.Add(new AiCandidateProposal(
                        PathTemplate: $@"%LOCALAPPDATA%\{gameName}",
                        Platform: "windows",
                        PathKind: "Directory",
                        Confidence: DetectionConfidence.Low,
                        Engine: GameEngineKind.Unity,
                        Rationale: "Unity local AppData fallback.",
                        Priority: 60));

                    list.Add(new AiCandidateProposal(
                        PathTemplate: $@"{ "{Documents}" }\{gameName}",
                        Platform: "windows",
                        PathKind: "Directory",
                        Confidence: DetectionConfidence.Low,
                        Engine: GameEngineKind.Unity,
                        Rationale: "Unity Documents save location.",
                        Priority: 60));
                    break;

                case GameEngineKind.Godot:
                    list.Add(new AiCandidateProposal(
                        PathTemplate: $@"%APPDATA%\Godot\app_userdata\{gameName}",
                        Platform: "windows",
                        PathKind: "Directory",
                        Confidence: DetectionConfidence.High,
                        Engine: GameEngineKind.Godot,
                        Rationale: "Standard Godot user:// directory on Windows.",
                        Priority: 85));

                    list.Add(new AiCandidateProposal(
                        PathTemplate: $@"%LOCALAPPDATA%\{gameName}",
                        Platform: "windows",
                        PathKind: "Directory",
                        Confidence: DetectionConfidence.Medium,
                        Engine: GameEngineKind.Godot,
                        Rationale: "Godot secondary AppData location.",
                        Priority: 65));
                    break;

                case GameEngineKind.RenPy:
                    list.Add(new AiCandidateProposal(
                        PathTemplate: $@"%APPDATA%\RenPy\{gameName}",
                        Platform: "windows",
                        PathKind: "Directory",
                        Confidence: DetectionConfidence.High,
                        Engine: GameEngineKind.RenPy,
                        Rationale: "Standard Ren'Py persistent save container in %APPDATA%.",
                        Priority: 85));

                    list.Add(new AiCandidateProposal(
                        PathTemplate: $@"{ "{GameInstallPath}" }\game\saves",
                        Platform: "windows",
                        PathKind: "Directory",
                        Confidence: DetectionConfidence.Medium,
                        Engine: GameEngineKind.RenPy,
                        Rationale: "Ren'Py local game directory save folder.",
                        Priority: 75));
                    break;

                case GameEngineKind.Source:
                    list.Add(new AiCandidateProposal(
                        PathTemplate: $@"{ "{GameInstallPath}" }\{gameName}\save",
                        Platform: "windows",
                        PathKind: "Directory",
                        Confidence: DetectionConfidence.High,
                        Engine: GameEngineKind.Source,
                        Rationale: "Source Engine standard save directory inside mod folder.",
                        Priority: 80));

                    if (!string.IsNullOrWhiteSpace(steamAppId) && steamAppId != "0")
                    {
                        list.Add(new AiCandidateProposal(
                            PathTemplate: $@"{ "{SteamRoot}" }\userdata\{ "{AccountId}" }\{steamAppId}\remote",
                            Platform: "windows",
                            PathKind: "Directory",
                            Confidence: DetectionConfidence.High,
                            Engine: GameEngineKind.Source,
                            Rationale: "Source Engine Steam Cloud remote save directory.",
                            Priority: 85));
                    }
                    break;

                case GameEngineKind.RpgMaker:
                    list.Add(new AiCandidateProposal(
                        PathTemplate: $@"{ "{GameInstallPath}" }\save",
                        Platform: "windows",
                        PathKind: "Directory",
                        Confidence: DetectionConfidence.High,
                        Engine: GameEngineKind.RpgMaker,
                        Rationale: "Standard RPG Maker local save folder.",
                        Priority: 80));

                    list.Add(new AiCandidateProposal(
                        PathTemplate: $@"{ "{GameInstallPath}" }\www\save",
                        Platform: "windows",
                        PathKind: "Directory",
                        Confidence: DetectionConfidence.Medium,
                        Engine: GameEngineKind.RpgMaker,
                        Rationale: "RPG Maker MV/MZ web/NW.js save folder.",
                        Priority: 75));

                    list.Add(new AiCandidateProposal(
                        PathTemplate: $@"%LOCALAPPDATA%\{gameName}\save",
                        Platform: "windows",
                        PathKind: "Directory",
                        Confidence: DetectionConfidence.Medium,
                        Engine: GameEngineKind.RpgMaker,
                        Rationale: "RPG Maker AppData save container.",
                        Priority: 70));
                    break;

                default:
                    list.Add(new AiCandidateProposal(
                        PathTemplate: $@"{ "{Documents}" }\My Games\{gameName}",
                        Platform: "windows",
                        PathKind: "Directory",
                        Confidence: DetectionConfidence.Medium,
                        Engine: GameEngineKind.Custom,
                        Rationale: "Standard Windows My Games directory.",
                        Priority: 60));

                    list.Add(new AiCandidateProposal(
                        PathTemplate: $@"%LOCALAPPDATA%\{gameName}",
                        Platform: "windows",
                        PathKind: "Directory",
                        Confidence: DetectionConfidence.Medium,
                        Engine: GameEngineKind.Custom,
                        Rationale: "Standard Windows LocalAppData directory.",
                        Priority: 60));

                    list.Add(new AiCandidateProposal(
                        PathTemplate: $@"{ "{GameInstallPath}" }\saves",
                        Platform: "windows",
                        PathKind: "Directory",
                        Confidence: DetectionConfidence.Low,
                        Engine: GameEngineKind.Custom,
                        Rationale: "Generic in-tree save folder.",
                        Priority: 50));
                    break;
            }

            return list;
        }

        private static string BuildAiPrompt(
            string gameName,
            string? steamAppId,
            EngineFingerprintResult fingerprint,
            string directoryTree)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Game Name: {gameName}");
            sb.AppendLine($"Steam AppID: {steamAppId ?? "None"}");
            sb.AppendLine($"Detected Engine: {fingerprint.Engine} (Confidence: {fingerprint.Confidence})");
            if (fingerprint.Evidence.Count > 0)
            {
                sb.AppendLine($"Engine Evidence: {string.Join(", ", fingerprint.Evidence)}");
            }
            if (!string.IsNullOrWhiteSpace(fingerprint.CompanyName))
            {
                sb.AppendLine($"Company Name: {fingerprint.CompanyName}");
            }
            sb.AppendLine();
            sb.AppendLine("Sanitized Directory Structure:");
            sb.AppendLine(directoryTree);
            sb.AppendLine();
            sb.AppendLine("Instructions:");
            sb.AppendLine("Propose 1 to 4 candidate save game paths for Windows. Use standard tokens (%LOCALAPPDATA%, %APPDATA%, %USERPROFILE%, {Documents}, {SavedGames}, {SteamRoot}, {GameInstallPath}).");
            sb.AppendLine("Format your response strictly as a JSON array:");
            sb.AppendLine("[");
            sb.AppendLine("  {");
            sb.AppendLine("    \"pathTemplate\": \"%LOCALAPPDATA%\\\\Game\\\\Saved\\\\SaveGames\",");
            sb.AppendLine("    \"platform\": \"windows\",");
            sb.AppendLine("    \"pathKind\": \"Directory\",");
            sb.AppendLine("    \"confidence\": \"High\",");
            sb.AppendLine("    \"rationale\": \"Reason for this path\"");
            sb.AppendLine("  }");
            sb.AppendLine("]");

            return sb.ToString();
        }

        private static List<AiCandidateProposal> ParseAiResponse(string completion, GameEngineKind engine)
        {
            var result = new List<AiCandidateProposal>();

            if (string.IsNullOrWhiteSpace(completion))
                return result;

            string json = completion.Trim();

            // Strip markdown code fences if present (e.g. ```json ... ```)
            if (json.StartsWith("```", StringComparison.Ordinal))
            {
                int firstNewline = json.IndexOf('\n');
                if (firstNewline >= 0)
                {
                    json = json[(firstNewline + 1)..];
                }
                if (json.EndsWith("```", StringComparison.Ordinal))
                {
                    json = json[..^3].TrimEnd();
                }
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    return result;

                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    string? pathTemplate = el.TryGetProperty("pathTemplate", out var pt) ? pt.GetString() : null;
                    if (string.IsNullOrWhiteSpace(pathTemplate))
                        continue;

                    string platform = el.TryGetProperty("platform", out var p) ? (p.GetString() ?? "windows") : "windows";
                    string pathKind = el.TryGetProperty("pathKind", out var pk) ? (pk.GetString() ?? "Directory") : "Directory";
                    string confidenceStr = el.TryGetProperty("confidence", out var c) ? (c.GetString() ?? "Medium") : "Medium";
                    string rationale = el.TryGetProperty("rationale", out var r) ? (r.GetString() ?? "AI proposed candidate") : "AI proposed candidate";

                    if (!Enum.TryParse(confidenceStr, ignoreCase: true, out DetectionConfidence confidence))
                        confidence = DetectionConfidence.Medium;

                    result.Add(new AiCandidateProposal(
                        PathTemplate: pathTemplate.Replace('/', '\\'),
                        Platform: platform.ToLowerInvariant(),
                        PathKind: pathKind,
                        Confidence: confidence,
                        Engine: engine,
                        Rationale: rationale,
                        Priority: confidence == DetectionConfidence.High ? 75 : 65));
                }
            }
            catch
            {
                // Return whatever could be extracted or empty
            }

            return result;
        }

        private static List<AiCandidateProposal> MergeProposals(
            List<AiCandidateProposal> heuristics,
            List<AiCandidateProposal> aiProposals)
        {
            var merged = new List<AiCandidateProposal>(heuristics);
            var seen = new HashSet<string>(
                heuristics.Select(h => h.PathTemplate),
                StringComparer.OrdinalIgnoreCase);

            foreach (var ai in aiProposals)
            {
                if (seen.Add(ai.PathTemplate))
                {
                    merged.Add(ai);
                }
            }

            return merged;
        }

        private static string ComputeSha256(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            byte[] hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash);
        }
    }
}
