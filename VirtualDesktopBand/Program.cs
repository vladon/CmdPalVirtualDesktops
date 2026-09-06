// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CommandPalette.Extensions;
using Shmuelie.WinRTServer;
using Shmuelie.WinRTServer.CsWinRT;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Vladon.CmdPal.VirtualDesktops;

// Append-only lifetime trace in %LOCALAPPDATA%\dev.vladon.virtualdesktops\extension.log.
// Exists because the extension used to disappear without any Event Log trace; this log
// distinguishes a clean self-exit (ProcessExit/UnhandledException get logged) from an
// external TerminateProcess (nothing gets logged).
internal static class LifetimeLog
{
    private static readonly object Gate = new();

    internal static void Write(string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "dev.vladon.virtualdesktops");
            Directory.CreateDirectory(directory);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {message}{Environment.NewLine}";
            lock (Gate)
            {
                File.AppendAllText(Path.Combine(directory, "extension.log"), line);
            }
        }
        catch
        {
            // Logging must never take the extension down.
        }
    }
}

public class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "-RegisterProcessAsComServer")
        {
            global::Shmuelie.WinRTServer.ComServer server = new();
            LifetimeLog.Write($"Started (pid {Environment.ProcessId})");
            AppDomain.CurrentDomain.ProcessExit += (_, _) => LifetimeLog.Write("ProcessExit: main returned or Environment.Exit was called");
            AppDomain.CurrentDomain.UnhandledException += (_, e) => LifetimeLog.Write($"UnhandledException isTerminating={e.IsTerminating}: {e.ExceptionObject}");
            TaskScheduler.UnobservedTaskException += (_, e) => LifetimeLog.Write($"UnobservedTaskException: {e.Exception?.GetBaseException()?.Message}");

            ManualResetEvent extensionDisposedEvent = new(false);

            // We are instantiating an extension instance once above, and returning it every time the callback in RegisterExtension below is called.
            // This makes sure that only one instance of SampleExtension is alive, which is returned every time the host asks for the IExtension object.
            // If you want to instantiate a new instance each time the host asks, create the new instance inside the delegate.
            VirtualDesktopBand extensionInstance = new(extensionDisposedEvent);
            server.RegisterClass<VirtualDesktopBand, IExtension>(() => extensionInstance);
            server.Start();

            // The extension ignores the host's idle release-dispose to keep the dock band
            // functional; this watchdog is the only thing that lets the process exit —
            // when the host process itself is gone, so we can't outlive it as an orphan.
            System.Threading.Timer hostWatchdog = new(_ =>
            {
                LifetimeLog.Write($"Watchdog tick: host processes={Process.GetProcessesByName("Microsoft.CmdPal.UI").Length}");
                if (Process.GetProcessesByName("Microsoft.CmdPal.UI").Length == 0)
                {
                    LifetimeLog.Write("Watchdog: host process not found — exiting");
                    extensionDisposedEvent.Set();
                }
            });
            hostWatchdog.Change(30000, 30000);

            // This will make the main thread wait until the event is signalled by the extension class.
            // The extension ignores idle release-dispose from the host (see VirtualDesktopBand.Dispose),
            // so this event is only signalled by the watchdog below when the host process is gone.
            extensionDisposedEvent.WaitOne();
            server.Stop();
            server.UnsafeDispose();

            hostWatchdog.Dispose();
        }
        else
        {
            Console.WriteLine("Not being launched as a Extension... exiting.");
        }
    }

    private static DateTime _lastHostRestart = DateTime.MinValue;

    // The dock band goes stale across session transitions and the host never re-reads it
    // (microsoft/PowerToys#50367). The only reliable recovery is bouncing the palette
    // process, so on RDP/console session switches we quietly do it for the user.
    internal static void RestartHostForSessionTransition(string reason)
    {
        try
        {
            if ((DateTime.Now - _lastHostRestart).TotalSeconds < 10)
            {
                LifetimeLog.Write($"Session transition {reason}: host restart skipped (rate limit)");
                return;
            }

            string? hostExePath = null;
            foreach (var process in Process.GetProcessesByName("Microsoft.CmdPal.UI"))
            {
                try
                {
                    hostExePath ??= process.MainModule?.FileName;
                }
                catch
                {
                    // MainModule can be inaccessible for some processes; the fallback below covers this.
                }

                process.Kill(entireProcessTree: true);
                process.Dispose();
            }

            Thread.Sleep(1500);
            if (!string.IsNullOrEmpty(hostExePath) && File.Exists(hostExePath))
            {
                // Launch the exe directly instead of app activation: the latter pops the
                // palette window open every time, and the user only wants the dock.
                Process.Start(new ProcessStartInfo(hostExePath) { UseShellExecute = true });
                LifetimeLog.Write($"Session transition {reason}: host relaunched from {hostExePath}");
            }
            else
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\Microsoft.CommandPalette_8wekyb3d8bbwe!App")
                {
                    UseShellExecute = true,
                });
                LifetimeLog.Write($"Session transition {reason}: host relaunched via AppsFolder");
            }

            // The palette window can pop up on relaunch (and sometimes a bit later);
            // poll for a while and hide it — the user only wants the dock.
            HidePaletteWindowsAfterRelaunch();
        }
        catch (Exception e)
        {
            LifetimeLog.Write($"Session transition {reason}: host restart failed — {e.Message}");
        }
    }

    private static void HidePaletteWindowsAfterRelaunch()
    {
        _ = Task.Run(async () =>
        {
            var hidAny = false;
            for (var attempt = 0; attempt < 30; attempt++)
            {
                hidAny |= HidePaletteWindows();
                await Task.Delay(500);
            }

            if (!hidAny)
            {
                LifetimeLog.Write("Palette window hide polling finished — nothing to hide");
            }
        });
    }

    private static unsafe bool HidePaletteWindows()
    {
        var hidAny = false;
        try
        {
            PInvoke.EnumWindows((hWnd, _) =>
            {
                var bufferSize = PInvoke.GetWindowTextLength(hWnd) + 1;
                fixed (char* windowNameChars = new char[bufferSize])
                {
                    if (PInvoke.GetWindowText(hWnd, windowNameChars, bufferSize) > 0)
                    {
                        var title = new string(windowNameChars);
                        if (title == "Command Palette" && PInvoke.IsWindowVisible(hWnd))
                        {
                            PInvoke.ShowWindow(hWnd, SHOW_WINDOW_CMD.SW_HIDE);
                            hidAny = true;
                            LifetimeLog.Write("Hid the palette window that popped up on host relaunch");
                        }
                    }
                }

                return true;
            }, IntPtr.Zero);
        }
        catch (Exception e)
        {
            LifetimeLog.Write($"HidePaletteWindows failed — {e.Message}");
        }

        return hidAny;
    }
}
