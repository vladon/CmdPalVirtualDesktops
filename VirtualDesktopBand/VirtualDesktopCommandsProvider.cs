// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using WindowsDesktop;

namespace Vladon.CmdPal.VirtualDesktops;

public partial class VirtualDesktopCommandsProvider : CommandProvider
{
    private readonly ICommandItem[] _commands = [];
    private readonly ICommandItem[] _bands;

    public VirtualDesktopCommandsProvider()
    {
        DisplayName = "Virtual desktops";
        Icon = Icons.AppIcon;

        Settings = VirtualDesktopSettings.Instance.Settings;
        _commands = [
            new CommandItem(new VirtualDesktopsListPage(asBand: false)) { Title = DisplayName },
        ];
        _bands = [
            new CommandItem(new VirtualDesktopsListPage(asBand: true)) { Title = DisplayName },
        ];
    }

    public override ICommandItem[] TopLevelCommands()
    {
        return _commands;
    }
    public override ICommandItem[]? GetDockBands()
    {
        return _bands;
    }

    public override ICommandItem? GetCommandItem(string id)
    {
        // First check top-level commands.
        foreach (var li in _commands)
        {
            if (li?.Command is ICommand cmd && cmd.Id == id)
            {
                return li;
            }
        }
        // don't need to sheck bands, those are the same thing,
        return null;
    }

}

public static class Icons
{
    public static readonly IconInfo TaskViewIcon = new("\uE7C4");

    public static readonly IconInfo CheckboxEmptyIcon = new("\uE739");
    public static readonly IconInfo CheckboxFillIcon = new("\uE73B");
    public static readonly IconInfo ToggleFilledIcon = new("\uEC11");
    public static readonly IconInfo StatusCircleIcon = new("\uEA81");
    public static readonly IconInfo CircleFillBadge12Icon = new("\uEDB0");

    public static readonly IconInfo Switchcon = new("\uE8AB"); // Switch
    public static readonly IconInfo SendIcon = new("\uE724"); // Send
    public static readonly IconInfo NewWindowIcon = new("\uE78B"); // NewWindow
    
    public static readonly IconInfo AppIcon = IconHelpers.FromRelativePath("Assets\\Square44x44Logo.scale-200.png");
}

public partial class VirtualDesktopsListPage : ListPage
{
    TaskScheduler _scheduler;

    // Raised by the nested commands after they change desktop state so live pages refresh
    // immediately instead of waiting for the (sometimes delayed) COM event.
    internal static event Action? DesktopsChanged;

    public override string Name => "Open";
    public override string Id => "dev.vladon.virtualDesktops";
    public override IconInfo Icon => Icons.TaskViewIcon;

    public static readonly Tag CurrentDesktopTag = new("Current");

    private VirtualDesktop[] _desktops;
    private readonly bool _asBand;

    // Items are cached per desktop and mutated in place on refresh: the dock renders the
    // ListItem objects it holds at registration and ignores RaiseItemsChanged, so in-place
    // updates are the only way its visible state (active desktop highlight) can change.
    private readonly Dictionary<Guid, ListItem> _itemsByDesktopId = new();

    // Session transitions (RDP/console) can break the host's dock band binding; all
    // host-bound refreshes are suppressed until the session settles (see SessionSwitch).
    private static DateTime _sessionSettleUntil = DateTime.MinValue;

    // Flip to true to bounce the palette host on session transitions — the brute-force
    // recovery for a stale band (blinks the dock for a few seconds). Off while the
    // deferred-refresh experiment runs.
    private static readonly bool EnableHostRestartOnSessionTransition = false;

    public VirtualDesktopsListPage(bool asBand)
    {
        _asBand = asBand;
        _scheduler = TaskScheduler.Current;

        VirtualDesktop.CurrentChanged += (_, args) => UpdateDesktopsOffUiThread();
        VirtualDesktop.Created += (_, desktop) => UpdateDesktopsOffUiThread();
        VirtualDesktop.Destroyed += (_, _) => UpdateDesktopsOffUiThread();
        VirtualDesktop.Renamed += (_, _) => UpdateDesktopsOffUiThread();
        // RDP/console transitions recreate the session desktop AND the host's dock band
        // binding can break (microsoft/PowerToys#50367). Suspected trigger: COM item
        // updates fired into the host mid-transition. So: suppress all host-bound
        // refreshes while the session settles, then apply one deferred refresh — if the
        // band survives this way, no palette restart is ever needed.
        SystemEvents.SessionSwitch += (_, e) =>
        {
            LifetimeLog.Write($"SessionSwitch: {e.Reason}");
            if (e.Reason is SessionSwitchReason.RemoteConnect
                or SessionSwitchReason.RemoteDisconnect
                or SessionSwitchReason.ConsoleConnect
                or SessionSwitchReason.ConsoleDisconnect)
            {
                _sessionSettleUntil = DateTime.Now.AddSeconds(8);
                Task.Run(async () =>
                {
                    await Task.Delay(8500);
                    LifetimeLog.Write("Deferred refresh after session transition");
                    UpdateDesktopsOffUiThread();
                });
                if (EnableHostRestartOnSessionTransition)
                {
                    var reason = e.Reason.ToString();
                    Task.Run(async () =>
                    {
                        await Task.Delay(3000);
                        Program.RestartHostForSessionTransition(reason);
                    });
                }
            }
            else
            {
                UpdateDesktopsOffUiThread();
            }
        };
        DesktopsChanged += UpdateDesktopsOffUiThread;
        VirtualDesktopSettings.Instance.Settings.SettingsChanged += (_, _) => UpdateDesktopsOffUiThread();

        _desktops = VirtualDesktop.GetDesktops();

        ShowDetails = !_asBand;
    }

    public override IListItem[] GetItems()
    {
        List<IListItem> items = new(_desktops.Length);
        var seenDesktopIds = new HashSet<Guid>();

        for (int i = 0; i < _desktops.Length; i++)
        {
            VirtualDesktop desktop = _desktops[i];
            seenDesktopIds.Add(desktop.Id);
            items.Add(GetOrRefreshItem(desktop, _asBand, i));
        }

        foreach (var staleId in _itemsByDesktopId.Keys.Where(id => !seenDesktopIds.Contains(id)).ToList())
        {
            _itemsByDesktopId.Remove(staleId);
        }

        return items.ToArray();
    }

    private void UpdateDesktopsOffUiThread()
    {
        if (DateTime.Now < _sessionSettleUntil)
        {
            LifetimeLog.Write("Refresh suppressed during session transition (deferred)");
            return;
        }

        Task.Factory.StartNew(UpdateDesktopsOnUiThread,
            CancellationToken.None,
            TaskCreationOptions.None,
            _scheduler);
    }

    private void UpdateDesktopsOnUiThread()
    {
        try
        {
            _desktops = VirtualDesktop.GetDesktops();
            var current = VirtualDesktop.Current;
            LifetimeLog.Write($"Refresh: {_desktops.Length} desktops, current={current.Id}");
        }
        catch (Exception e)
        {
            LifetimeLog.Write($"Refresh failed: {e.Message}");
        }

        RaiseItemsChanged();
    }

    private static ListItem DesktopToItem(VirtualDesktop desktop, bool asBand, int index)
    {
        bool isCurrent = desktop == VirtualDesktop.Current;

        List<CommandContextItem> contextItems = [
            new CommandContextItem(new MoveWindowToDesktopCommand(desktop, index, false))
            {
                Title = "Move window here",
            },
            new CommandContextItem(new MoveWindowToDesktopCommand(desktop, index, true))
            {
                Title = "Move window and switch",
            },
        ];

        if (asBand)
        {
            // in the band we only show the context menu, not the command in the list item itself
            contextItems.Insert(0, new CommandContextItem(new SwitchToDesktopCommand(desktop, isCurrent, asBand: false, index))
            {
                Title = "Switch to desktop",
                Icon = Icons.Switchcon,
            });
        }

        ListItem li = new ListItem(new SwitchToDesktopCommand(desktop, isCurrent, asBand, index))
        {
            MoreCommands = contextItems.ToArray(),
        };

        ApplyDesktopState(li, desktop, asBand, index);
        return li;
    }

    // Sets the display state (icon, title, tags) of a band/list item. Shared between item
    // creation and in-place refreshes: the dock renders the ListItem objects it holds at
    // registration and ignores RaiseItemsChanged, so refreshing in place is the only way
    // its visible state (active desktop highlight) can change.
    private static void ApplyDesktopState(ListItem li, VirtualDesktop desktop, bool asBand, int index)
    {
        bool isCurrent = desktop == VirtualDesktop.Current;
        IconInfo wallpaperIconInfo = new IconInfo(desktop.WallpaperPath);

        IconInfo icon = asBand ?
            (isCurrent
                ? VirtualDesktopSettings.GetIconForValue(VirtualDesktopSettings.Instance.ActiveDesktopIcon, desktop.WallpaperPath)
                : VirtualDesktopSettings.GetIconForValue(VirtualDesktopSettings.Instance.InactiveDesktopIcon, desktop.WallpaperPath)) :
            wallpaperIconInfo;

        li.Icon = icon;

        if (!asBand)
        {
            string desktopName = GetDesktopName(desktop, index);
            bool hasName = !string.IsNullOrEmpty(desktopName);
            string desktopNumberLabel = $"Desktop {index + 1}";

            li.Title = hasName ? desktopName : desktopNumberLabel;
            li.Subtitle = hasName ? desktopNumberLabel : string.Empty;
            li.Details = new Details()
            {
                Title = li.Title,
                HeroImage = icon,
            };
        }

        li.Tags = isCurrent ? [CurrentDesktopTag] : [];
    }

    private ListItem GetOrRefreshItem(VirtualDesktop desktop, bool asBand, int index)
    {
        if (!_itemsByDesktopId.TryGetValue(desktop.Id, out var li))
        {
            li = DesktopToItem(desktop, asBand, index);
            _itemsByDesktopId[desktop.Id] = li;
        }
        else
        {
            ApplyDesktopState(li, desktop, asBand, index);
        }

        return li;
    }

    // On Windows 10 the COM layer often returns an empty name for custom-named desktops
    // (zadjii/CmdPalVirtualDesktops#4); Win10 reliably persists them in the registry keyed
    // by desktop GUID, so fall back to it whenever the API gives us nothing.
    private static string GetDesktopName(VirtualDesktop desktop, int index)
    {
        string name = desktop.Name;
        if (string.IsNullOrEmpty(name))
        {
            name = GetDesktopNameFromRegistry(desktop.Id);
        }

        return name ?? string.Empty;
    }

    private static string? GetDesktopNameFromRegistry(Guid desktopId)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops\Desktops\{{{desktopId}}}");
            return key?.GetValue("Name") as string;
        }
        catch (Exception e)
        {
            DebugPrint($"Desktop name registry lookup failed\n{e.Message}");
            return null;
        }
    }

    private static HWND FindLastNonToolWindow()
    {
        HWND found = HWND.Null;
        uint currentPid = (uint)Environment.ProcessId;

        PInvoke.EnumWindows((hWnd, _) =>
        {
            if (!PInvoke.IsWindowVisible(hWnd))
            {
                return true; // continue
            }

            const int WS_EX_TOOLWINDOW = 0x00000080;
            int exStyle = PInvoke.GetWindowLong(hWnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0)
            {
                return true; // continue
            }

            // also skip popups
            const uint WS_POPUP = 0x80000000;
            int style = PInvoke.GetWindowLong(hWnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
            if ((style & WS_POPUP) != 0)
            {
                return true; // continue
            }

            found = hWnd;
            return false; // stop
        }, IntPtr.Zero);

        return found;
    }

    // The cached desktop object can go stale across session transitions; resolve a fresh
    // instance by Id (falling back to the cached one) so switches and moves always work.
    private static VirtualDesktop ResolveFresh(VirtualDesktop desktop)
    {
        try
        {
            return VirtualDesktop.FromId(desktop.Id) ?? desktop;
        }
        catch
        {
            return desktop;
        }
    }

    // After a programmatic desktop switch Windows keeps the foreground where it was (the
    // dock button the user just clicked), so the last-used window on the target desktop
    // never gets focus (zadjii/CmdPalVirtualDesktops#1). Foreground the topmost window
    // living on the target desktop, skipping the palette host's own windows.
    private static unsafe void ActivateTopmostWindowOnDesktop(VirtualDesktop target)
    {
        var hostProcessIds = Process.GetProcessesByName("Microsoft.CmdPal.UI")
            .Select(p => p.Id)
            .ToHashSet();

        PInvoke.EnumWindows((hWnd, _) =>
        {
            if (!PInvoke.IsWindowVisible(hWnd))
            {
                return true; // continue
            }

            const int WS_EX_TOOLWINDOW = 0x00000080;
            int exStyle = PInvoke.GetWindowLong(hWnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0)
            {
                return true; // continue
            }

            // also skip popups
            const uint WS_POPUP = 0x80000000;
            int style = PInvoke.GetWindowLong(hWnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
            if ((style & WS_POPUP) != 0)
            {
                return true; // continue
            }


            if (VirtualDesktop.FromHwnd(hWnd) is not VirtualDesktop onDesktop || onDesktop.Id != target.Id)
            {
                return true; // window lives on another desktop
            }

            if (VirtualDesktop.IsPinnedWindow(hWnd))
            {
                return true; // pinned windows exist on every desktop
            }

            ActivateWindow(hWnd);
            return false; // stop
        }, IntPtr.Zero);
    }

    // SetForegroundWindow from a background process is blocked by the foreground lock;
    // attach our thread to the foreground window's thread to unlock it.
    private static unsafe void ActivateWindow(HWND hWnd)
    {
        try
        {
            var foreground = PInvoke.GetForegroundWindow();
            uint foregroundPid = 0;
            var foregroundThread = (uint)PInvoke.GetWindowThreadProcessId(foreground, &foregroundPid);
            var currentThread = PInvoke.GetCurrentThreadId();
            _ = PInvoke.AttachThreadInput(currentThread, foregroundThread, true);
            _ = PInvoke.SetForegroundWindow(hWnd);
            _ = PInvoke.AttachThreadInput(currentThread, foregroundThread, false);
        }
        catch (Exception e)
        {
            DebugPrint($"ActivateWindow failed\n{e.Message}");
        }
    }

    private sealed partial class MoveWindowToDesktopCommand(VirtualDesktop desktop, int index, bool andSwitchTo) : InvokableCommand
    {
        public override string Name => andSwitchTo ? "Move window and switch" : "Move window here";
        public override string Id => $"dev.vladon.virtualDesktops.moveWindow.{index}";
        public override IconInfo Icon => andSwitchTo ? Icons.NewWindowIcon : Icons.SendIcon;

        public override ICommandResult Invoke()
        {
            try
            {
                HWND hWnd = FindLastNonToolWindow();
                if (hWnd != HWND.Null)
                {
                    string title = string.Empty;
                    var bufferSize = PInvoke.GetWindowTextLength(hWnd) + 1;
                    unsafe
                    {
                        fixed (char* windowNameChars = new char[bufferSize])
                        {
                            if (PInvoke.GetWindowText(hWnd, windowNameChars, bufferSize) == 0)
                            {
                                title = "<unknown>";
                            }

                            title = new string(windowNameChars);
                        }
                    }

                    var fresh = ResolveFresh(desktop);
                    DebugPrint($"Moving window {hWnd} ('{title}') to '{fresh}'");
                    VirtualDesktop.MoveToDesktop(hWnd, fresh);
                    DebugPrint($"...done");
                    DesktopsChanged?.Invoke();

                    if (andSwitchTo)
                    {
                        DebugPrint($"Switching to '{fresh}'");
                        fresh.Switch();
                        DebugPrint($"...done");
                        ActivateWindow(hWnd);
                        DesktopsChanged?.Invoke();
                    }
                }
                else
                {
                    DebugPrint("No eligible window found to move");
                }
            }
            catch (Exception e)
            {
                DebugPrint($"MoveWindowToDesktopCommand invoke\n{e.Message}\n{e.StackTrace}");
            }

            return CommandResult.KeepOpen();
        }
    }

    private sealed partial class SwitchToDesktopCommand(VirtualDesktop desktop, bool isCurrent, bool asBand, int index) : InvokableCommand
    {
        public VirtualDesktop Desktop => desktop;
        public override string Name => asBand ? string.Empty : "Switch to desktop";
        internal bool IsCurrent { get; init; } = isCurrent;
        public override string Id => $"dev.vladon.virtualDesktops.switchTo.{index}";
        public override IconInfo Icon => Icons.Switchcon;
        public override string ToString()
        {
            return $"{(IsCurrent ? "*" : string.Empty)}{Desktop.ToString()}";
        }
        public override ICommandResult Invoke()
        {
            try
            {
                var fresh = ResolveFresh(Desktop);
                DebugPrint($"Switching to '{fresh}'");
                fresh.Switch();
                DebugPrint($"...done");
                ActivateTopmostWindowOnDesktop(fresh);
                DesktopsChanged?.Invoke();
            }
            catch (Exception e)
            {
                DebugPrint($"SwitchToDesktopCommand invoke\n{e.Message}\n{e.StackTrace}");
            }
            return CommandResult.KeepOpen();
        }
    }

    private static void DebugPrint(string? s)
    {
        Debug.WriteLine(s);
    }
}

