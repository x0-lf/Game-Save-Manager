using Microsoft.Data.Sqlite;

namespace GameSaves.Infrastructure.Data
{
    /// <summary>
    /// Exports the catalog of a database (<c>schema_migrations</c>,
    /// <c>save_path_mappings</c>, <c>game_titles</c>) as the seed database the
    /// package ships beside the program and the application copies on its first
    /// start when no database exists yet. Every other table is emptied and the
    /// file is rewritten, so no byte of secrets, remote profiles, scheduled
    /// jobs, history, backups, verification results or harvest caches survives.
    /// Nothing is written when a kept row carries the exporting user's name,
    /// the one thing a profile path from this machine always contains (while
    /// <c>\Users\*\</c> or <c>C:\Users\Public\</c> are legitimate templates);
    /// the findings name the rows to fix first.
    /// </summary>
    public static class SeedDatabaseExporter
    {
        public static readonly string[] CatalogTables = ["schema_migrations", "save_path_mappings", "game_titles"];

        public static SeedDatabaseExportResult Export(string sourcePath, string outputPath, string? userName = null)
        {
            userName ??= Environment.UserName;
            string fullOutput = Path.GetFullPath(outputPath);
            string temp = fullOutput + ".exporting";
            Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
            if (File.Exists(temp))
                File.Delete(temp);

            // A consistent snapshot of the live file through a read-only connection.
            using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = sourcePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString()))
            {
                source.Open();
                using SqliteCommand vacuum = source.CreateCommand();
                vacuum.CommandText = "VACUUM INTO @path";
                vacuum.Parameters.AddWithValue("@path", temp);
                vacuum.ExecuteNonQuery();
            }

            var kept = new Dictionary<string, long>(StringComparer.Ordinal);
            var emptied = new List<string>();
            var findings = new List<string>();

            using (var copy = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = temp,
                Pooling = false
            }.ToString()))
            {
                copy.Open();
                Execute(copy, "PRAGMA foreign_keys = OFF");

                foreach (string table in Query(copy, "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name"))
                {
                    if (CatalogTables.Contains(table, StringComparer.OrdinalIgnoreCase))
                    {
                        kept[table] = Scalar(copy, $"SELECT count(*) FROM \"{table}\"");
                        findings.AddRange(PersonalRows(copy, table, userName));
                    }
                    else
                    {
                        Execute(copy, $"DELETE FROM \"{table}\"");
                        emptied.Add(table);
                    }
                }

                if (Scalar(copy, "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'sqlite_sequence'") == 1)
                    Execute(copy, "DELETE FROM sqlite_sequence WHERE name NOT IN ('schema_migrations', 'save_path_mappings', 'game_titles')");

                // Rewrites the file from scratch: freed pages, and what they held, are gone.
                if (findings.Count == 0)
                    Execute(copy, "VACUUM");
            }

            if (findings.Count > 0)
            {
                File.Delete(temp);
                return new SeedDatabaseExportResult(kept, emptied, findings, 0);
            }

            File.Move(temp, fullOutput, overwrite: true);
            return new SeedDatabaseExportResult(kept, emptied, findings, new FileInfo(fullOutput).Length);
        }

        private static IEnumerable<string> PersonalRows(SqliteConnection connection, string table, string userName)
        {
            string needle = userName.Trim();
            if (needle.Length < 3)
                yield break;   // shortcut: a one- or two-letter user name would flag half the catalog; export from a normal account

            foreach (string column in Query(connection, $"SELECT name FROM pragma_table_info('{table}')"))
            {
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText =
                    $"SELECT rowid, CAST(\"{column}\" AS TEXT) FROM \"{table}\" " +
                    $"WHERE instr(lower(CAST(\"{column}\" AS TEXT)), lower(@needle)) > 0 LIMIT 20";
                command.Parameters.AddWithValue("@needle", needle);
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    string value = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    if (value.Length > 80)
                        value = value[..80] + "...";
                    yield return $"{table}.{column} rowid {reader.GetInt64(0)} contains '{needle}': {value}";
                }
            }
        }

        private static void Execute(SqliteConnection connection, string sql)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private static long Scalar(SqliteConnection connection, string sql)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }

        private static string[] Query(SqliteConnection connection, string sql)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            using SqliteDataReader reader = command.ExecuteReader();
            var values = new List<string>();
            while (reader.Read())
                values.Add(reader.GetString(0));
            return [.. values];
        }
    }

    /// <summary>What <see cref="SeedDatabaseExporter.Export"/> kept, emptied and found; the file exists only when <see cref="Written"/>.</summary>
    public sealed record SeedDatabaseExportResult(
        IReadOnlyDictionary<string, long> KeptRows,
        IReadOnlyList<string> EmptiedTables,
        IReadOnlyList<string> Findings,
        long OutputBytes)
    {
        public bool Written => Findings.Count == 0;
    }
}
