using GameSaves.Core.Data;
using System.Data.Common;

namespace GameSaves.Infrastructure.Data.Migrations
{
    public sealed class V002__ReviewColumnsAndProvenance : ISchemaMigration
    {
        public int Version => 2;
        public string Name => "V002__ReviewColumnsAndProvenance";
        public string Description => "Add review columns, review status index, and ensure review status defaults to Pending.";

        public void Up(DbConnection connection, DbTransaction transaction)
        {
            EnsureColumn(connection, transaction, "save_path_mappings", "review_status", "TEXT NOT NULL DEFAULT 'Pending'");
            EnsureColumn(connection, transaction, "save_path_mappings", "reviewed_utc", "TEXT NULL");
            EnsureColumn(connection, transaction, "save_path_mappings", "review_notes", "TEXT NULL");

            using var indexCommand = connection.CreateCommand();
            indexCommand.Transaction = transaction;
            indexCommand.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_save_path_mappings_review_status
                ON save_path_mappings (source_name, review_status, enabled);
            """;
            indexCommand.ExecuteNonQuery();

            using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;
            updateCommand.CommandText = """
            UPDATE save_path_mappings
            SET review_status = 'Pending'
            WHERE review_status IS NULL;
            """;
            updateCommand.ExecuteNonQuery();
        }

        private static void EnsureColumn(
            DbConnection connection,
            DbTransaction transaction,
            string tableName,
            string columnName,
            string columnDefinition)
        {
            if (ColumnExists(connection, transaction, tableName, columnName))
                return;

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
            command.ExecuteNonQuery();
        }

        private static bool ColumnExists(
            DbConnection connection,
            DbTransaction transaction,
            string tableName,
            string columnName)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"PRAGMA table_info({tableName});";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string name = reader.GetString(reader.GetOrdinal("name"));
                if (string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
