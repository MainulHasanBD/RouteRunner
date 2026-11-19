// language: C#, file: src/Core/Installer.cs, target: Windows 10/11, .NET 8
// Global npm install with live output. Only ever runs when dj clicks Install:
// the app refuses to start a service whose package is missing.
using System.Diagnostics;
using System.Text;

namespace RoutRunner.Core;

public static class Installer
{
    public static async Task<bool> InstallAsync(string packageId, Action<string> onLine, CancellationToken token)
    {
        var npm = ToolLocator.FindNpm();
        if (npm is null)
        {
            onLine("[install] npm.cmd not found on PATH — install Node.js first");
            return false;
        }

        var shell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(shell)) shell = "powershell.exe";

        var psi = new ProcessStartInfo
        {
            FileName = shell,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("-NoLogo");
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add($"& '{npm.Replace("'", "''")}' install -g {packageId}");
        psi.Environment["NO_COLOR"] = "1";

        onLine($"[install] npm install -g {packageId}");

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        proc.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) onLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) onLine(e.Data); };
        proc.Exited += (_, _) => completion.TrySetResult(SafeExitCode(proc));

        if (!proc.Start()) return false;
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await completion.Task.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            onLine("[install] cancelled");
            return false;
        }

        var ok = proc.ExitCode == 0;
        onLine(ok ? "[install] done" : $"[install] failed with code {proc.ExitCode}");
        return ok;
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; }
        catch { return -1; }
    }
}
