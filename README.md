# Virtual Desktops 2.0

A [Command Palette](https://learn.microsoft.com/windows/powertoys/command-palette/overview) (CmdPal, part of [PowerToys](https://learn.microsoft.com/windows/powertoys/)) extension for managing Windows virtual desktops: switch between desktops and send windows to them without leaving the palette.

## Features

- **Desktop list** — every virtual desktop as a list item with its wallpaper as the icon, a details pane, and a "Current" tag on the active desktop.
- **Dock band** — a compact icon-only desktop switcher rendered inside CmdPal. Icons for active/inactive desktops are configurable.
- **Commands**:
  - *Switch to desktop* — activate a desktop.
  - *Move window here* — send the topmost eligible window (skips tool and popup windows) to the selected desktop.
  - *Move window and switch* — the same, then switch to that desktop.

The list updates live: desktops created or activated outside the extension are picked up automatically.

## Requirements

- Windows 10 version 2004 (build 19041) or newer, x64 or ARM64
- PowerToys with Command Palette enabled

## Installation

### From a release

Grab the latest `.msix` for your architecture (`x64` or `arm64`) from the [Releases](https://github.com/vladon/CmdPalVirtualDesktops/releases) page.

The packages are signed with a self-signed certificate (`CN=vladon.dev`); its public half ships with each release and lives in the repo as [`vd2-signing.cer`](../blob/main/vd2-signing.cer). Trust it once (admin PowerShell), then install:

```powershell
certutil -addstore -f TrustedPeople .\vd2-signing.cer
Add-AppxPackage -Path .\VirtualDesktopBand_2.0.3.0_x64.msix
```

If the original extension by zadjii is still installed, remove it first — it is a different package identity: `Get-AppxPackage *CmdPalVirtualDesktops* | Remove-AppxPackage`.

Once installed, the extension registers itself with CmdPal — look for **Virtual Desktops 2.0** in the palette.

### From source

Requires Windows and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). The easiest route is Visual Studio (with the single-project MSIX packaging workload): open `VirtualDesktopBand.sln` and press F5 with the `VirtualDesktopBand (Package)` profile — this deploys the extension without launching it; the CmdPal host starts it on demand.

Command line:

```sh
dotnet build VirtualDesktopBand.sln -c Release -p:Platform=x64   # or -p:Platform=ARM64
```

Release builds also produce the MSIX package, signed with a self-signed dev certificate (`CN=vladon.dev`; the signing key is not committed — see `AGENTS.md` → **Signing** to recreate it). To install the dev package, first import that certificate into `Local Computer\Trusted People`, then run `Add-AppxPackage` on the `.msix` from the `AppPackages\` output folder. Note that rebuilding force-closes any running instance of the extension.

## Usage

1. Open Command Palette (`Win + Alt + Space` by default).
2. Type **Virtual Desktops 2.0** to open the desktop list, or activate the dock band for a persistent compact switcher.
3. In the list, `Enter` switches to a desktop; the context menu offers *Move window here* and *Move window and switch*.

### Settings

Open the extension's settings in CmdPal to choose which icons the band uses for the active and inactive desktops: dot, pill, filled/empty square, or the desktop's wallpaper. Settings are stored locally in `%LOCALAPPDATA%\dev.vladon.virtualdesktops\settings.json` and are migrated automatically from the pre-2.0 location on first run.

## How It Works

The extension is an out-of-proc COM server packaged as MSIX. The CmdPal host activates a COM class (`f1270cad-9bc8-45c2-83a9-bee1cc52b60d`), which the process registers via `Shmuelie.WinRTServer` and exposes as a `CommandProvider` with a top-level command and a dock band. Virtual desktop enumeration and switching use the [`Slions.VirtualDesktop`](https://www.nuget.org/packages/Slions.VirtualDesktop) library; finding the window to move uses Win32 `EnumWindows` (CsWin32 source-generated P/Invoke).

## Privacy

No data collection — everything runs locally. See [doc/PRIVACY.md](doc/PRIVACY.md).

## Credits

- Based on [zadjii/CmdPalVirtualDesktops](https://github.com/zadjii/CmdPalVirtualDesktops) by Michael Griese — this project started as a fork of that repository and is now maintained independently.
- Built on the [Microsoft Command Palette extension template](https://github.com/microsoft/PowerToys) and the `Slions.VirtualDesktop` library.
