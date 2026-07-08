namespace GameSaves.Core.Save
{
    public sealed record SavePathMappingStatus(
        string SteamAppId,
        int TotalMappings,
        int EnabledMappings,
        int ApprovedMappings,
        int PendingMappings,
        int NeedsFixMappings,
        int RejectedMappings)
    {
        public bool HasUsableMapping => EnabledMappings > 0;

        public bool HasNeedsFixOnly =>
            EnabledMappings == 0 &&
            NeedsFixMappings > 0;

        public bool HasAnyMapping => TotalMappings > 0;
    }
}