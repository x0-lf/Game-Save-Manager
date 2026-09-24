using GameSaves.Infrastructure.Save;
using Microsoft.Data.Sqlite;

namespace GameSaves.Tests;

// The seeder, the importer and the mapping repository expect a migrated
// database, exactly as every production entry point provides one.
internal static class MigratedDatabase
{
    public static string Create(TemporaryDirectory temp, string fileName)
    {
        string path = temp.GetPath(fileName);
        new SavePathDatabase(path).Initialize();
        return path;
    }

    public static long Scalar(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
    }

    public static void Execute(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
