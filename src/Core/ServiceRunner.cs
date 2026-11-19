// language: C#, file: src/Core/ServiceRunner.cs, target: Windows 10/11, .NET 8
// One supervised service = one PowerShell host -> one npm shim -> one node tree,
// pinned in a kill-on-close job object. Readiness is proven by a TCP connect,
// never by a sleep.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using RoutRunner.Native;

namespace RoutRunner.Core;

public enum ServiceState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Failed,
    External
}

public sealed class ServiceRunner : IDisposable
{
    private static readonly Regex Ansi = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);

    private readonly object _sync = new();
    private Process? _proc;
    private JobHandle? _job;
    private CancellationTokenSource? _probeCts;
    private int _generation;
    private int _restartAttempts;
    private volatile bool _disposed;
    private volatile bool _wantedRunning;

    public string Id { get; }
    public string DisplayName { get; }
    public int Port { get; set; }
    public string ExtraArgs { get; set; } = "";
    public bool AutoRestart { get; set; } = true;
    public LogBuffer Log { get; }

    public ServiceState State { get; private set; } = ServiceState.Stopped;
    public string Detail { get; private set; } = "not started";
    public bool Owned { get; private set; }
    public ToolInfo Tool { get; private set; }
    public int? ExitCode { get; private set; }
    public bool? ChildElevated { get; private set; }

    public event Action<ServiceRunner>? Changed;
    public event Action<ServiceRunner, string>? LineWritten;

    public ServiceRunner(string id, string displayName, int port)
    {
        Id = id;
        DisplayName = displayName;
        Port = port;
        Log = new LogBuffer(id);
        Tool = ToolLocator.Resolve(id, displayName);
    }

    public string Endpoint => $"http://127.0.0.1:{Port}";

    public void RefreshTool()
    {
        Tool = ToolLocator.Resolve(Id, DisplayName);
        RaiseChanged();
    }

    public static bool PortOpen(int port)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(IPAddress.Loopback, port);
            if (!connect.Wait(500)) return false;
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    public void Start(bool automatic = false)
    {
        lock (_sync)
        {
            if (_disposed) return;
            if (State is ServiceState.Starting or ServiceState.Running) return;
            _wantedRunning = true;
            if (!automatic) _restartAttempts = 0;

            RefreshToolQuiet();
            if (!Tool.Installed)
            {
                Note($"[preflight] {Tool.Display} is not usable — {Tool.Summary}");
                SetState(ServiceState.Failed, "not installed: " + Tool.Summary);
                return;
            }

            if (PortOpen(Port) && !Owned)
            {
                Note($"[preflight] port {Port} already has a listener — not starting a second {DisplayName}");
                SetState(ServiceState.External, $"port {Port} held by another process");
                return;
            }

            try
            {
                var psi = BuildStartInfo();
                var proc = new Process
                {
                    StartInfo = psi,
                    EnableRaisingEvents = true
                };
                proc.OutputDataReceived += (_, e) => OnStdout(e.Data);
                proc.ErrorDataReceived += (_, e) => OnStderr(e.Data);
                proc.Exited += (_, _) => OnExited();

                var generation = ++_generation;
                if (!proc.Start())
                {
                    Note("[spawn] Process.Start returned false");
                    SetState(ServiceState.Failed, "could not spawn powershell");
                    return;
                }

                _proc = proc;
                Owned = true;
                ExitCode = null;
                ChildElevated = null;
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                _job?.Dispose();
                _job = JobHandle.Create();
                _job?.Attach(proc);

                var stamp = automatic ? "auto-restart" : "start";
                Note($"[spawn:{stamp}] powershell.exe {DescribeArguments()}");
                SetState(ServiceState.Starting, $"launching on port {Port}");

                _probeCts?.Cancel();
                _probeCts?.Dispose();
                _probeCts = new CancellationTokenSource();
                _ = ProbeReadinessAsync(generation, _probeCts.Token);
            }
            catch (Exception ex)
            {
                Note($"[spawn] {ex.GetType().Name}: {ex.Message}");
                SetState(ServiceState.Failed, ex.Message);
            }
        }
    }

    private void RefreshToolQuiet()
    {
        Tool = ToolLocator.Resolve(Id, DisplayName);
    }

    private ProcessStartInfo BuildStartInfo()
    {
        var shell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(shell)) shell = "powershell.exe";

        var shim = Tool.ShimPath!;
        var command = $"& '{shim.Replace("'", "''")}' --port {Port}";
        if (!string.IsNullOrWhiteSpace(ExtraArgs)) command += " " + ExtraArgs;

        var psi = new ProcessStartInfo
        {
            FileName = shell,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
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
        psi.ArgumentList.Add(command);

        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["FORCE_COLOR"] = "0";
        psi.Environment["CLICOLOR"] = "0";
        psi.Environment["TERM"] = "dumb";

        return psi;
    }

    private string DescribeArguments() =>
        $"-NoProfile -ExecutionPolicy Bypass -Command \"& '{Tool.ShimPath}' --port {Port}" +
        (string.IsNullOrWhiteSpace(ExtraArgs) ? "\"" : " " + ExtraArgs + "\"");

    private async Task ProbeReadinessAsync(int generation, CancellationToken token)
    {
        var timeout = Id == "omniroute" ? TimeSpan.FromSeconds(150) : TimeSpan.FromSeconds(90);
        var deadline = DateTime.UtcNow + timeout;

        try
        {
            while (DateTime.UtcNow < deadline)
            {
                if (token.IsCancellationRequested || generation != _generation) return;

                if (PortOpen(Port))
                {
                    if (generation != _generation) return;
                    _restartAttempts = 0;

                    var proc = _proc;
                    var elevated = proc is null ? null : Elevation.ProbeChildElevation(proc.Id);
                    ChildElevated = elevated;
                    var badge = elevated switch
                    {
                        true => " · elevated",
                        false => " · standard user",
                        _ => string.Empty
                    };

                    Note($"[ready] listening on 127.0.0.1:{Port}{badge}");
                    SetState(ServiceState.Running, $"listening on 127.0.0.1:{Port}{badge}");
                    return;
                }

                var live = _proc;
                if (live is null || live.HasExited) return; // OnExited owns the state

                await Task.Delay(500, token).ConfigureAwait(false);
            }

            if (generation == _generation && State == ServiceState.Starting)
            {
                Note($"[ready] no TCP answer on {Port} after {timeout.TotalSeconds:0}s — process still alive");
                SetState(ServiceState.Failed, $"no listener on {Port} after {timeout.TotalSeconds:0}s");
            }
        }
        catch (OperationCanceledException)
        {
            // probe cancelled by an explicit stop
        }
        catch (Exception ex)
        {
            Note($"[probe] {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnStdout(string? data)
    {
        if (string.IsNullOrEmpty(data)) return;
        Note(data);
    }

    private void OnStderr(string? data)
    {
        if (string.IsNullOrEmpty(data)) return;
        Note(data);
    }

    private void OnExited()
    {
        int? code = null;
        try
        {
            code = _proc?.ExitCode;
        }
        catch
        {
            // process already reaped
        }

        bool wasStopping;
        lock (_sync)
        {
            wasStopping = State == ServiceState.Stopping;
            ExitCode = code;
            _job?.Dispose();
            _job = null;
            Owned = false;
            _proc?.Dispose();
            _proc = null;
            _generation++;
        }

        if (_disposed) return;

        if (wasStopping || !_wantedRunning)
        {
            Note("[exit] stopped");
            SetState(ServiceState.Stopped, "stopped");
            return;
        }

        Note($"[exit] powershell exited with code {code?.ToString() ?? "?"}");
        SetState(ServiceState.Failed, $"exited (code {code?.ToString() ?? "?"})");

        if (AutoRestart && _wantedRunning && _restartAttempts < 5)
        {
            _restartAttempts++;
            var delay = TimeSpan.FromSeconds(5 * _restartAttempts);
            Note($"[watchdog] restarting in {delay.TotalSeconds:0}s (attempt {_restartAttempts}/5)");
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                    if (!_disposed && _wantedRunning) Start(automatic: true);
                }
                catch
                {
                    // supervisor thread never takes the app down
                }
            });
        }
    }

    public void Stop(bool killExternal = false)
    {
        lock (_sync)
        {
            _wantedRunning = false;
            _probeCts?.Cancel();

            if (!Owned)
            {
                if (State == ServiceState.External)
                {
                    if (killExternal)
                    {
                        Note("[stop] taking the port from the external listener");
                        SetState(ServiceState.Stopping, "killing external listener");
                        Task.Run(KillWhoeverOwnsPort);
                    }
                    else
                    {
                        Note("[stop] external instance — use 'Kill' to take the port by force");
                    }
                }
                else if (State != ServiceState.External)
                {
                    SetState(ServiceState.Stopped, "stopped");
                }
                return;
            }

            if (State == ServiceState.Stopping) return;

            SetState(ServiceState.Stopping, "stopping");
            var proc = _proc;
            var job = _job;

            // Never block the UI thread on WaitForExit — a killed node tree can take seconds.
            Task.Run(() => StopWorker(proc, job));
        }
    }

    private void StopWorker(Process? proc, JobHandle? job)
    {
        job?.Terminate();

        if (proc is not null)
        {
            try
            {
                if (!proc.WaitForExit(4000))
                {
                    Note("[stop] job terminate did not land — falling back to taskkill /T /F");
                    KillTree(proc.Id);
                    proc.WaitForExit(3000);
                }
            }
            catch (Exception)
            {
                // process object already reaped by the exit handler
            }
        }

        lock (_sync)
        {
            if (ReferenceEquals(_proc, proc))
            {
                _job?.Dispose();
                _job = null;
                _proc?.Dispose();
                _proc = null;
                Owned = false;
            }
        }

        if (State != ServiceState.Stopped) SetState(ServiceState.Stopped, "stopped");
    }

    private void KillWhoeverOwnsPort()
    {
        var pid = FindPortOwner(Port);
        if (pid is null)
        {
            Note($"[stop] no listener found on port {Port}");
            SetState(ServiceState.Stopped, "stopped");
            return;
        }

        Note($"[stop] terminating external listener pid {pid} on port {Port}");
        KillTree(pid.Value);
        Thread.Sleep(800);

        if (PortOpen(Port))
        {
            Note("[stop] port still held — process likely runs elevated; relaunch RoutRunner as administrator");
            SetState(ServiceState.External, $"port {Port} still held (access denied?)");
        }
        else
        {
            SetState(ServiceState.Stopped, "external listener terminated");
        }
    }

    private static void KillTree(int pid)
    {
        try
        {
            using var killer = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "taskkill.exe"),
                Arguments = $"/PID {pid} /T /F",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            killer?.WaitForExit(6000);
        }
        catch
        {
            // nothing else to try
        }
    }

    public static int? FindPortOwner(int port)
    {
        try
        {
            using var netstat = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "netstat.exe"),
                Arguments = "-ano -p tcp",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            });
            if (netstat is null) return null;
            var output = netstat.StandardOutput.ReadToEnd();
            netstat.WaitForExit(5000);

            var needle = ":" + port;
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("TCP", StringComparison.OrdinalIgnoreCase)) continue;
                if (!trimmed.Contains("LISTENING", StringComparison.OrdinalIgnoreCase)) continue;

                var cols = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (cols.Length < 5) continue;
                if (!cols[1].EndsWith(needle, StringComparison.Ordinal)) continue;
                if (int.TryParse(cols[4], out var pid)) return pid;
            }
        }
        catch
        {
            // netstat unavailable — caller degrades to "not found"
        }
        return null;
    }

    /// <summary>External probe used by the health timer.</summary>
    public void SyncExternalState()
    {
        if (Owned || State is ServiceState.Starting or ServiceState.Stopping) return;

        var listening = PortOpen(Port);
        if (listening && State != ServiceState.External)
        {
            SetState(ServiceState.External, $"port {Port} held by another process");
        }
        else if (!listening && State == ServiceState.External)
        {
            SetState(ServiceState.Stopped, "external listener gone");
        }
    }

    private void Note(string raw)
    {
        var line = Ansi.Replace(raw, string.Empty).Replace("\r", string.Empty);
        if (string.IsNullOrWhiteSpace(line)) return;
        var stamped = $"{DateTime.Now:HH:mm:ss}  {line}";
        Log.Write(stamped);
        LineWritten?.Invoke(this, stamped);
    }

    private void SetState(ServiceState state, string detail)
    {
        State = state;
        Detail = detail;
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this);
        }
        catch
        {
            // UI subscribers must never break supervision
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _wantedRunning = false;
        try
        {
            _probeCts?.Cancel();
            _job?.Terminate();

            var proc = _proc;
            if (proc is not null)
            {
                try
                {
                    if (!proc.WaitForExit(3000)) KillTree(proc.Id);
                }
                catch
                {
                    // already gone
                }
            }
        }
        catch
        {
            // shutdown path never throws
        }

        _probeCts?.Dispose();
        _job?.Dispose();
        _proc?.Dispose();
    }
}
