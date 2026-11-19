// language: C#, file: src/Ui/IconFactory.cs, target: Windows 10/11, .NET 8
// Icons are drawn at runtime — no binary assets, one glyph per state, legible at 16px.
using System.Drawing.Drawing2D;
using RoutRunner.Native;

namespace RoutRunner.Ui;

internal static class IconFactory
{
    public static readonly Color Accent = Color.FromArgb(52, 211, 153);
    public static readonly Color Warn = Color.FromArgb(251, 191, 36);
    public static readonly Color Danger = Color.FromArgb(248, 113, 113);
    public static readonly Color Idle = Color.FromArgb(100, 116, 139);

    public static Icon Build(Color accent)
    {
        const int size = 64;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using (var back = new SolidBrush(Color.FromArgb(15, 18, 23)))
                g.FillEllipse(back, 2, 2, size - 4, size - 4);

            using (var ring = new Pen(accent, 3f))
                g.DrawEllipse(ring, 6, 6, size - 12, size - 12);

            // route glyph: two nodes joined by a rising line
            using var line = new Pen(accent, 5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(line, 21, 45, 43, 21);

            using var node = new SolidBrush(accent);
            g.FillEllipse(node, 15, 39, 13, 13);
            g.FillEllipse(node, 37, 15, 13, 13);
        }

        var handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            Interop.DestroyIcon(handle);
        }
    }
}
