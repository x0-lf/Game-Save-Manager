# Game-Save-Manager
Which Steam games are installed? Which Steam users exist? Which known save-location rules match those games? Which of those paths actually exist? Which files changed since last backup? Where should these files be restored for another user/OS/device? How to sync between Steam profiles and make family‑sharing backups painless.

### What each class does

| Class | Purpose |
| --- | --- |
| ``Program`` | Entry point of the console app. Creates the **[SteamDiscoveryService](ca://s?q=Explain_SteamDiscoveryService)**, runs discovery, prints results. |
| ``SteamDiscoveryService`` | Main orchestrator. Controls discovery order: registry lookup, Steam root validation, library metadata parsing, manifest reading, and fallback disk scanning. |
| ``RegistrySteamLocator`` | Reads Steam’s installation path from the Windows registry. Does *not* validate libraries or games — only finds the Steam root. |
| ``SteamLibraryFoldersReader`` | Reads ``libraryfolders.vdf`` and extracts Steam library paths. One of the only classes that should know about **Gameloop.Vdf**. |
| ``SteamAppManifestReader`` | Reads ``appmanifest_*.acf`` files and converts them into ``SteamGame`` objects. The second class that should know about **Gameloop.Vdf**. |
| ``SteamRootValidator`` | Validates whether a folder looks like a real Steam installation (``steam.exe``, ``steam.dll``, ``steamapps``, etc.). |
| ``SteamLibraryValidator`` | Validates whether a folder is a proper Steam library (``steamapps``, ``common``, manifest files). |
| ``SteamFallbackScanner`` | Step 4 fallback. Scans fixed drives for Steam libraries when registry/VDF discovery fails or when the user requests a deep scan. |
| ``SteamDiscoveryResult`` | Holds the final discovery output: Steam root, libraries, games, warnings, and confidence levels. |
| ``SteamRootValidationResult`` | Represents validation results for the Steam root folder — answers “does this path really look like Steam?”. |
| ``SteamLibraryInfo`` | Represents one Steam library folder: path, presence of ``steamapps``, presence of ``common``, and manifest count. |
| ``SteamGame`` | Represents one installed Steam game: app ID, name, install folder, manifest path, game path, and confidence level. |
| ``SteamDiscoveryConfidence`` | Indicates how trustworthy the discovery result is: ``High`` (metadata), ``Low`` (fallback scan), ``Orphaned`` (missing manifests). |
| ``SteamConstants`` | Stores registry keys and value names to avoid magic strings like ``"InstallPath"`` scattered across the codebase. |

---
