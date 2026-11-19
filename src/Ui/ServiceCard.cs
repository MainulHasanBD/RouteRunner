// language: C#, file: src/Ui/ServiceCard.cs, target: Windows 10/11, .NET 8
// One service, one card: dot, name, port, state line, install path, actions.
using RoutRunner.Core;

namespace RoutRunner.Ui;

internal sealed class ServiceCard : Panel
{
    private readonly Label _dot;
    private readonly Label _title;
    private readonly Label _port;
    private readonly Label _status;
    private readonly Label _detail;
    private readonly FlowLayoutPanel _actions;

    public Button StartButton { get; }
    public Button StopButton { get; }
    public Button OpenButton { get; }
    public Button KillButton { get; }

    public ServiceCard(ServiceRunner service, Action onStart, Action onStop, Action onOpen, Action onKill)
    {
        DoubleBuffered = true;
        BackColor = Palette.Surface;
        Margin = new Padding(0, 0, 0, 0);
        Padding = new Padding(0);

        _dot = new Label
        {
            Text = "●",
            Font = new Font("Segoe UI", 13f, FontStyle.Regular),
            ForeColor = Palette.ForState(service.State),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.Transparent
        };

        _title = new Label
        {
            Text = service.DisplayName,            Font = new Font("Segoe UI Semibold", 11.5f, FontStyle.Bold),
            ForeColor = Palette.Text,
            AutoSize = false,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.Transparent
        };

        _port = new Label
        {
            Text = "127.0.0.1:" + service.Port,
            Font = new Font("Consolas", 9f),
            ForeColor = Palette.Muted,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleRight,
            BackColor = Color.Transparent
        };

        _status = new Label
        {
            Font = new Font("Segoe UI", 9f),
            ForeColor = Palette.Text,
            AutoSize = false,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.Transparent
        };

        _detail = new Label
        {
            Font = new Font("Consolas", 8f),
            ForeColor = Palette.Muted,
            AutoSize = false,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.Transparent
        };

        StartButton = MakeButton("Start", Palette.Accent, Color.FromArgb(8, 14, 12));
        StopButton = MakeButton("Stop", Palette.Surface, Palette.Text);
        OpenButton = MakeButton("Open", Palette.Surface, Palette.Text);
        KillButton = MakeButton("Kill", Color.FromArgb(70, 24, 26), Palette.Danger);
        KillButton.Visible = false;

        StartButton.Click += (_, _) => onStart();
        StopButton.Click += (_, _) => onStop();
        OpenButton.Click += (_, _) => onOpen();
        KillButton.Click += (_, _) => onKill();

        _actions = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        _actions.Controls.Add(StartButton);
        _actions.Controls.Add(StopButton);
        _actions.Controls.Add(OpenButton);
        _actions.Controls.Add(KillButton);

        Controls.Add(_dot);
        Controls.Add(_title);
        Controls.Add(_port);
        Controls.Add(_status);
        Controls.Add(_detail);
        Controls.Add(_actions);

        Paint += (_, e) => ControlPaint.DrawBorder(e.Graphics, ClientRectangle,
            Palette.Border, ButtonBorderStyle.Solid);

        UpdateView(service);
    }

    private static Button MakeButton(string text, Color back, Color fore)
    {
        var button = new Button
        {
            Text = text,
            Width = 76,
            Height = 30,
            Margin = new Padding(0, 0, 8, 0),
            FlatStyle = FlatStyle.Flat,
            BackColor = back,
            ForeColor = fore,
            UseVisualStyleBackColor = false,
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
            Cursor = Cursors.Hand,
            TabStop = false
        };
        button.FlatAppearance.BorderColor = Palette.Border;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = ControlPaint.Light(back, 0.25f);
        button.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(back, 0.1f);
        return button;
    }

    public void UpdateView(ServiceRunner service)
    {        _dot.ForeColor = Palette.ForState(service.State);
        _port.Text = "127.0.0.1:" + service.Port;

        var state = Palette.Label(service.State);
        _status.ForeColor = service.State == ServiceState.Failed ? Palette.Danger : Palette.Text;
        _status.Text = state + "  ·  " + service.Detail;

        _detail.Text = service.Tool.Installed
            ? $"v{service.Tool.Version ?? "?"}  ·  {service.Tool.ShimPath}{ElevationTag(service)}"
            : service.Tool.Summary;
        _detail.ForeColor = service.Tool.Installed ? Palette.Muted : Palette.Warn;

        var busy = service.State is ServiceState.Starting or ServiceState.Stopping;
        StartButton.Enabled = !busy && service.State is ServiceState.Stopped or ServiceState.Failed;
        StopButton.Enabled = service.Owned || service.State == ServiceState.External;
        OpenButton.Enabled = service.State is ServiceState.Running or ServiceState.External;
        KillButton.Visible = service.State == ServiceState.External;

        // Accent only when the action is actually available — a lit green button you cannot press lies.
        StartButton.BackColor = StartButton.Enabled ? Palette.Accent : Palette.Surface;
        StartButton.ForeColor = StartButton.Enabled ? Color.FromArgb(8, 14, 12) : Palette.Muted;
        StopButton.ForeColor = StopButton.Enabled ? Palette.Text : Palette.Muted;
        OpenButton.ForeColor = OpenButton.Enabled ? Palette.Text : Palette.Muted;
    }

    private static string ElevationTag(ServiceRunner service) => service.ChildElevated switch
    {
        true => "  ·  admin",
        false => "  ·  user token",
        _ => string.Empty
    };

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        const int pad = 16;

        _dot.SetBounds(pad - 4, pad - 2, 20, 22);
        _title.SetBounds(pad + 18, pad - 2, Math.Max(60, Width - pad * 2 - 160), 24);
        _port.SetBounds(Width - pad - 130, pad, 130, 20);
        _status.SetBounds(pad, pad + 26, Width - pad * 2, 20);
        _detail.SetBounds(pad, pad + 46, Width - pad * 2, 18);
        _actions.SetBounds(pad - 4, Height - 46, Width - pad * 2, 32);
    }
}
