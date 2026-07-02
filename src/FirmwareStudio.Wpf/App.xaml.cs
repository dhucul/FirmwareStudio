using System.Runtime.InteropServices;
using System.Windows;
using FirmwareStudio.Core.Drives;
using FirmwareStudio.Core.Scsi;

namespace FirmwareStudio.Wpf;

public partial class App : Application
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int dwProcessId);
    private const int ATTACH_PARENT_PROCESS = -1;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Headless self-test: print to the launching terminal and exit, no UI.
        if (e.Args.Length > 0 && e.Args[0] == "--smoke-scsi")
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            Shutdown(RunSmoke());
            return;
        }
        base.OnStartup(e);   // StartupUri creates MainWindow
    }

    private static int RunSmoke()
    {
        Console.WriteLine("FirmwareStudio --smoke-scsi");
        string? layout = ScsiDevice.VerifyLayout();
        Console.WriteLine(layout is null ? "[OK]   SPTD layout 56/56" : $"[FAIL] {layout}");
        if (layout is not null) return 2;

        var drives = DriveEnumerator.Scan();
        Console.WriteLine($"[OK]   Optical drives: {drives.Count}");
        foreach (var d in drives) Console.WriteLine($"         {d.Display}");
        return 0;
    }
}
