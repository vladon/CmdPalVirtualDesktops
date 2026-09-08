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
        if (args.Length > 0 && args[0] == "--supervisor")
        {
            RunSupervisor(args.Length > 1 ? args[1] : null);
            return;
        }

        if (args.Length > 0 && args[0] == "-RegisterProcessAsComServer")
        {
            global::Shmuelie.WinRTServer.ComServer server = new();
            LifetimeLog.Write($"Started (pid {Environment.ProcessId})");
            AppDomain.CurrentDomain.ProcessExit += (_, _) => LifetimeLog.Write("ProcessExit: main returned or Environment.Exit was called");
            AppDomain.CurrentDomain.UnhandledException += (_, e) => LifetimeLog.Write($"UnhandledException isTerminating={e.IsTerminating}: {e.ExceptionObject}");
            TaskScheduler.UnobservedTaskException += (_, e) => LifetimeLog.Write($"UnobservedTaskException: {e.Exception?.GetBaseException()?.Message}");

            // Detached supervisor: if the host terminates this process after an idle
            // release (TerminateProcess survives no in-process defense), the supervisor
            // relaunches it in COM server mode within a few seconds.
            try
            {
                var selfExe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(selfExe))
                {
                    Process.Start(new ProcessStartInfo(selfExe, "--supervisor \"" + selfExe + "\"") { UseShellExecute = true });
                    LifetimeLog.Write("Supervisor spawned");
                }
            }
            catch (Exception e)
            {
                LifetimeLog.Write($"Supervisor spawn failed: {e.Message}");
            }

            ManualResetEvent extensionDisposedEvent = new(false);

            // We are instantiating an extension instance once above, and returning it every time the callback in RegisterExtension below is called.
            // This makes sure that only one instance of SampleExtension is alive, which is returned every time the host asks for the IExtension object.
            // If you want to instantiate a new instance each time the host asks, create the new instance inside the delegate.
            VirtualDesktopBand extensionInstance = new(extensionDisposedEvent);
            server.RegisterClass<VirtualDesktopBand, IExtension>(() => extensionInstance);
            server.Start();

            // The host releases idle extensions, and with COMGLB_FAST_RUNDOWN (set by
            // Shmuelie) that rundowns the whole process — silently killing the dock band
            // binding. Hold a server-process lock: COM may not rundown or exit the process
            // while it is held, so the band binding stays alive across idle releases.
            LifetimeLog.Write($"Server lock acquired: {PInvoke.CoAddRefServerProcess()}");

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
            LifetimeLog.Write($"Exiting — releasing server lock (refs={PInvoke.CoReleaseServerProcess()})");
            server.Stop();
            server.UnsafeDispose();

            hostWatchdog.Dispose();
        }
        else
        {
            Console.WriteLine("Not being launched as a Extension... exiting.");
        }
    }

    // Detached watchdog process: every 5 seconds, if the palette host is alive but our
    // extension process is gone (the host terminates released extensions — TerminateProcess
    // survives no in-process defense), start it again in COM server mode. The relaunched
    // instance re-registers the COM class factory, so the host can re-activate the
    // extension (e.g. when the user re-pins the dock band).
    private static void RunSupervisor(string? selfExePath)
    {
        LifetimeLog.Write($"Supervisor started (watching {selfExePath ?? "???"})");
        using var mutex = new Mutex(false, @"Local\VD2Supervisor");
        var acquired = false;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            acquired = true; // the previous owner died — the lock is ours now
        }

        if (!acquired)
        {
            LifetimeLog.Write("Supervisor: another instance owns the mutex — exit");
            return;
        }

        while (true)
        {
            Thread.Sleep(3000);
            try
            {
                var hostAlive = Process.GetProcessesByName("Microsoft.CmdPal.UI").Length > 0;
                var extensionAlive = Process.GetProcessesByName("VirtualDesktopsExtension").Length > 0;
                if (!hostAlive || extensionAlive)
                {
                    continue;
                }

                LifetimeLog.Write("Supervisor: extension died while the host is alive — relaunching and bouncing the host");
                Process.Start(new ProcessStartInfo(selfExePath, "-RegisterProcessAsComServer") { UseShellExecute = true });
                Thread.Sleep(1500);
                foreach (var p in Process.GetProcessesByName("Microsoft.CmdPal.UI"))
                {
                    p.Kill(entireProcessTree: true);
                    p.Dispose();
                }

                Thread.Sleep(1500);
                Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\Microsoft.CommandPalette_8wekyb3d8bbwe!App")
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception e)
            {
                LifetimeLog.Write($"Supervisor tick failed: {e.Message}");
            }
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
