using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading.Tasks;

namespace GameSave
{
    public class SteamDiscoveryService
    {
        public static bool TryLocate(out string steamPath)
        {
            steamPath = string.Empty;

            if (!OperatingSystem.IsWindows())
                return false;

            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine,
                    RegistryView.Registry32);

                using RegistryKey? key = baseKey.OpenSubKey(SteamConstants.SteamSubKey);

                if (key?.GetValue(SteamConstants.InstallPathValue) is not string rawPath ||
                    string.IsNullOrWhiteSpace(rawPath))
                {
                    return false;
                }

                string expandedPath = Environment.ExpandEnvironmentVariables(rawPath);

                if (!Directory.Exists(expandedPath))
                    return false;

                steamPath = expandedPath;
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (SecurityException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }
        public static void ValidateLocation()
        {
            if (!TryLocate(out string steamPath))
            {
                Console.WriteLine("Steam was not found in the registry.");
                return;
            }

            Console.WriteLine($"Steam found at: {steamPath}");

            var fileTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "steam.exe",
                    "steam.dll"
                };

            var dirTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "steamapps",
                    "config"
                };

            foreach (string file in Directory.GetFiles(steamPath))
            {
                string name = Path.GetFileName(file);

                if (fileTargets.Contains(name))
                    Console.WriteLine($"Matched file: {name}");
            }

            foreach (string dir in Directory.GetDirectories(steamPath))
            {
                string dirName = Path.GetFileName(dir);

                if (dirTargets.Contains(dirName))
                    Console.WriteLine($"Matched directory: {dirName}");
            }
        }

    }
}
