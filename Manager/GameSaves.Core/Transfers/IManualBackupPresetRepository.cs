namespace GameSaves.Core.Transfers
{
    /// <summary>
    /// Stores named manual-backup presets. Names are unique case-insensitively;
    /// saving an existing name updates that preset.
    /// </summary>
    public interface IManualBackupPresetRepository
    {
        IReadOnlyList<ManualBackupPreset> GetAll();

        ManualBackupPreset Save(ManualBackupPreset preset);

        void Delete(long id);

        void MarkUsed(long id);
    }
}
