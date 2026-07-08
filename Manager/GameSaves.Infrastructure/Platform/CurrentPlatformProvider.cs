using GameSaves.Core.Platform;

namespace GameSaves.Infrastructure.Platform
{
    public sealed class CurrentPlatformProvider : ICurrentPlatformProvider
    {
        public string GetCurrentPlatformKey()
        {
            if (OperatingSystem.IsWindows())
                return "windows";

            if (OperatingSystem.IsLinux())
                return "linux";

            if (OperatingSystem.IsMacOS())
                return "macos";

            return "unknown";
        }
    }
}