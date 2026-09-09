// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.IO;
using System.Threading;
using Windows.Win32;
using Windows.Win32.System.Com;

namespace Vladon.CmdPal.VirtualDesktops.Supervisor;

// Detached watchdog for the VirtualDesktops extension. Shipped inside the MSIX as a
// trimmed single-file exe; the extension copies it to %LOCALAPPDATA%\dev.vladon.
// virtualdesktops and spawns it, so it survives the by-name process kills that take
// the extension down (different exe name) and is not a child anyone kills (spawned
// detached). Every 3s: if the palette host is alive but the extension process is
// gone (the host terminates released extensions — TerminateProcess survives no
// in-process defense), revive the extension via a COM activation on its CLSID —
// the SCM launches the CURRENT packaged exe with the proper context — and bounce
// the host so its dock band re-registers (microsoft/PowerToys#50367).
internal static class Program
{
    private const string ExtensionExeName = "VirtualDesktopsExtension";
    private const string HostExeName = "Microsoft.CmdPal.UI";
    private const string ExtensionClsid = "f1270cad-9bc8-45c2-83a9-bee1cc52b60d";

    private static readonly object Gate = new();

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length < 2 || args[0] != "--supervisor")
        {
            Console.WriteLine("Not being launched as a supervisor... exiting.");
            return;
        }

        Log($"Supervisor started (watching {args[1]})");
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
            Log("Another instance owns the mutex — exit");
            return;
        }

        while (true)
        {
            Thread.Sleep(3000);
            try
            {
                var hostAlive = Process.GetProcessesByName(HostExeName).Length > 0;
                var extensionAlive = Process.GetProcessesByName(ExtensionExeName).Length > 0;
                if (!hostAlive || extensionAlive)
                {
                    continue;
                }

                Log("Extension died while the host is alive — reviving via COM activation and bouncing the host");
                ActivateExtensionViaCom();
                foreach (var p in Process.GetProcessesByName(HostExeName))
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
                Log($"Supervisor tick failed: {e.Message}");
            }
        }
    }

    // COM activation on the extension's CLSID: the SCM launches the CURRENT packaged
    // exe (with -Embedding) exactly like the palette host does — no stale paths, no
    // version mismatches. The activated object is intentionally left unreferenced.
    private static void ActivateExtensionViaCom()
    {
        Guid clsid = new(ExtensionClsid);
        Guid iid = new("00000000-0000-0000-C000-000000000046"); // IID_IUnknown
        var hr = PInvoke.CoCreateInstance(in clsid, null, CLSCTX.CLSCTX_LOCAL_SERVER, in iid, out var ppv);
        Log($"COM activation: hr=0x{hr.Value:x8}, launched={(ppv != null)}");
    }

    private static void Log(string message)
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
                File.AppendAllText(Path.Combine(directory, "supervisor.log"), line);
            }
        }
        catch
        {
            // Logging must never take the supervisor down.
        }
    }
}
