namespace GameSaves.Core.Save
{
    public interface ISavePathMappingRepository
    {
        IReadOnlyList<SavePathMapping> GetApprovedMappingsForApp(
            string steamAppId,
            string platform);

        IReadOnlyList<SavePathMapping> GetMappingsForApp(
            string steamAppId,
            string platform,
            bool includeDisabled);

        IReadOnlyDictionary<string, SavePathMappingStatus> GetMappingStatusesForApps(
            IEnumerable<string> steamAppIds,
            string platform);

        int CountApprovedMappings(string platform);

        int CountNeedsFixMappings(string platform);

        int CountPendingMappings(string platform);
    }
}