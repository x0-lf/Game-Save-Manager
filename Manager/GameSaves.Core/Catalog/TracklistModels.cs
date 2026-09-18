using System;
using System.Collections.Generic;

namespace GameSaves.Core.Catalog
{
    /// <summary>
    /// Categorizes the current catalog research state for a missing game title.
    /// </summary>
    public enum MissingTitleResearchStatus
    {
        /// <summary>
        /// Game has zero known save path mappings in the database and requires research.
        /// </summary>
        Unresearched,

        /// <summary>
        /// Game has candidate mappings in the database awaiting review and approval.
        /// </summary>
        InReview,

        /// <summary>
        /// Game has been verified to have no local save files (e.g. server-side only).
        /// </summary>
        NoSaveLocation
    }

    /// <summary>
    /// Export format for missing title tracklists.
    /// </summary>
    public enum TracklistExportFormat
    {
        Json,
        Csv
    }

    /// <summary>
    /// Candidate game title to reconcile against the save path database.
    /// Local filesystem paths are strictly excluded to prevent private path leaks.
    /// </summary>
    public sealed record MissingTitleCandidate(
        string SteamAppId,
        string Title,
        bool IsInstalled = false,
        string? Priority = null,
        string? Source = null,
        string? Notes = null);

    /// <summary>
    /// Actionable entry in a missing titles tracklist.
    /// Contains only public metadata; private paths and personal user identifiers are strictly excluded.
    /// </summary>
    public sealed record MissingTitleEntry(
        string SteamAppId,
        string Title,
        string StoreUrl,
        MissingTitleResearchStatus ResearchStatus,
        string Priority,
        bool IsInstalled,
        int ExistingCandidateCount,
        DateTimeOffset DiscoveredUtc,
        string? Notes = null);

    /// <summary>
    /// Complete reconciled tracklist of game titles missing save path coverage.
    /// </summary>
    public sealed record MissingTitlesTracklist(
        DateTimeOffset GeneratedUtc,
        int TotalReconciled,
        int TotalCovered,
        int TotalMissing,
        int UnresearchedCount,
        int InReviewCount,
        int NoSaveLocationCount,
        IReadOnlyList<MissingTitleEntry> Items);

    /// <summary>
    /// Filtering and execution options for generating a missing titles tracklist.
    /// </summary>
    public sealed record TracklistOptions(
        bool IncludeInstalledOnly = false,
        MissingTitleResearchStatus? StatusFilter = null,
        string? MinPriority = null,
        int? Limit = null,
        string Platform = "windows");
}
