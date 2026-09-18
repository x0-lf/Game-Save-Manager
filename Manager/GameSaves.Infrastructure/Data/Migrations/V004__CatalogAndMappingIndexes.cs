using GameSaves.Core.Data;
using System.Data.Common;

namespace GameSaves.Infrastructure.Data.Migrations
{
    public sealed class V004__CatalogAndMappingIndexes : ISchemaMigration
    {
        public int Version => 4;
        public string Name => "V004__CatalogAndMappingIndexes";
        public string Description => "Add performance indexes on game_titles and save_path_mappings for curated provenance queries.";

        public void Up(DbConnection connection, DbTransaction transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_game_titles_source
                ON game_titles(source_name);

            CREATE INDEX IF NOT EXISTS idx_save_path_mappings_source_enabled
                ON save_path_mappings(source_name, enabled);
            """;
            command.ExecuteNonQuery();
        }
    }
}
