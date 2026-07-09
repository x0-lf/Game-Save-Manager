namespace GameSaves.Core.Save
{
    public enum GameSaveStatusKind
    {
        Unknown = 0,

        Ready = 1,

        MappingMissing = 2,

        NeedsFixOnly = 3,

        PathMissing = 4,

        NotInstalledClean = 5,

        Error = 6
    }
}