// language: C#, file: src/Ui/MainForm.cs, target: Windows 10/11, .NET 8
// Control surface. Same three verbs as the tray — start, stop, open — plus live log.
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using RoutRunner.Core;

namespace RoutRunner.Ui;

public sealed class MainForm : Form
{
    private const int MaxDisplayLines = 2400;
    private const int TrimLines = 400;

    private readonly TrayContext _ctx;
    private readonly Label _title;
    private readonly Label _subtitle;
    private readonly Panel _bannerPanel;
    private readonly Label _bannerLabel;
    private readonly Button _installButton;
    private readonly ServiceCard _cardNine;
    private readonly ServiceCard _cardOmni;
    private readonly RichTextBox _log;
    private readonly NumericUpDown _portNine;
    private readonly NumericUpDown _portOmni;
    private readonly CheckBox _chkRestart;
    private readonly CheckBox _chkStartServices;
    private readonly CheckBox _chkStartMinimized;
    private readonly ConcurrentQueue<(string Id, string Line)> _pending = new();
    private readonly System.Windows.Forms.Timer _flush;
    private bool _suppressEvents;
    private bool _forceClose;
    private bool _balloonShown;
    private bool _toolsProbed;
    private int _displayedLines;

    public MainForm(TrayContext ctx)
    {
        _ctx = ctx;

        SuspendLayout();

        Text = "RoutRunner";
        ClientSize = new Size(920, 660);
        MinimumSize = new Size(800, 560);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Palette.Window;
        ForeColor = Palette.Text;
        Font = new Font("Segoe UI", 9f);
        Icon = _ctx.AppIcon;
        DoubleBuffered = true;

        // ---- header -------------------------------------------------------
        _title = new Label
        {
            Text = "ROUTRUNNER",
            Font = new Font("Segoe UI Semibold", 16f, FontStyle.Bold),
            ForeColor = Palette.Text,
            AutoSize = true,
            BackColor = Color.Transparent
        };

        _subtitle = new Label
        {
            Text = "9Router · OmniRoute · tray supervisor",
            Font = new Font("Segoe UI", 9f),
            ForeColor = Palette.Muted,
            AutoSize = true,
            BackColor = Color.Transparent
        };
        var headerActions = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            Dock = DockStyle.Right,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 6, 0, 0)
        };

        var btnHide = SmallButton("hide to tray");
        btnHide.Click += (_, _) => Hide();
        var btnRecheck = SmallButton("recheck");
        btnRecheck.Click += (_, _) =>
        {
            _ctx.RefreshTools();
            QueueLine("sys", "[preflight] re-resolved 9router and omniroute on PATH");
            RefreshUi();
        };
        headerActions.Controls.Add(btnHide);
        headerActions.Controls.Add(btnRecheck);

        var header = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        header.Controls.Add(headerActions);
        header.Controls.Add(_title);
        header.Controls.Add(_subtitle);
        header.Resize += (_, _) =>
        {
            _title.Location = new Point(0, 0);
            _subtitle.Location = new Point(2, 32);
        };

        // ---- preflight banner --------------------------------------------
        _bannerPanel = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Surface, Padding = new Padding(12, 0, 10, 0) };
        _bannerPanel.Paint += (_, e) => ControlPaint.DrawBorder(e.Graphics, _bannerPanel.ClientRectangle,
            Palette.Border, ButtonBorderStyle.Solid);

        _bannerLabel = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            ForeColor = Palette.Muted,
            Font = new Font("Segoe UI", 9f)
        };

        _installButton = SmallButton("install", Palette.Accent, Color.FromArgb(8, 14, 12));
        _installButton.Dock = DockStyle.Right;
        _installButton.Width = 96;
        _installButton.Visible = false;
        _installButton.Click += OnInstallClicked;

        _bannerPanel.Controls.Add(_bannerLabel);
        _bannerPanel.Controls.Add(_installButton);

        // ---- cards --------------------------------------------------------
        _cardNine = new ServiceCard(_ctx.Nine,
            () => _ctx.Nine.Start(),
            () => _ctx.StopService(_ctx.Nine),
            () => _ctx.OpenEndpoint(_ctx.Nine),
            () => _ctx.StopService(_ctx.Nine))
        { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 7, 0) };

        _cardOmni = new ServiceCard(_ctx.Omni,
            () => _ctx.Omni.Start(),
            () => _ctx.StopService(_ctx.Omni),
            () => _ctx.OpenEndpoint(_ctx.Omni),
            () => _ctx.StopService(_ctx.Omni))
        { Dock = DockStyle.Fill, Margin = new Padding(7, 0, 0, 0) };

        var cards = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 8)
        };
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        cards.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        cards.Controls.Add(_cardNine, 0, 0);
        cards.Controls.Add(_cardOmni, 1, 0);

        // ---- options ------------------------------------------------------
        _portNine = MakePortBox(_ctx.Settings.NineRouterPort);
        _portOmni = MakePortBox(_ctx.Settings.OmniRoutePort);
        _portNine.ValueChanged += (_, _) => PushPorts();
        _portOmni.ValueChanged += (_, _) => PushPorts();

        _chkRestart = MakeCheck("auto-restart", _ctx.Settings.AutoRestart);
        _chkStartServices = MakeCheck("start on launch", _ctx.Settings.AutoStartServices);
        _chkStartMinimized = MakeCheck("start minimized", _ctx.Settings.StartMinimized);

        _chkRestart.CheckedChanged += (_, _) =>
        {
            if (_suppressEvents) return;
            _ctx.ApplyAutoRestart(_chkRestart.Checked);
        };
        _chkStartServices.CheckedChanged += (_, _) =>
        {
            if (_suppressEvents) return;
            _ctx.ApplyAutoStartServices(_chkStartServices.Checked);
        };
        _chkStartMinimized.CheckedChanged += (_, _) =>
        {
            if (_suppressEvents) return;
            _ctx.Settings.StartMinimized = _chkStartMinimized.Checked;
            _ctx.Settings.Save();
        };

        var options = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
            Padding = new Padding(0, 4, 0, 0)
        };
        options.Controls.Add(FieldLabel("9Router port"));
        options.Controls.Add(_portNine);
        options.Controls.Add(FieldLabel("OmniRoute port", 10));
        options.Controls.Add(_portOmni);
        options.Controls.Add(_chkRestart);
        options.Controls.Add(_chkStartServices);
        options.Controls.Add(_chkStartMinimized);

        var tips = new ToolTip();
        tips.SetToolTip(_portNine, "Applied the next time the service starts");
        tips.SetToolTip(_portOmni, "Applied the next time the service starts");

        // ---- log ----------------------------------------------------------
        _log = new RichTextBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor = Palette.SurfaceDeep,
            ForeColor = Palette.Text,
            Font = new Font("Consolas", 9f),
            ReadOnly = true,
            WordWrap = false,
            DetectUrls = false,
            ScrollBars = RichTextBoxScrollBars.Both,
            HideSelection = false,
            ShortcutsEnabled = true,
            Margin = new Padding(0)
        };

        var logHeader = new Panel { Dock = DockStyle.Top, Height = 32, BackColor = Palette.Surface };
        var logTitle = new Label
        {
            Text = "LIVE LOG",
            Dock = DockStyle.Left,
            Width = 110,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Palette.Muted,
            Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
            Padding = new Padding(12, 0, 0, 0)
        };

        var logActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 210,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Palette.Surface,
            Padding = new Padding(0, 4, 8, 0)
        };
        var btnClear = SmallButton("clear");
        btnClear.Width = 68;
        btnClear.Click += (_, _) =>
        {
            _log.Clear();
            _displayedLines = 0;
        };
        var btnFolder = SmallButton("log folder");
        btnFolder.Width = 92;
        btnFolder.Click += (_, _) => OpenLogFolder();
        logActions.Controls.Add(btnClear);
        logActions.Controls.Add(btnFolder);

        logHeader.Controls.Add(logTitle);
        logHeader.Controls.Add(logActions);
        logHeader.Paint += (_, e) => ControlPaint.DrawBorder(e.Graphics, logHeader.ClientRectangle,
            Palette.Border, ButtonBorderStyle.Solid);

        var logBody = new Panel { Dock = DockStyle.Fill, BackColor = Palette.SurfaceDeep, Padding = new Padding(10, 8, 6, 8) };
        logBody.Controls.Add(_log);
        logBody.Paint += (_, e) => ControlPaint.DrawBorder(e.Graphics, logBody.ClientRectangle,
            Palette.Border, ButtonBorderStyle.Solid);

        var logSection = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Window, Margin = new Padding(0) };
        logSection.Controls.Add(logBody);
        logSection.Controls.Add(logHeader);

        // ---- root ---------------------------------------------------------
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = Palette.Window,
            Padding = new Padding(18, 14, 18, 14)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 162f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(_bannerPanel, 0, 1);
        root.Controls.Add(cards, 0, 2);
        root.Controls.Add(options, 0, 3);
        root.Controls.Add(logSection, 0, 4);

        Controls.Add(root);

        _ctx.UiRefresh += RefreshUi;

        _flush = new System.Windows.Forms.Timer { Interval = 120 };
        _flush.Tick += (_, _) => FlushLog();
        _flush.Start();

        ResumeLayout(false);

        RefreshUi();
    }

    // ------------------------------------------------------------------ ui

    private static Label FieldLabel(string text, int leftGap = 0) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Palette.Muted,
        Font = new Font("Segoe UI", 8.75f),
        Margin = new Padding(leftGap, 6, 6, 0)
    };

    private static NumericUpDown MakePortBox(int value) => new()
    {
        Minimum = 1024,
        Maximum = 65535,
        Value = value,
        Width = 74,
        Margin = new Padding(0, 2, 0, 0),
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = Palette.Surface,
        ForeColor = Palette.Text,
        Font = new Font("Consolas", 9.5f),
        ThousandsSeparator = false,
        TextAlign = HorizontalAlignment.Center
    };

    private static CheckBox MakeCheck(string text, bool value) => new()
    {
        Text = text,
        Checked = value,
        AutoSize = true,
        FlatStyle = FlatStyle.Flat,
        ForeColor = Palette.Text,
        Font = new Font("Segoe UI", 9f),
        Margin = new Padding(18, 5, 0, 0),
        Cursor = Cursors.Hand
    };

    private static Button SmallButton(string text, Color? back = null, Color? fore = null)
    {
        var bg = back ?? Palette.Surface;
        var button = new Button
        {
            Text = text,
            Height = 26,
            Width = 92,
            Margin = new Padding(6, 0, 0, 0),
            FlatStyle = FlatStyle.Flat,
            BackColor = bg,
            ForeColor = fore ?? Palette.Muted,
            UseVisualStyleBackColor = false,
            Font = new Font("Segoe UI", 8.5f),
            Cursor = Cursors.Hand,
            TabStop = false
        };
        button.FlatAppearance.BorderColor = Palette.Border;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = ControlPaint.Light(bg, 0.2f);
        return button;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        EnableDarkTitleBar();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_toolsProbed) return;
        _toolsProbed = true;
        _ctx.RefreshTools();
        QueueLine("sys", "[preflight] " + PreflightLine());
        QueueLine("sys", Elevation.IsElevated
            ? "[preflight] elevated — 9Router and OmniRoute will inherit the administrator token"
            : "[preflight] NOT elevated — services will inherit a standard user token (relaunch as administrator)");
        RefreshUi();
    }

    private void EnableDarkTitleBar()
    {
        try
        {
            var on = 1;
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Win10 1909+), 19 = pre-1909
            if (DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(Handle, 19, ref on, sizeof(int));
        }
        catch
        {
            // older shells keep the light title bar; cosmetic only
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // -------------------------------------------------------------- state

    public void RefreshUi()
    {
        if (IsDisposed || !IsHandleCreated) return;

        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(RefreshUi)); } catch { }
            return;
        }

        _cardNine.UpdateView(_ctx.Nine);
        _cardOmni.UpdateView(_ctx.Omni);

        var nine = _ctx.Nine.Tool;
        var omni = _ctx.Omni.Tool;
        var ready = nine.Installed && omni.Installed;

        if (ready)
        {
            _bannerLabel.ForeColor = Palette.Accent;
            _bannerLabel.Text = "preflight ok  ·  " + PreflightLine();
            _installButton.Visible = false;
        }
        else
        {
            var missing = new List<string>();
            if (!nine.Installed) missing.Add("9Router (" + nine.Summary + ")");
            if (!omni.Installed) missing.Add("OmniRoute (" + omni.Summary + ")");
            _bannerLabel.ForeColor = Palette.Danger;
            _bannerLabel.Text = "not installed  ·  " + string.Join("  |  ", missing);
            _installButton.Visible = true;
        }

        _suppressEvents = true;
        try
        {
            if (!_portNine.Focused && _portNine.Value != _ctx.Nine.Port) _portNine.Value = _ctx.Nine.Port;
            if (!_portOmni.Focused && _portOmni.Value != _ctx.Omni.Port) _portOmni.Value = _ctx.Omni.Port;
            _chkRestart.Checked = _ctx.Settings.AutoRestart;
            _chkStartServices.Checked = _ctx.Settings.AutoStartServices;
            _chkStartMinimized.Checked = _ctx.Settings.StartMinimized;
        }
        finally
        {
            _suppressEvents = false;
        }

        var elevated = Elevation.IsElevated;
        _subtitle.Text = "9Router · OmniRoute · tray supervisor  ·  " + (elevated ? "administrator" : "not elevated");
        _subtitle.ForeColor = elevated ? Palette.Muted : Palette.Danger;

        Text = $"RoutRunner  —  9Router {Palette.Label(_ctx.Nine.State)}  ·  OmniRoute {Palette.Label(_ctx.Omni.State)}";
    }

    private string PreflightLine()
    {
        var nine = _ctx.Nine.Tool;
        var omni = _ctx.Omni.Tool;
        var node = nine.NodeVersion ?? omni.NodeVersion ?? "?";
        return $"9Router v{nine.Version ?? "?"} · OmniRoute v{omni.Version ?? "?"} · node v{node}";
    }

    private void PushPorts()
    {
        if (_suppressEvents) return;
        _ctx.ApplyPorts((int)_portNine.Value, (int)_portOmni.Value);
    }

    // ---------------------------------------------------------------- log

    public void QueueLine(string id, string line) => _pending.Enqueue((id, line));

    private void FlushLog()
    {
        if (_pending.IsEmpty || IsDisposed) return;

        var budget = 500;
        while (budget-- > 0 && _pending.TryDequeue(out var item))
        {
            var tag = item.Id switch
            {
                "9router" => "9R",
                "omniroute" => "OR",
                _ => "··"
            };

            _log.SelectionStart = _log.TextLength;
            _log.SelectionLength = 0;
            _log.SelectionColor = item.Id switch
            {
                "9router" => Palette.Nine,
                "omniroute" => Palette.Omni,
                _ => Palette.Muted
            };
            _log.AppendText($"{tag}  {item.Line}{Environment.NewLine}");
            _displayedLines++;
        }

        if (_displayedLines > MaxDisplayLines)
        {
            var cut = _log.GetFirstCharIndexFromLine(TrimLines);
            if (cut > 0)
            {
                _log.Select(0, cut);
                _log.SelectedText = string.Empty;
                _displayedLines -= TrimLines;
            }
        }

        _log.SelectionStart = _log.TextLength;
        _log.SelectionLength = 0;
        _log.ScrollToCaret();
    }

    private async void OnInstallClicked(object? sender, EventArgs e)
    {
        _installButton.Enabled = false;
        QueueLine("sys", "[install] resolving npm global install…");
        try
        {
            await _ctx.InstallMissingAsync(line => QueueLine("sys", line));
        }
        catch (Exception ex)
        {
            QueueLine("sys", $"[install] {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _installButton.Enabled = true;
            RefreshUi();
        }
    }

    private void OpenLogFolder()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RoutRunner", "logs");
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch
        {
            QueueLine("sys", "[ui] could not open the log folder");
        }
    }

    // ------------------------------------------------------------ lifetime

    public void CloseForExit()
    {
        _forceClose = true;
        _flush.Stop();
        _ctx.UiRefresh -= RefreshUi;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_forceClose && !_ctx.Exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            ShowInTaskbar = false;
            if (!_balloonShown)
            {
                _balloonShown = true;
                QueueLine("sys", "[ui] still running in the tray — right-click the icon to stop or exit");
            }
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _flush?.Dispose();
        base.Dispose(disposing);
    }
}
