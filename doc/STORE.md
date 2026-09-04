# Microsoft Store publishing checklist

Step-by-step for publishing Virtual Desktops 2.0 to the Microsoft Store, based on the
[official Command Palette Store guide](https://learn.microsoft.com/en-us/windows/powertoys/command-palette/publish-extension-store)
and [Partner Center account docs](https://learn.microsoft.com/en-us/windows/apps/publish/partner-center/open-a-developer-account).

## 1. Developer account (free)

- Individual account: free, requires a personal Microsoft account + **identity verification** (government ID + selfie, captured on mobile).
- Entry point: <https://storedeveloper.microsoft.com> (the only supported entry for the free flow).
- Region note: availability and identity verification depend on the account country; if onboarding is blocked for a region, this is the step to resolve first.

## 2. Partner Center product setup

1. Partner Center → **Apps and games** → **+ New Product** → **MSIX or PWA app**.
2. Reserve the product name (e.g. `Virtual Desktops 2.0`).
3. From **Product Management → Product identity**, copy three values:
   - `Package/Identity/Name`
   - `Package/Identity/Publisher`
   - `Package/Properties/PublisherDisplayName`

## 3. Store build (no csproj changes needed)

The single-project MSIX targets accept Store overrides from the command line, so the
repository stays untouched and the normal dev flow keeps using the local identity
(`dev.vladon.virtualdesktops` / `CN=vladon.dev`):

```powershell
dotnet publish VirtualDesktopBand -c Release -p:Platform=x64 `
  -p:AppxPackageIdentityName=<IdentityName> `
  -p:AppxPackagePublisher=<IdentityPublisher> `
  -p:AppxPackageVersion=2.0.4.0 `
  -p:AppxPackageDir="AppPackages\Store\x64\"

dotnet publish VirtualDesktopBand -c Release -p:Platform=ARM64 `
  -p:AppxPackageIdentityName=<IdentityName> `
  -p:AppxPackagePublisher=<IdentityPublisher> `
  -p:AppxPackageVersion=2.0.4.0 `
  -p:AppxPackageDir="AppPackages\Store\ARM64\"
```

The Store build is **unsigned or self-signed — the Store re-signs packages during
certification**, so the self-signed cert is not a blocker here (unlike winget).

### Assets

Store validation wants base-name logos. If missing, generate scale-less copies next to
the existing ones (`Square150x150Logo.png`, `SmallTile.png`, `LargeTile.png`,
`Wide310x150Logo.png`, `SplashScreen.png`, `StoreLogo.png`) — the official guide does this
with a `PrepareAssets` MSBuild target copying from the `scale-200` files.

### Bundle

The Store accepts a single `.msixbundle`:

1. Create `bundle_mapping.txt`:

   ```text
   [Files]
   "AppPackages\Store\x64\VirtualDesktopsExtension_2.0.4.0_x64.msix" "VirtualDesktopsExtension_2.0.4.0_x64.msix"
   "AppPackages\Store\ARM64\VirtualDesktopsExtension_2.0.4.0_arm64.msix" "VirtualDesktopsExtension_2.0.4.0_arm64.msix"
   ```

2. Bundle (makeappx ships with the Windows SDK / VS):

   ```powershell
   & "<WindowsSDK>\bin\<ver>\<arch>\makeappx.exe" bundle /f bundle_mapping.txt /p VirtualDesktops_2.0.4.0_Bundle.msixbundle
   ```

## 4. Submission content

- **Description** must include the phrase pattern `Virtual Desktops 2.0 integrates with the Windows Command Palette to …`
- **Additional Testing Information** (Supplemental info): explain that PowerToys with
  Command Palette must be installed and enabled for the extension to function
  (example: [chatasweetie/CmdPalExtensions TesterInstructions](https://github.com/chatasweetie/CmdPalExtensions/blob/main/microsoftStoreResources/TesterInstructions.txt)).
- Privacy policy URL: `https://github.com/vladon/CmdPalVirtualDesktops/blob/main/doc/PRIVACY.md`
- Screenshots of the palette list, dock band, and context menus; age rating questionnaire.
- Note in testing info that the extension registers via `com.microsoft.commandpalette`
  app extension and runs as a full-trust packaged COM server (`runFullTrust` is declared
  in the manifest — expect a justification question during certification).

## 5. Identity change — consequences

The Store re-signs packages with its own publisher, so the Store-installed identity will
differ from the GitHub-release identity (`dev.vladon.virtualdesktops`):

- Users installing both variants get **two extensions in CmdPal** — document uninstalling
  the GitHub-release one (`Get-AppxPackage dev.vladon.virtualdesktops | Remove-AppxPackage`).
- Settings are safe: `settings.json` lives in a plain `%LOCALAPPDATA%` folder, not tied to
  package identity.
- Command IDs (`dev.vladon.virtualDesktops.*`) are manifest data and survive re-signing.
