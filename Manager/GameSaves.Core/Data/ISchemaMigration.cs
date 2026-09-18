using System.Data.Common;

namespace GameSaves.Core.Data
{
    public interface ISchemaMigration
    {
        int Version { get; }
        string Name { get; }
        string Description { get; }
        void Up(DbConnection connection, DbTransaction transaction);
    }
}
