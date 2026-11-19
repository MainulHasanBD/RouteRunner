// language: C#, file: src/Ui/Palette.cs, target: Windows 10/11, .NET 8
using RoutRunner.Core;

namespace RoutRunner.Ui;

internal static class Palette
{
    public static readonly Color Window = Color.FromArgb(15, 17, 21);
    public static readonly Color Surface = Color.FromArgb(20, 25, 32);
    public static readonly Color SurfaceDeep = Color.FromArgb(11, 13, 17);
    public static readonly Color Border = Color.FromArgb(33, 41, 52);
    public static readonly Color Text = Color.FromArgb(229, 233, 240);
    public static readonly Color Muted = Color.FromArgb(139, 150, 166);
    public static readonly Color Accent = Color.FromArgb(52, 211, 153);
    public static readonly Color Warn = Color.FromArgb(251, 191, 36);
    public static readonly Color Danger = Color.FromArgb(248, 113, 113);
    public static readonly Color Nine = Color.FromArgb(125, 211, 252);
    public static readonly Color Omni = Color.FromArgb(196, 181, 253);

    public static Color ForState(ServiceState state) => state switch
    {
        ServiceState.Running => Accent,
        ServiceState.Starting => Warn,
        ServiceState.Stopping => Warn,
        ServiceState.Failed => Danger,
        ServiceState.External => Nine,
        _ => Color.FromArgb(100, 116, 139)
    };

    public static Color ForService(string id) => id == "omniroute" ? Omni : Nine;

    public static string Label(ServiceState state) => state switch
    {
        ServiceState.Running => "running",
        ServiceState.Starting => "starting",
        ServiceState.Stopping => "stopping",
        ServiceState.Failed => "failed",
        ServiceState.External => "running (external)",
        _ => "stopped"
    };
}
