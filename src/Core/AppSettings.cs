// language: C#, file: src/Core/AppSettings.cs, target: Windows 10/11, .NET 8
// %APPDATA%\RoutRunner\settings.json — ports, args, autostart flags.
using System.Text.Json;

namespace RoutRunner.Core;

public sealed class AppSettings
{
    public int NineRouterPort { get; set; } = 20128;
    public int OmniRoutePort { get; set; } = 20129;
    public string NineRouterArgs { get; set; } = "--no-browser";
    public string OmniRouteArgs { get; set; } = "--no-open";
    public bool AutoRestart { get; set; } = true;
    public bool AutoStartServices { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool MinimizeOnClose { get; set; } = true;

    public static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RoutRunner", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                var parsed = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(StorePath), JsonOptions);
                if (parsed is not null) return parsed.Normalize();
            }
        }
        catch
        {
            // corrupted config falls back to defaults instead of blocking startup
        }
        return new AppSettings();
    }

    public AppSettings Normalize()
    {
        NineRouterPort = Clamp(NineRouterPort, 20128);
        OmniRoutePort = Clamp(OmniRoutePort, 20129);
        NineRouterArgs = (NineRouterArgs ?? "").Trim();
        OmniRouteArgs = (OmniRouteArgs ?? "").Trim();
        return this;
    }

    private static int Clamp(int port, int fallback) =>
        port is >= 1024 and <= 65535 ? port : fallback;

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(StorePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // config persistence is best-effort
        }
    }

    public static bool IsAutoStartEnabled() => StartupTask.Exists();

    /// <summary>
    /// Autostart is a Task Scheduler entry with RunLevel=HighestAvailable — a registry Run key
    /// would pop UAC at every logon. Returns false when the task cannot be written (needs admin).
    /// </summary>
    public bool SetAutoStart(bool enabled, out string message)
    {
        return enabled ? StartupTask.Create(out message) : StartupTask.Delete(out message);
    }
}
