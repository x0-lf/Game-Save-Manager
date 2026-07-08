namespace GameSaves.External
{
    public sealed record PcgwHarvestResult(
        int TitlesIndexed,
        int TitlesProcessed,
        int TitlesFailed,
        int MappingsExtracted);
}