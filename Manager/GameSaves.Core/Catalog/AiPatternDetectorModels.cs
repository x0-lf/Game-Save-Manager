using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GameSaves.Core.Catalog
{
    /// <summary>
    /// Recognized game engine families with known save path conventions.
    /// </summary>
    public enum GameEngineKind
    {
        Unknown,
        Custom,
        Unreal,
        Unity,
        Godot,
        RenPy,
        Source,
        RpgMaker
    }

    /// <summary>
    /// Confidence level in engine detection or save path proposal.
    /// </summary>
    public enum DetectionConfidence
    {
        Low,
        Medium,
        High,
        Certain
    }

    /// <summary>
    /// Parameters for analyzing a game directory structure.
    /// </summary>
    public sealed record AiDetectionRequest(
        string GameDirectory,
        string? SteamAppId = null,
        string? GameName = null,
        string? ModelName = null,
        bool OfflineOnly = false,
        int MaxDirectoryDepth = 3,
        int MaxFilesToInspect = 200);

    /// <summary>
    /// A single save path candidate proposed by AI or engine heuristics.
    /// <see cref="IsAiProposal"/> is true only for candidates taken from an AI completion.
    /// </summary>
    public sealed record AiCandidateProposal(
        string PathTemplate,
        string Platform,
        string PathKind,
        DetectionConfidence Confidence,
        GameEngineKind Engine,
        string Rationale,
        int Priority = 70,
        string? Notes = null,
        bool IsAiProposal = false);

    /// <summary>
    /// Complete analysis result from the AI pattern detector.
    /// Contains engine fingerprinting, candidate proposals, and audit provenance.
    /// </summary>
    public sealed record AiDetectionResult(
        string GameDirectory,
        string? SteamAppId,
        string GameName,
        GameEngineKind DetectedEngine,
        DetectionConfidence EngineConfidence,
        IReadOnlyList<string> EngineEvidence,
        IReadOnlyList<AiCandidateProposal> Proposals,
        string SanitizedDirectoryTree,
        string ModelVersion,
        string PromptHash,
        DateTimeOffset GeneratedUtc,
        bool IsOfflineHeuristic);

    /// <summary>
    /// Pluggable client contract for generating text completions from an AI model.
    /// Enables 100% offline, deterministic testing with canned responses.
    /// </summary>
    public interface IAiCompletionClient
    {
        Task<string> GenerateCompletionAsync(
            string prompt,
            string? systemPrompt = null,
            CancellationToken cancellationToken = default);
    }
}
