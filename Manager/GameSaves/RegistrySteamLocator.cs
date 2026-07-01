using Microsoft.Win32;
using System;
using System.IO;
using System.Security;

namespace GameSave
{
    public sealed class RegistrySteamLocator
    {
        public bool TryLocate(out string steamPath)
        {
            steamPath = string.Empty;

            if (!OperatingSystem.IsWindows())
                return false;

            return TryReadRegistryPath(RegistryHive.LocalMachine, RegistryView.Registry32, out steamPath)
                || TryReadRegistryPath(RegistryHive.LocalMachine, RegistryView.Registry64, out steamPath)
                || TryReadRegistryPath(RegistryHive.CurrentUser, RegistryView.Registry32, out steamPath)
                || TryReadRegistryPath(RegistryHive.CurrentUser, RegistryView.Registry64, out steamPath);
        }

        private static bool TryReadRegistryPath(
            RegistryHive hive,
            RegistryView view,
            out string steamPath)
        {
            steamPath = string.Empty;

            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                using RegistryKey? key = baseKey.OpenSubKey(SteamConstants.SteamSubKey);

                if (key?.GetValue(SteamConstants.InstallPathValue) is not string rawPath ||
                    string.IsNullOrWhiteSpace(rawPath))
                {
                    return false;
                }

                if (!TryNormalizePath(rawPath, out string normalizedPath))
                    return false;

                if (!Directory.Exists(normalizedPath))
                    return false;

                steamPath = normalizedPath;
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

        private static bool TryNormalizePath(string rawPath, out string normalizedPath)
        {
            normalizedPath = string.Empty;

            try
            {
                string expandedPath = Environment.ExpandEnvironmentVariables(rawPath.Trim().Trim('"'));
                normalizedPath = Path.GetFullPath(expandedPath);
                return true;
            }
            catch (Exception ex) when (
                ex is ArgumentException ||
                ex is NotSupportedException ||
                ex is PathTooLongException)
            {
                return false;
            }
        }
    }
}