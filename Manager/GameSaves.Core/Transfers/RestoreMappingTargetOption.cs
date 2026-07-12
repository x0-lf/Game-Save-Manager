namespace GameSaves.Core.Transfers
{
    /// <summary>
    /// One approved save-path mapping offered as a restore target, with its
    /// resolved path. CanUse is false when the mapping cannot serve as a
    /// single unambiguous target (unresolved, multiple paths, profile-specific).
    /// </summary>
    public sealed record RestoreMappingTargetOption(
        long MappingId,
        string PathTemplate,
        string? ResolvedPath,
        bool CanUse,
        string StatusText);
}
