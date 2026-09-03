# Repository Guidelines

## Project Overview

Virtual desktops extension for the Windows Command Palette (CmdPal, from PowerToys). Ships as a single-project, MSIX-packaged out-of-proc COM server that registers with the CmdPal host via the `com.microsoft.commandpalette` app extension. Provides:

- A top-level **list page** of virtual desktops (wallpaper thumbnail per item, details pane, "Current" tag).
- A **dock band** (`GetDockBands`) — compact icon-only desktop switcher; icons for active/inactive desktops are user-configurable.
- Commands: switch to desktop, move the last non-tool window to a desktop, move + switch.

Named **Virtual Desktops 2.0**. Based on `zadjii/CmdPalVirtualDesktops` — started as a fork of it, now a standalone repo (`origin` = `vladon/CmdPalVirtualDesktops`, `upstream` removed). No data collection — everything is local (see `doc/PRIVACY.md`).

## Architecture & Data Flow

Classic CmdPal extension wiring, all in one project:

```
CmdPal host activates COM class f1270cad-9bc8-45c2-83a9-bee1cc52b60d
  → Program.Main (arg "-RegisterProcessAsComServer")
    → Shmuelie.WinRTServer ComServer registers singleton VirtualDesktopBand : IExtension
      → GetProvider(ProviderType.Commands) → VirtualDesktopCommandsProvider : CommandProvider
        → TopLevelCommands()  → VirtualDesktopsListPage(asBand: false)
        → GetDockBands()      → VirtualDesktopsListPage(asBand: true)
```

- `VirtualDesktopBand.cs` — `IExtension` + `IDisposable`. The `[Guid]` **must** match the `com:Class Id` in `Package.appxmanifest`. `Dispose()` sets the `ManualResetEvent` that unblocks `Main` (process exits).
- `VirtualDesktopsListPage` (in `VirtualDesktopCommandsProvider.cs`) is the core: subscribes to `VirtualDesktop.CurrentChanged` / `VirtualDesktop.Created` and `SettingsChanged`, then refreshes via `UpdateDesktopsOffUiThread` → `Task.Factory.StartNew(..., _scheduler)` (captured `TaskScheduler`) → `RaiseItemsChanged()`. `GetItems()` maps each `WindowsDesktop.VirtualDesktop` to a `ListItem`.
- Virtual desktop operations (enumerate, `Switch()`, `MoveToDesktop`) come from the **`Slions.VirtualDesktop`** NuGet package (namespace `WindowsDesktop`), not from in-repo code. Win32 window enumeration (`FindLastNonToolWindow`) uses CsWin32-generated `PInvoke.*`.
- Settings persist to `%LOCALAPPDATA%\dev.vladon.virtualdesktops\settings.json`. On first run after the 2.0 rebrand, `MigrateLegacySettings` copies them from the legacy `%LOCALAPPDATA%\Zadjii.CmdPal.VirtualDesktops\settings.json` (pre-2.0 identity) if present; the legacy file is left untouched.

## Key Directories

| Path | Purpose |
|---|---|
| `VirtualDesktopBand/` | The only project — all C# sources, manifests, assets |
| `VirtualDesktopBand/Assets/` | MSIX logos + `TaskViewCmdPal*.svg` icon sources |
| `VirtualDesktopBand/Properties/` | `launchSettings.json`, publish profiles (`win-x64.pubxml`, `win-arm64.pubxml`) |
| `doc/` | `PRIVACY.md` only |

## Development Commands

Requires Windows + .NET 10 SDK. From repo root:
```sh
dotnet restore VirtualDesktopBand.sln
dotnet build VirtualDesktopBand.sln -c Debug -p:Platform=x64    # or ARM64
dotnet publish VirtualDesktopBand -c Release -p:Platform=x64    # picks up win-x64.pubxml (self-contained, ReadyToRun)
```

- Release builds **also generate the MSIX** (`GenerateAppxPackageOnBuild=true`).
- **Publishing a rebuild to the machine: bump the patch version first** (third part of `Identity Version` in `Package.appxmanifest`, e.g. `2.0.0.0` → `2.0.1.0`) and commit the bump. Windows blocks reinstalling the same version (`0x80073CFB`), so every published rebuild needs a fresh patch number — no `Remove-AppxPackage` dance required.
- A custom MSBuild target (`KillRunningExecutable`) runs `taskkill /F /IM VirtualDesktopsExtension.exe` before every Build/Deploy/Publish — a running instance is force-killed (note the exe name is `VirtualDesktopsExtension`, not the project name).
- Debug/deploy: F5 in Visual Studio with the `VirtualDesktopBand (Package)` profile deploys the MSIX without launching (`doNotLaunchApp: true`) — the CmdPal host starts the exe. The `(Unpackaged)` profile runs the exe directly, which just prints "Not being launched as a Extension... exiting." (COM activation arg is absent).
- x86 solution configurations exist in the `.sln` but are vestigial: `RuntimeIdentifiers` and publish profiles cover only `win-x64` / `win-arm64`.
### Signing

- Dev/test packages are signed with a self-signed cert `CN=vladon.dev`: `VirtualDesktopBand/VirtualDesktopsExtension_TemporaryKey.pfx`, wired via `PackageCertificateKeyFile`/`PackageCertificatePassword` in the `.csproj`. The `.pfx` itself is gitignored (`*.pfx`).
- To regenerate on a new machine (password `vd2dev` must match `PackageCertificatePassword`):

  ```powershell
  $c = New-SelfSignedCertificate -Type Custom -Subject "CN=vladon.dev" -KeyUsage DigitalSignature -FriendlyName "Virtual Desktops 2.0 dev" -CertStoreLocation "Cert:\CurrentUser\My" -NotAfter (Get-Date).AddYears(5) -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3","2.5.29.19={text}")
  Export-PfxCertificate -Cert "Cert:\CurrentUser\My\$($c.Thumbprint)" -FilePath .\VirtualDesktopBand\VirtualDesktopsExtension_TemporaryKey.pfx -Password (ConvertTo-SecureString -String vd2dev -Force -AsPlainText)
  ```

- Signed MSIX lands in `AppPackages\VirtualDesktopBand_<version>_<arch>_Test\` (the `_Test` suffix + warning `APPX0107` appear because the self-signed cert isn't machine-trusted). To install a dev MSIX, first trust the cert: import it into `Local Computer\Trusted People` (admin), then `Add-AppxPackage`.

## Code Conventions & Common Patterns

- **File-scoped namespaces**, `Nullable` enable, `ImplicitUsings` **not** set (explicit usings), `AllowUnsafeBlocks` true. LangVersion = net9 default (C# 13).
- Files carry the CmdPal template header (`// Copyright (c) Microsoft Corporation ... MIT`); keep it on new files. No LICENSE file exists in the repo.
- **Commands are primary-constructor nested classes**: `private sealed partial class SwitchToDesktopCommand(VirtualDesktop desktop, bool isCurrent, bool asBand, int index) : InvokableCommand` overriding `Name`/`Id`/`Icon`/`Invoke()`.
- **Command IDs are reverse-DNS + position index**: `dev.vladon.virtualDesktops`, `dev.vladon.virtualDesktops.switchTo.{index}`, `dev.vladon.virtualDesktops.moveWindow.{index}` (matches the package identity). Keep that scheme for new commands.
- **Error handling**: commands wrap `Invoke()` bodies in try/catch and log via `DebugPrint` (`Debug.WriteLine` — visible in DebugView); always return `CommandResult.KeepOpen()`. No exceptions escape to the host.
- **Settings**: singleton `VirtualDesktopSettings.Instance` (internal static field with `#pragma` SA1401 suppression); setting keys namespaced via `Namespaced(nameof(...))` → `virtualDesktops.ActiveDesktopIcon`; auto-save on `SettingsChanged`. Add new settings as private `Setting` fields, expose read-only properties with fallback defaults, register in the ctor, and map display values in `GetIconForValue`-style switches.
- **Icons**: static `IconInfo` fields in the `Icons` class using Segoe Fluent glyphs (`"\uE7C4"`); file-based icons via `IconHelpers.FromRelativePath("Assets\\...")`.
- **Win32 interop**: never hand-write `DllImport`. Add the API name to `NativeMethods.txt` (CsWin32 source generator) and call `PInvoke.*`; marshal strings with `unsafe fixed char*`.
- **Threading**: never touch the desktop list on a callback thread — hop to the page's captured scheduler (`UpdateDesktopsOffUiThread` pattern) before mutating state + `RaiseItemsChanged()`.
- **Package versions** are centrally managed: add to `Directory.Packages.props`, reference without version in the `.csproj`. Several pinned versions there (WebView2, WindowsAppSDK, System.Text.Json, StyleCop.Analyzers) are currently unreferenced leftovers from the template.
- Analyzers: `EnableNETAnalyzers` + `AnalysisMode=Recommended` (via `Directory.Build.props`); CsWinRT AOT optimizer enabled at warning level 2 — keep interop trim/AOT-warning-clean.

## Important Files

- `VirtualDesktopBand/Program.cs` — entry point / COM server bootstrap
- `VirtualDesktopBand/VirtualDesktopBand.cs` — `IExtension` (GUID ties code ⇔ manifest)
- `VirtualDesktopBand/VirtualDesktopCommandsProvider.cs` — provider, `Icons`, `VirtualDesktopsListPage`, both `InvokableCommand`s (the bulk of the logic)
- `VirtualDesktopBand/VirtualDesktopSettings.cs` — settings model + persistence path
- `VirtualDesktopBand/VirtualDesktopBand.csproj` — TFM, RIDs, MSIX tooling, package refs, `KillRunningExecutable` target
- `VirtualDesktopBand/Package.appxmanifest` — MSIX identity (`CmdPalVirtualDesktops`), COM `ExeServer` + CmdPal `CmdPalProvider` registration, capabilities (`runFullTrust`, `internetClient`)
- `VirtualDesktopBand/NativeMethods.txt` — CsWin32 P/Invoke allow-list
- `Directory.Build.props` / `Directory.Packages.props` / `nuget.config` — shared build config, CPM versions, nuget.org-only source mapping

## Runtime/Tooling Preferences

- .NET 10, Windows-specific TFM `net10.0-windows10.0.26100.0` with Windows SDK projection `10.0.26100.68-preview`; min OS 10.0.19041.0. Windows-only — do not try to make it cross-platform.
- Build with `dotnet` CLI or Visual Studio (MSIX tooling / single-project packaging). NuGet source is pinned to nuget.org only (`nuget.config` clears all others).
- Platforms: x64 and ARM64 (`PlatformTarget=$(Platform)` in `Directory.Build.props`).
- Renaming the extension touches several coupled places: `AssemblyName` (`VirtualDesktopsExtension.exe`, referenced by the `com:ExeServer` and the taskkill target), the COM GUID (code + manifest), and the settings folder name.

## Testing & QA

- **No test infrastructure exists**: no test projects, test SDK packages, CI workflows, scripts, or coverage config (verified repo-wide). Don't scaffold tests unprompted.
- Verification is manual: build, deploy the MSIX, then exercise the extension inside the real CmdPal host (switch/move commands, band icons, settings changes refreshing the list). The `(Package)` launch profile handles deploy.
- After reinstalling/updating the extension package, **restart the CmdPal host** (`Stop-Process -Name Microsoft.CmdPal.UI -Force`, then relaunch) — the dev build caches extension state and won't show updated top-level commands until restarted.
- `DebugPrint` tracing (`Debug.WriteLine`) is the debugging tool — attach DebugView or a debugger to the running `VirtualDesktopsExtension.exe` process.

## Git Workflow

- **After every completed step of work: commit and push.** Stage the touched files, commit, and push to `origin`/`main` before moving on — do not batch multiple steps into one commit.

  ```sh
  git add <files>
  git commit -m "Short one-line summary"
  git push origin main
  ```

- Commit messages follow the repo's existing style: short, casual one-line subjects (`neat`, `polished enough for release`), no conventional-commit prefixes, no bodies.
- `origin` is `vladon/CmdPalVirtualDesktops`; branch is `main`.
