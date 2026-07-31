using GameSaves.Infrastructure.Save;
using Microsoft.Data.Sqlite;

namespace GameSaves.Tests;

public sealed class SqliteDependencyRegressionTests
{
    [Fact]
    public void SavePathDatabase_InitializesNestedNewDatabaseAndReopensWithoutDataLoss()
    {
        using var temp = new TemporaryDirectory();
        string databasePath = temp.GetPath("nested", "data", "gamesave.db");
        var database = new SavePathDatabase(databasePath);

        database.Initialize();

        using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            connection.Open();
            using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO game_titles (steam_app_id, title, source_name)
                VALUES ('1234', 'Persistent Test Game', 'SecurityRegression');
                """;
            Assert.Equal(1, insert.ExecuteNonQuery());
        }

        database.Initialize();

        using var reopened = new SqliteConnection($"Data Source={databasePath}");
        reopened.Open();
        using SqliteCommand read = reopened.CreateCommand();
        read.CommandText = """
            SELECT title
            FROM game_titles
            WHERE steam_app_id = '1234';
            """;

        Assert.Equal("Persistent Test Game", read.ExecuteScalar());
    }

    [Fact]
    public void BundledSqliteRuntime_OpensConnectionAndReportsVersion()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";

        string version = Assert.IsType<string>(command.ExecuteScalar());

        Assert.Matches(@"^\d+\.\d+\.\d+$", version);
    }
}
