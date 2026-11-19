// language: C#, file: src/Core/StartupTask.cs, target: Windows 10/11, .NET 8
// Two scheduled tasks, both RunLevel=HighestAvailable:
//   RoutRunner        logon trigger, action `--minimized`      -> autostart with no UAC
//   RoutRunner.Launch no trigger, no arguments                 -> silent elevated launch on demand
// A registry Run key would raise a UAC prompt at every logon; a task does not.
using System.Diagnostics;
using System.Security;
using System.Text;

namespace RoutRunner.Core;

public static class StartupTask
{
    public const string LogonTaskName = "RoutRunner";
    public const string LaunchTaskName = "RoutRunner.Launch";

    /// <summary>True when the logon task is registered — the flag the tray checkbox reflects.</summary>
    public static bool Exists() => Query(LogonTaskName);

    /// <summary>
    /// Silent elevation: runs the on-demand task, which already carries a full admin token.
    /// Returns false when the task is absent or the scheduler refuses to start it.
    /// </summary>
    public static bool TryRunLaunch()
    {
        if (!Query(LaunchTaskName)) return false;
        var (code, _, _) = Run("schtasks.exe", $"/Run /TN \"{LaunchTaskName}\"");
        return code == 0;
    }

    public static bool Create(out string message)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            message = "cannot resolve this executable's path";
            return false;
        }

        var path = Path.GetFullPath(exe);
        var failures = new List<string>();

        if (!Register(LogonTaskName, path, autostart: true, out var logonError))
            failures.Add($"{LogonTaskName}: {logonError}");
        if (!Register(LaunchTaskName, path, autostart: false, out var launchError))
            failures.Add($"{LaunchTaskName}: {launchError}");

        message = failures.Count == 0
            ? "registered scheduled tasks (elevated logon autostart + silent elevated launch)"
            : string.Join(" | ", failures);
        return failures.Count == 0;
    }

    public static bool Delete(out string message)
    {
        var problems = new List<string>();

        foreach (var name in new[] { LogonTaskName, LaunchTaskName })
        {
            if (!Query(name))
            {
                continue;
            }

            var (code, stdout, stderr) = Run("schtasks.exe", $"/Delete /TN \"{name}\" /F");
            if (code != 0) problems.Add($"{name}: {Trim(stderr) + Trim(stdout)}");
        }

        message = problems.Count == 0 ? "startup tasks removed" : string.Join(" | ", problems);
        return problems.Count == 0;
    }

    private static bool Query(string name)
    {
        var (code, _, _) = Run("schtasks.exe", $"/Query /TN \"{name}\"");
        return code == 0;
    }

    private static bool Register(string name, string exePath, bool autostart, out string error)
    {
        var file = Path.Combine(Path.GetTempPath(), name.Replace('.', '-') + ".xml");
        try
        {
            File.WriteAllText(file, BuildXml(name, exePath, autostart), Encoding.Unicode);
        }
        catch (Exception ex)
        {
            error = "could not write task xml: " + ex.Message;
            return false;
        }

        var (code, stdout, stderr) = Run("schtasks.exe", $"/Create /TN \"{name}\" /XML \"{file}\" /F");
        try { File.Delete(file); } catch { }

        error = code == 0 ? string.Empty : Trim(stderr) + Trim(stdout);
        return code == 0;
    }

    private static string Trim(string text) => (text ?? string.Empty).Trim();

    private static string BuildXml(string name, string exePath, bool autostart)
    {
        var user = WindowsIdentityName();
        var dir = Path.GetDirectoryName(exePath) ?? string.Empty;
        var cmd = SecurityElement.Escape(exePath) ?? exePath;
        var work = SecurityElement.Escape(dir) ?? dir;
        var who = SecurityElement.Escape(user) ?? user;
        var taskName = SecurityElement.Escape(name) ?? name;
        var arguments = autostart ? "--minimized" : string.Empty;
        var description = autostart
            ? "RoutRunner - elevated logon autostart for 9Router and OmniRoute"
            : "RoutRunner - silent elevated launch (no UAC prompt)";

        // Empty <Triggers/> is schema-valid and means "on demand only".
        var triggers = autostart
            ? $"""
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{who}</UserId>
    </LogonTrigger>
  </Triggers>
"""
            : $"  <Triggers>{Environment.NewLine}  </Triggers>{Environment.NewLine}";

        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>{SecurityElement.Escape(description)}</Description>
    <URI>\{taskName}</URI>
  </RegistrationInfo>
{triggers}  <Principals>
    <Principal id="Author">
      <UserId>{who}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{cmd}</Command>
      <Arguments>{arguments}</Arguments>
      <WorkingDirectory>{work}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
""";
    }

    private static string WindowsIdentityName()
    {
        try
        {
            return System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        }
        catch
        {
            var domain = Environment.UserDomainName;
            return string.IsNullOrEmpty(domain) ? Environment.UserName : $"{domain}\\{Environment.UserName}";
        }
    }

    private static (int Code, string StdOut, string StdErr) Run(string file, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, file),
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc is null) return (-1, string.Empty, "could not start " + file);

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(15000);
            return (proc.HasExited ? proc.ExitCode : -1, stdout, stderr);
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }
}
