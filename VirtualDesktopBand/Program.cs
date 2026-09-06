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
            if ((DateTime.Now - _lastHostRestart).TotalSeconds < 60)
            {
                LifetimeLog.Write($"Session transition {reason}: host restart skipped (rate limit)");
                return;
            }

            _lastHostRestart = DateTime.Now;
            LifetimeLog.Write($"Session transition {reason}: restarting CmdPal host");
            foreach (var process in Process.GetProcessesByName("Microsoft.CmdPal.UI"))
            {
                process.Kill(entireProcessTree: true);
                process.Dispose();
            }

            Thread.Sleep(1500);
            Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\Microsoft.CommandPalette_8wekyb3d8bbwe!App")
            {
                UseShellExecute = true,
            });
            LifetimeLog.Write("Session transition: host relaunched");
        }
        catch (Exception e)
        {
            LifetimeLog.Write($"Session transition {reason}: host restart failed — {e.Message}");
        }
    }
}
