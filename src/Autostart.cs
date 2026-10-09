using System.Diagnostics;
using System.Security;
using System.Security.Principal;

namespace Toasty;

/// <summary>
/// "Start with Windows" via a logon scheduled task. A Run registry key can't launch an
/// admin app without a UAC prompt every login; a task with highest privileges can.
/// </summary>
public static class Autostart
{
    private const string TaskName = "Toasty";

    public static bool IsEnabled() => RunSchtasks($"/Query /TN \"{TaskName}\"") == 0;

    public static bool Set(bool enabled)
    {
        if (!enabled) return RunSchtasks($"/Delete /F /TN \"{TaskName}\"") == 0;

        // Created from XML so we can switch off the defaults that would kill a tray app
        // (72h execution limit, stopping on battery).
        string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        string exe = SecurityElement.Escape(Environment.ProcessPath!);
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts Toasty (tray hardware monitor) at logon.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{exe}</Command></Exec></Actions>
            </Task>
            """;

        string path = Path.Combine(Path.GetTempPath(), "toasty-task.xml");
        try
        {
            File.WriteAllText(path, xml, System.Text.Encoding.Unicode);
            return RunSchtasks($"/Create /F /TN \"{TaskName}\" /XML \"{path}\"") == 0;
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static int RunSchtasks(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit(10_000);
        return p.HasExited ? p.ExitCode : -1;
    }
}
