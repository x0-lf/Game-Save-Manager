using GameSaves.Core.Platform;
using GameSaves.Infrastructure.Save;

namespace GameSaves.Infrastructure.Platform
{
    // The desktop application historically assumed the database schema
    // already existed, because only the CLI called SavePathDatabase
    // .Initialize(). On a machine that has only ever run the desktop app the
    // file was created empty and every query failed with "no such table".
    // This decorator is the single choke point every database consumer in the
    // DI graph resolves the path through, so wrapping it guarantees the
    // schema exists before the first connection, exactly once per distinct
    // path, without any repository having to know about bootstrapping.
    public sealed class SchemaInitializingAppDatabasePathProvider
        : IAppDatabasePathProvider
    {
        private readonly IAppDatabasePathProvider _inner;
        private readonly object _gate = new();
        private readonly HashSet<string> _initializedPaths = new();

        public SchemaInitializingAppDatabasePathProvider(
            IAppDatabasePathProvider inner)
        {
            _inner = inner;
        }

        public string GetDatabasePath()
        {
            string path = _inner.GetDatabasePath();

            lock (_gate)
            {
                if (_initializedPaths.Add(path))
                {
                    new SavePathDatabase(path).Initialize();
                }
            }

            return path;
        }
    }
}
