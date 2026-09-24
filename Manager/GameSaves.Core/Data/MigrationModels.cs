namespace GameSaves.Core.Data
{
    public sealed record SchemaMigrationRecord(
        int Id,
        string Name,
        DateTimeOffset AppliedUtc);

    public sealed record SchemaMigrationInfo(
        int Version,
        string Name,
        string Description);

    public sealed record MigrationPlan(
        string DatabasePath,
        int CurrentVersion,
        int TargetVersion,
        IReadOnlyList<SchemaMigrationInfo> PendingMigrations,
        bool IntegrityCheckPassed,
        string? IntegrityMessage,
        string? PlannedBackupDirectory);

    public sealed record MigrationExecutionResult(
        bool Success,
        int PreviousVersion,
        int CurrentVersion,
        IReadOnlyList<string> AppliedMigrations,
        string? PreMigrationBackupPath,
        bool RolledBack,
        string? ErrorMessage);

    public interface ISchemaMigrator
    {
        MigrationPlan Plan(string databasePath);

        MigrationExecutionResult Migrate(string databasePath);

        string Backup(string databasePath);

        bool VerifyIntegrity(string databasePath, out string message);

        IReadOnlyList<SchemaMigrationRecord> GetAppliedMigrations(string databasePath);
    }
}
