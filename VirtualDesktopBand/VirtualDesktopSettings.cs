// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CommandPalette.Extensions.Toolkit;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Vladon.CmdPal.VirtualDesktops;

public class VirtualDesktopSettings : JsonSettingsManager
{
    internal const string WallpaperValue = "wallpaper";
    internal const string CircleFillBadge12Value = "circleFillBadge12";
    internal const string ToggleFilledValue = "toggleFilled";
    internal const string CheckboxFillValue = "checkboxFill";
    internal const string CheckboxEmptyValue = "checkboxEmpty";

    private static readonly string _namespace = "virtualDesktops";

    private static string Namespaced(string propertyName) => $"{_namespace}.{propertyName}";

    private static readonly List<ChoiceSetSetting.Choice> _iconChoices =
    [
        new("Dot", CircleFillBadge12Value),
        new("Pill", ToggleFilledValue),
        new("Filled square", CheckboxFillValue),
        new("Empty square", CheckboxEmptyValue),
        new("Desktop wallpaper", WallpaperValue),
    ];

#pragma warning disable SA1401 // Fields should be private
    internal static VirtualDesktopSettings Instance = new();
#pragma warning restore SA1401

    public string ActiveDesktopIcon => _activeDesktopIcon.Value ?? ToggleFilledValue;

    public string InactiveDesktopIcon => _inactiveDesktopIcon.Value ?? CircleFillBadge12Value;

    private readonly ChoiceSetSetting _activeDesktopIcon = new(
        Namespaced(nameof(ActiveDesktopIcon)),
        "Active desktop icon",
        "The icon to display for the currently active desktop in the band",
        _iconChoices);

    private readonly ChoiceSetSetting _inactiveDesktopIcon = new(
        Namespaced(nameof(InactiveDesktopIcon)),
        "Inactive desktop icon",
        "The icon to display for inactive desktops in the band",
        _iconChoices);

    private const string SettingsFolderName = "dev.vladon.virtualdesktops";
    private const string LegacySettingsFolderName = "Zadjii.CmdPal.VirtualDesktops";

    internal static string SettingsJsonPath()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            SettingsFolderName);
        Directory.CreateDirectory(directory);
        MigrateLegacySettings(directory);
        return Path.Combine(directory, "settings.json");
    }

    // First run after the 2.0 rebrand: the package identity changed, so settings would start
    // from scratch. If the pre-2.0 extension ever wrote settings, seed the new location from
    // it. The legacy file is left in place so an old install keeps working until uninstalled.
    //
    // Both the original and our pre-unvirtualized builds are packaged apps, so MSIX
    // redirected their AppData writes into each package's LocalCache — the real
    // %LOCALAPPDATA% folders never existed for them. Probe the package stores too.
    private static void MigrateLegacySettings(string newDirectory)
    {
        try
        {
            var newPath = Path.Combine(newDirectory, "settings.json");
            if (File.Exists(newPath))
            {
                return;
            }

            foreach (var legacyPath in LegacySettingsCandidates())
            {
                if (!File.Exists(legacyPath))
                {
                    continue;
                }

                File.Copy(legacyPath, newPath);
                Debug.WriteLine($"Migrated settings from {legacyPath} to {newPath}");
                return;
            }
        }
        catch (Exception e)
        {
            Debug.WriteLine($"Settings migration failed\n{e.Message}");
        }
    }

    private static IEnumerable<string> LegacySettingsCandidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // The documented pre-2.0 location (exists only if something ran unpackaged).
        yield return Path.Combine(localAppData, LegacySettingsFolderName, "settings.json");

        var packagesRoot = Path.Combine(localAppData, "Packages");
        string[] packageFolders = [];
        try
        {
            packageFolders = Directory.GetDirectories(packagesRoot);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"Packages enumeration failed\n{e.Message}");
        }

        foreach (var package in packageFolders)
        {
            yield return Path.Combine(package, "LocalCache", "Local", LegacySettingsFolderName, "settings.json");
        }

        foreach (var package in packageFolders)
        {
            yield return Path.Combine(package, "LocalCache", "Local", SettingsFolderName, "settings.json");
        }
    }

    public VirtualDesktopSettings()
    {
        FilePath = SettingsJsonPath();

        Settings.Add(_activeDesktopIcon);
        Settings.Add(_inactiveDesktopIcon);
        // default to pill for the active desktop
        _activeDesktopIcon.Value = _iconChoices[1].Value;

        LoadSettings();

        Settings.SettingsChanged += (s, a) => SaveSettings();
    }

    public static IconInfo GetIconForValue(string value, string? wallpaperPath = null)
    {
        return value switch
        {
            CircleFillBadge12Value => Icons.CircleFillBadge12Icon,
            ToggleFilledValue => Icons.ToggleFilledIcon,
            CheckboxFillValue => Icons.CheckboxFillIcon,
            CheckboxEmptyValue => Icons.CheckboxEmptyIcon,
            WallpaperValue when wallpaperPath is not null => new IconInfo(wallpaperPath),
            _ => Icons.CircleFillBadge12Icon,
        };
    }
}
