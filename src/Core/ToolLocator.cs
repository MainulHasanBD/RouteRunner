// language: C#, file: src/Core/ToolLocator.cs, target: Windows 10/11, .NET 8
// Resolves the npm global shims the same way a shell would, then reads the
// package.json next to them so "installed" means the real thing, not a stray file.
using System.Diagnostics;
using System.Text.Json;

namespace RoutRunner.Core;

public sealed class ToolInfo
{
    public required string Id { get; init; }          // "9router"
    public required string Display { get; init; }     // "9Router"
    public string? ShimPath { get; set; }             // ...\npm\9router.cmd
    public string? PackageDir { get; set; }
    public string? Version { get; set; }
    public string? NodePath { get; set; }
    public string? NodeVersion { get; set; }
    public string? Problem { get; set; }

    public bool HasShim => !string.IsNullOrEmpty(ShimPath);
    public bool HasNode => !string.IsNullOrEmpty(NodePath);
    public bool Installed => HasShim && HasNode;

    public string Summary
    {
        get
        {
            if (Installed)
                return $"v{Version ?? "?"}  ·  {ShimPath}";
            if (!HasNode && !HasShim) return "node.js and the package are both missing";
            if (!HasNode) return "node.js not found on PATH";
            return !HasShim ? "npm shim not found on PATH" : Problem ?? "incomplete install";
        }
    }
}

public static class ToolLocator
{
    private static readonly string[] ShimExtensions = { ".cmd", ".exe", ".bat", ".ps1" };

    public static string? NpmPrefix
    {
        get
        {
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(roaming)) return null;
            var dir = Path.Combine(roaming, "npm");
            return Directory.Exists(dir) ? dir : null;
        }
    }

    public static IEnumerable<string> SearchDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var npm = NpmPrefix;
        if (npm is not null && seen.Add(npm)) yield return npm;

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var raw in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dir = raw.Trim('"');
            if (dir.Length == 0) continue;
            if (seen.Add(dir)) yield return dir;
        }
    }

    public static string? FindOnPath(string fileName)
    {
        foreach (var dir in SearchDirectories())
        {
            try
            {
                var candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // malformed PATH entry
            }
        }
        return null;
    }

    private static string? FindShim(string id)
    {
        foreach (var ext in ShimExtensions)
        {
            var hit = FindOnPath(id + ext);
            if (hit is not null) return hit;
        }
        return null;
    }

    // One node probe per process lifetime — the answer cannot change while we run,
    // and every re-resolve must keep it (state refreshes resolve tools constantly).
    private static readonly Lazy<string?> NodeVersionProbe = new(() =>
    {
        var node = FindOnPath("node.exe");
        return node is null ? null : ProbeNodeVersion(node);
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    public static ToolInfo Resolve(string id, string display)
    {
        var info = new ToolInfo { Id = id, Display = display };

        info.ShimPath = FindShim(id);
        info.NodePath = FindOnPath("node.exe");
        if (info.HasNode) info.NodeVersion = NodeVersionProbe.Value;

        var prefix = NpmPrefix;
        if (prefix is not null)
        {
            var pkgDir = Path.Combine(prefix, "node_modules", id);
            if (Directory.Exists(pkgDir))
            {
                info.PackageDir = pkgDir;
                var manifest = Path.Combine(pkgDir, "package.json");
                if (File.Exists(manifest))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                        if (doc.RootElement.TryGetProperty("version", out var v))
                            info.Version = v.GetString();
                    }
                    catch (Exception ex)
                    {
                        info.Problem = "package.json unreadable: " + ex.GetType().Name;
                    }
                }
            }
            else if (info.HasShim)
            {
                info.Problem = "shim exists but " + pkgDir + " is missing";
            }
        }

        return info;
    }

    private static string? ProbeNodeVersion(string nodePath)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = nodePath,
                Arguments = "-v",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (proc is null) return null;
            var text = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(4000);
            return string.IsNullOrWhiteSpace(text) ? null : text.TrimStart('v');
        }
        catch
        {
            return null;
        }
    }

    public static string? FindNpm() => FindOnPath("npm.cmd") ?? FindOnPath("npm.exe");
}
