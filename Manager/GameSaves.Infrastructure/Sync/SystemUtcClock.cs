using GameSaves.Core.Sync;

namespace GameSaves.Infrastructure.Sync
{
    public sealed class SystemUtcClock : IUtcClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
