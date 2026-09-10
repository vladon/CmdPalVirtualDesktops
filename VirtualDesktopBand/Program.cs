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
using Windows.Win32.System.Com;
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

            // Detached supervisor: a separate trimmed single-file exe shipped NEXT TO this
            // exe in the package and spawned directly from there. Its different exe name
            // (VirtualDesktopsSupervisor.exe) keeps it alive through the by-name process
            // kills that take the extension down, and spawning from the package install
            // dir is the one launch context proven reliable for children. After an
            // in-place update the previous version's supervisor keeps watching (its
            // revive is a COM activation — version-independent), the new spawn then
            // exits on the supervisor mutex, so exactly one stays alive. If the host
            // terminates this process after an idle release, the supervisor revives it
            // via COM activation and bounces the host, restoring the dock band without
            // any user action.
            try
            {
                var supervisorExe = Path.Combine(AppContext.BaseDirectory, "VirtualDesktopsSupervisor.exe");
                var selfExe = Environment.ProcessPath ?? string.Empty;
                Process.Start(new ProcessStartInfo(supervisorExe, "--supervisor \"" + selfExe + "\"") { UseShellExecute = true });
                LifetimeLog.Write("Supervisor spawned");
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


}
