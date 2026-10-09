namespace Toasty;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Used by the installer/uninstaller: "Toasty.exe --autostart on|off", then exit.
        if (args.Length == 2 && args[0] == "--autostart")
        {
            return Autostart.Set(args[1] == "on") || args[1] == "off" ? 0 : 1;
        }

        // Used by the installer/uninstaller: ask a running Toasty to exit cleanly (saving any
        // game session in progress), and wait up to 10s for it to go.
        if (args.Length == 1 && args[0] == "--exit")
        {
            try
            {
                using var exit = EventWaitHandle.OpenExisting(ExitEventName);
                exit.Set();
                using var running = Mutex.OpenExisting(MutexName);
                try { running.WaitOne(10_000); } catch (AbandonedMutexException) { }
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // Not running.
            }
            return 0;
        }

        // Only one Toasty at a time, otherwise every icon shows up twice.
        using var mutex = new Mutex(true, MutexName, out bool isFirst);
        if (!isFirst) return 0;

        // A tray app shouldn't die (or show a crash dialog) over one bad click or paint:
        // log the error and keep going.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => LogError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogError(e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();
#pragma warning disable WFO5001 // dark mode is still marked experimental; it's exactly what the panel needs
        Application.SetColorMode(SystemColorMode.Dark);
#pragma warning restore WFO5001
        Application.Run(new TrayApp());
        return 0;
    }

    internal const string MutexName = @"Local\Toasty.SingleInstance";
    internal const string ExitEventName = @"Local\Toasty.Exit";

    /// <summary>Appends to %AppData%\Toasty\error.log, keeping it small.</summary>
    internal static void LogError(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Toasty");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "error.log");
            if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Move(path, path + ".old", overwrite: true);
            var version = typeof(Program).Assembly.GetName().Version?.ToString(3);
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Toasty {version}\n{ex}\n\n");
        }
        catch
        {
            // Nowhere left to report to.
        }
    }
}
