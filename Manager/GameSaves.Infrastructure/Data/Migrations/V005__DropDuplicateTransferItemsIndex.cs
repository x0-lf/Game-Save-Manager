using GameSaves.Core.Data;
using System.Data.Common;

namespace GameSaves.Infrastructure.Data.Migrations
{
    public sealed class V005__DropDuplicateTransferItemsIndex : ISchemaMigration
    {
        public int Version => 5;
        public string Name => "V005__DropDuplicateTransferItemsIndex";
        public string Description => "Drop idx_transfer_items_run, a duplicate of the transfer history's idx_transfer_items_run_id.";

        public void Up(DbConnection connection, DbTransaction transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DROP INDEX IF EXISTS idx_transfer_items_run;";
            command.ExecuteNonQuery();
        }
    }
}
