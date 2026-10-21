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
        string? PlannedBackupDirectory)
    {
        /// <summary>
        /// Set when the database records a migration newer than this build
        /// knows: the words a window or command shows, and the reason
        /// <see cref="ISchemaMigrator.Migrate"/> will refuse without writing.
        /// Null for every database this build can open.
        /// </summary>
        public string? Refusal { get; init; }
    }

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
