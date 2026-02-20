# Fast File Explorer (Windows)

Listary-style desktop search app built with .NET 9 + WPF.

## Implemented

- Name-only search (no path matching in query).
- Searches both files and folders.
- Left filter panel:
  - `All`, `Folder`, `File`, `Document`, `Picture`, `Video`
  - date filters: `All`, `Last 1 day`, `Last 7 days`, `Last 30 days`, `Last 365 days`
- Per-item system icons (folder, `.txt`, `.docx`, etc.).
- Non-blocking search pipeline with debounce + cancellation.
- Persistent SQLite index cache (`index-cache-v2.db`) with incremental updates:
  - first run builds index
  - subsequent runs load cached index immediately
  - continuous checkpoint persistence during indexing
- Right-click context menu:
  - `Open`
  - `Open Containing Folder`
  - `Copy Path to Clipboard`
  - `Edit with VS Code`
  - `Properties` (opens in Explorer)
- Indexing mode toggle:
  - default: skips hidden/system/low-level directories for much faster indexing
  - optional checkbox: include hidden/system files and folders
- Tray-background behavior:
  - closing window hides to system tray
  - indexing continues in background
  - launching app again restores the existing window (single-instance activation)

## Run

From `FastFileExplorer/`:

```powershell
dotnet run
```

## Installer

1. Install Inno Setup (for `ISCC.exe`) on the build machine.
2. Installer icon priority:
   - `FastFileExplorer/icon/idemia_new.ico`
   - fallback: `src/app/favicon.ico`
3. Build installer:

```powershell
cd FastFileExplorer\installer
powershell -ExecutionPolicy Bypass -File .\build-installer.ps1 -SelfContained
```

Installer output:
- `FastFileExplorer/installer/out/installer/FastFileExplorer-Setup.exe`

## Notes

- Cache path: `%LOCALAPPDATA%\FastFileExplorer\index-cache-v2.db`
- Legacy cache import: `%LOCALAPPDATA%\FastFileExplorer\index-cache-v1.json` (and migrated `%PROGRAMDATA%` legacy cache)
- Use the `Reindex` button after changing large storage roots while the app is closed.
- App icon priority:
  - `FastFileExplorer/icon/idemia_new.ico`
  - fallback: `src/app/favicon.ico`

## Tests

Run all tests + coverage:

```powershell
cd FastFileExplorer.Tests
powershell -ExecutionPolicy Bypass -File .\run-tests.ps1
```

Results:
- `FastFileExplorer.Tests/TestResults/**/coverage.cobertura.xml`
