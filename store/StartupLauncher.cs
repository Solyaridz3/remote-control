using System;
using System.Diagnostics;
using System.IO;

internal static class StartupLauncher
{
    private static void Main()
    {
        string directory = AppDomain.CurrentDomain.BaseDirectory;
        var start = new ProcessStartInfo(Path.Combine(directory, "RemoteController.exe"), "--minimized") {
            UseShellExecute = false,
            WorkingDirectory = directory
        };
        using (Process.Start(start)) { }
    }
}
