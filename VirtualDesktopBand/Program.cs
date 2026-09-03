// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CommandPalette.Extensions;
using Shmuelie.WinRTServer;
using Shmuelie.WinRTServer.CsWinRT;
using System;
using System.Diagnostics;
using System.Threading;

namespace Vladon.CmdPal.VirtualDesktops;

public class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "-RegisterProcessAsComServer")
        {
            global::Shmuelie.WinRTServer.ComServer server = new();

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
}
