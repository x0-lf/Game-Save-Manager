using GameSaves.Core.Transfers;

namespace GameSaves.App.Models
{
    public sealed class RestoreMappingOptionRowViewModel
    {
        public RestoreMappingOptionRowViewModel(RestoreMappingTargetOption option)
        {
            Option = option;
        }

        public RestoreMappingTargetOption Option { get; }

        public long MappingId => Option.MappingId;

        public string PathTemplate => Option.PathTemplate;

        public string? ResolvedPath => Option.ResolvedPath;

        public bool CanUse => Option.CanUse;

        public string StatusText => Option.StatusText;

        public string ComboDisplay => Option.CanUse
            ? $"{Option.PathTemplate}  →  {Option.ResolvedPath}"
            : $"{Option.PathTemplate}  ({Option.StatusText})";

        public override string ToString() => ComboDisplay;
    }
}
