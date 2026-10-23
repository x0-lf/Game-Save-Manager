using System.Text;
using GameSaves.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace GameSaves.Tests;

// The seed database the package ships is exported from a developer's real
// database, so the exporter is the boundary that keeps secrets, profiles,
// jobs, history and personal paths out of the installer. These tests run it
// against the 0.1.0 fixture, never against a real database.
public sealed class SeedDatabaseExportTests : IDisposable
{
    private static readonly string FixtureV010 = Path.Combine(
        DocumentationIntegrityTests.FindRepoRoot(), "Manager", "GameSaves.Tests", "Fixtures", "database", "v0.1.0.sql");

    private readonly TemporaryDirectory _temp = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    [Fact]
    public void TheExport_KeepsOnlyTheCatalog_AndLeavesNoByteOfTheRest()
    {
        string source = _temp.GetPath("source.db");
        SqliteScript.Restore(File.ReadAllText(FixtureV010), source);
        string output = Path.Combine(_temp.GetPath("seed"), "gamesave.db");

        SeedDatabaseExportResult result = SeedDatabaseExporter.Export(source, output, userName: "fixture-user");

        Assert.True(result.Written, string.Join("\n", result.Findings));
        Assert.Equal(7L, result.KeptRows["schema_migrations"]);
        Assert.Equal(21L, result.KeptRows["save_path_mappings"]);
        Assert.Equal(21L, result.KeptRows["game_titles"]);
        Assert.Equal(12, result.EmptiedTables.Count);
        Assert.Contains("protected_sync_secrets", result.EmptiedTables);
        Assert.Contains("sync_remote_profiles", result.EmptiedTables);
        Assert.Contains("transfer_runs", result.EmptiedTables);

        // Every table is still there (the app expects the whole schema), the
        // emptied ones with no rows, and the catalog intact.
        Assert.Equal(15L, MigratedDatabase.Scalar(output, "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'"));
        foreach (string table in result.EmptiedTables)
            Assert.Equal(0L, MigratedDatabase.Scalar(output, $"SELECT count(*) FROM \"{table}\""));
        Assert.Equal(21L, MigratedDatabase.Scalar(output, "SELECT count(*) FROM game_titles"));

        // The file was rewritten: the secret placeholder and the remote profile's
        // id (which the dropped secret row also carried) are not in its bytes.
        SqliteConnection.ClearAllPools();
        byte[] bytes = File.ReadAllBytes(output);
        Assert.Equal(result.OutputBytes, bytes.Length);
        Assert.Equal(-1, bytes.AsSpan().IndexOf(Convert.FromHexString("00112233445566778899AABBCCDDEEFF")));
        Assert.DoesNotContain("1b9c4e2a-6d7f-4a30-8c15-2e4f6a8b0c1d", Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);
        Assert.False(File.Exists(output + ".exporting"));
    }

    // `\Users\*\` and `C:\Users\Public\` are legitimate templates, so only the
    // exporting user's own name is refused; it is what a profile path from the
    // exporting machine always carries.
    [Fact]
    public void TheExport_RefusesTheExportingUsersName_AndWritesNothing()
    {
        string source = _temp.GetPath("personal.db");
        SqliteScript.Restore(File.ReadAllText(FixtureV010), source);
        MigratedDatabase.Execute(
            source,
            @"UPDATE save_path_mappings SET notes = 'seen at C:\Users\Someone\Saved Games' WHERE id = (SELECT min(id) FROM save_path_mappings)");
        string output = Path.Combine(_temp.GetPath("seed-refused"), "gamesave.db");

        Assert.True(SeedDatabaseExporter.Export(source, Path.Combine(_temp.GetPath("seed-other"), "gamesave.db"), userName: "fixture-user").Written);

        SeedDatabaseExportResult result = SeedDatabaseExporter.Export(source, output, userName: "someone");

        Assert.False(result.Written);
        Assert.Contains(result.Findings, finding =>
            finding.StartsWith("save_path_mappings.notes rowid ", StringComparison.Ordinal) &&
            finding.Contains("contains 'someone': seen at C:\\Users\\Someone\\Saved Games", StringComparison.Ordinal));
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(output + ".exporting"));
    }
}
