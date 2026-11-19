// language: C#, file: src/Core/Elevation.cs, target: Windows 10/11, .NET 8
// RoutRunner ships asInvoker and elevates itself: 9Router and OmniRoute must inherit a
// full administrator token, so the supervisor has to hold one first.
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace RoutRunner.Core;

public static class Elevation
{
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    public static string Describe => IsElevated ? "administrator" : "standard user";

    /// <summary>
    /// Starts a second copy of this executable through the UAC prompt.
    /// Returns false when the user declines (Win32 1223) — caller decides what to say.
    /// </summary>
    public static bool TryRelaunchElevated(string extraArgs, out bool cancelled)
    {
        cancelled = false;
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "--elevated" + (string.IsNullOrWhiteSpace(extraArgs) ? string.Empty : " " + extraArgs),
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory
        };

        try
        {
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            cancelled = true;
            return false;
        }
        catch
        {
            return false;
        }
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_ELEVATION
    {
        public int TokenIsElevated;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr token, int infoClass, out TOKEN_ELEVATION info, int infoLength, out int returned);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// Reads TokenElevation for another process. Null when the token cannot be opened.
    /// Used to prove the spawned PowerShell host — and therefore node under it — is elevated.
    /// </summary>
    public static bool? ProbeChildElevation(int pid)
    {
        var process = IntPtr.Zero;
        var token = IntPtr.Zero;
        try
        {
            process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process == IntPtr.Zero) return null;

            if (!OpenProcessToken(process, TOKEN_QUERY, out token)) return null;

            if (!GetTokenInformation(token, TokenElevation, out var info, Marshal.SizeOf<TOKEN_ELEVATION>(), out _))
                return null;

            return info.TokenIsElevated != 0;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
            if (process != IntPtr.Zero) CloseHandle(process);
        }
    }
}
