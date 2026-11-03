# RoboCopyUI

This branch builds RoboCopyUI 1.7.3 for Windows 11 x64. It extends Tusharr Kabir’s WPF frontend for Microsoft Robocopy with Explorer shortcuts and a per-user tray host.

## Using the shortcuts

Copy or cut files with Explorer’s Ctrl+C or Ctrl+X, then focus the destination folder’s file list.

| Shortcut | Action |
| --- | --- |
| Ctrl+Alt+V | Paste with ordinary file-copy settings. |
| Ctrl+Alt+Shift+V | Request full preservation for this paste, including permissions, ownership and auditing where supported. |
| Ctrl+Alt+D | Request permanent deletion of the selection. The confirmation defaults to Cancel. |

The tray settings let you rebind or disable each shortcut. Folder transfers include empty directories and keep destination-only files. Full preservation requests /ZB, /COPYALL and /DCOPY:DAT. The tray runs with the current user’s permissions. A failed transfer can offer a one-task administrator retry. This version ships without an Explorer context-menu extension or an elevated startup task.

[English user guide](packaging/README.en-US.txt) · [简体中文使用说明](packaging/README.zh-CN.txt)

## Building and checking a contribution

Use Windows with the .NET 10 desktop SDK. Run the following commands from the repository root. The two checks below exercise shortcut handling, transfer options, settings migration and both language catalogs without installing the application.

```powershell
dotnet build RoboCopyUI.csproj -c Release
dotnet run --project tests/ShortcutChecks -c Release
dotnet run --project tests/LocalizationChecks -c Release -- .
```

To build the per-user installer, use PowerShell from an x64 Visual Studio developer environment with MSVC, Windows SDK 10.0.26100.0, and Inno Setup with the Chinese Simplified translation. Put ISCC.exe on PATH or pass -InnoCompiler. Pass -MsvcRoot if VCToolsInstallDir is unavailable. The selected .NET installation must include both Microsoft.NETCore.App and Microsoft.WindowsDesktop.App at the requested runtime version (default 10.0.14).

```powershell
$dotnetRoot = Split-Path (Get-Command dotnet.exe).Source
.\scripts\Build-Installer.ps1 -DotnetRoot $dotnetRoot
```

The installer and its SHA-256 manifest are written under artifacts/installer/1.7.3. See [LICENSE](LICENSE) for the upstream license.
