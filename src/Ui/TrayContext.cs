// language: C#, file: src/Ui/TrayContext.cs, target: Windows 10/11, .NET 8
// The supervisor shell: tray icon, menu, health tick, window lifetime.
// Everything the tray can do the window can do — same methods, no second code path.
using System.Collections.Generic;
using RoutRunner.Core;
using RoutRunner.Native;

namespace RoutRunner.Ui;

public sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _miNine;
    private readonly ToolStripMenuItem _miOmni;
    private readonly ToolStripMenuItem _miAutoRestart;
    private readonly ToolStripMenuItem _miAutoStartLogin;
    private readonly ToolStripMenuItem _miAutoStartServices;
    private readonly System.Windows.Forms.Timer _health;
    private readonly TaskbarWatcher _watcher;
    private readonly Control _marshaler = new() { Visible = false };
    private readonly Icon _iconIdle;
    private readonly Icon _iconRun;
    private readonly Icon _iconWarn;
    private readonly Icon _iconErr;
    private MainForm? _form;
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _showWait;
    private bool _bootstrapped;
    private bool _syncingMenu;

    public AppSettings Settings { get; }
    public ServiceRunner Nine { get; }
    public ServiceRunner Omni { get; }
    public bool Exiting { get; private set; }
    public Icon AppIcon => _iconRun;

    public event Action? UiRefresh;

    public TrayContext(AppSettings settings)
    {
        Settings = settings;

        Nine = new ServiceRunner("9router", "9Router", settings.NineRouterPort)
        {
            ExtraArgs = settings.NineRouterArgs,
            AutoRestart = settings.AutoRestart
        };
        Omni = new ServiceRunner("omniroute", "OmniRoute", settings.OmniRoutePort)
        {
            ExtraArgs = settings.OmniRouteArgs,
            AutoRestart = settings.AutoRestart
        };

        Nine.Changed += _ => OnServiceChanged();
        Omni.Changed += _ => OnServiceChanged();
        Nine.LineWritten += (svc, line) => _form?.QueueLine(svc.Id, line);
        Omni.LineWritten += (svc, line) => _form?.QueueLine(svc.Id, line);

        ListenForShowRequests();

        _iconIdle = IconFactory.Build(Palette.ForState(ServiceState.Stopped));
        _iconRun = IconFactory.Build(Palette.Accent);
        _iconWarn = IconFactory.Build(Palette.Warn);
        _iconErr = IconFactory.Build(Palette.Danger);

        _miNine = new ToolStripMenuItem("9Router") { Enabled = false };
        _miOmni = new ToolStripMenuItem("OmniRoute") { Enabled = false };

        _miAutoRestart = new ToolStripMenuItem("Auto-restart crashes")
        {
            CheckOnClick = true,
            Checked = settings.AutoRestart
        };
        _miAutoRestart.CheckedChanged += (_, _) => ApplyAutoRestart(_miAutoRestart.Checked);

        _miAutoStartLogin = new ToolStripMenuItem("Start with Windows (elevated task)")
        {
            CheckOnClick = true,
            Checked = AppSettings.IsAutoStartEnabled()
        };
        _miAutoStartLogin.CheckedChanged += (_, _) =>
        {
            if (_syncingMenu) return;
            ApplyAutoStartLogin(_miAutoStartLogin.Checked);
        };

        _miAutoStartServices = new ToolStripMenuItem("Start services on launch")
        {
            CheckOnClick = true,
            Checked = settings.AutoStartServices
        };
        _miAutoStartServices.CheckedChanged += (_, _) =>
        {
            Settings.AutoStartServices = _miAutoStartServices.Checked;
            Settings.Save();
        };

        _menu = BuildMenu();

        _tray = new NotifyIcon
        {
            Icon = _iconIdle,
            Text = "RoutRunner",
            Visible = true,
            ContextMenuStrip = _menu
        };
        _tray.DoubleClick += (_, _) => ShowWindow();
        _tray.BalloonTipClicked += (_, _) => ShowWindow();

        _watcher = new TaskbarWatcher(() =>
        {
            // Explorer restarted — NotifyIcon forgot its icon, re-add it.
            _tray.Visible = false;
            _tray.Visible = true;
        });

        _health = new System.Windows.Forms.Timer { Interval = 4000 };
        _health.Tick += (_, _) => HealthTick();
        _health.Start();

        _ = _marshaler.Handle; // invisible anchor so worker-thread events can marshal onto the UI thread

        UpdateTraySurface();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            Font = new Font("Segoe UI", 9f)
        };

        var header = new ToolStripMenuItem("ROUTRUNNER") { Enabled = false, Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold) };

        var services = new ToolStripMenuItem("Services");
        services.DropDownItems.Add(MenuItem("Start 9Router", () => Nine.Start()));
        services.DropDownItems.Add(MenuItem("Stop 9Router", () => StopService(Nine)));
        services.DropDownItems.Add(new ToolStripSeparator());
        services.DropDownItems.Add(MenuItem("Start OmniRoute", () => Omni.Start()));
        services.DropDownItems.Add(MenuItem("Stop OmniRoute", () => StopService(Omni)));

        var dashboards = new ToolStripMenuItem("Dashboards");
        dashboards.DropDownItems.Add(MenuItem("9Router  :" + Nine.Port, () => OpenEndpoint(Nine)));
        dashboards.DropDownItems.Add(MenuItem("OmniRoute  :" + Omni.Port, () => OpenEndpoint(Omni)));

        menu.Items.Add(header);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_miNine);
        menu.Items.Add(_miOmni);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(MenuItem("Start all", StartAll, bold: true));
        menu.Items.Add(MenuItem("Stop all", StopAll));
        menu.Items.Add(services);
        menu.Items.Add(dashboards);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_miAutoStartServices);
        menu.Items.Add(_miAutoRestart);
        menu.Items.Add(_miAutoStartLogin);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(MenuItem("Show window", ShowWindow, bold: true));
        menu.Items.Add(MenuItem("Exit", ExitApp));

        return menu;
    }

    private static ToolStripMenuItem MenuItem(string text, Action action, bool bold = false)
    {
        var item = new ToolStripMenuItem(text) { Font = new Font("Segoe UI", 9f, bold ? FontStyle.Bold : FontStyle.Regular) };
        item.Click += (_, _) => action();
        return item;
    }

    public void Bootstrap()
    {
        if (_bootstrapped) return;
        _bootstrapped = true;

        if (!Settings.StartMinimized) ShowWindow();

        if (Settings.AutoStartServices)
        {
            // let the window paint its first frame before PowerShell spins up
            var start = new System.Windows.Forms.Timer { Interval = 350 };
            start.Tick += (_, _) =>
            {
                start.Stop();
                start.Dispose();
                StartAll();
            };
            start.Start();
        }
    }

    /// <summary>
    /// A second launch of the exe signals this event instead of complaining: surface the window.
    /// Wait thread never touches UI directly — it hops through the marshaler control.
    /// </summary>
    private void ListenForShowRequests()
    {
        try
        {
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Instance.ShowEventName);
            _showWait = ThreadPool.RegisterWaitForSingleObject(
                _showSignal,
                (_, _) =>
                {
                    if (_marshaler.IsHandleCreated)
                    {
                        try { _marshaler.BeginInvoke(new Action(ShowWindow)); } catch { }
                    }
                },
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }
        catch
        {
            // another instance owns the name, or the ACL refused it — the tray still works
        }
    }

    public void ShowWindow()
    {
        if (_form is null || _form.IsDisposed)
        {
            _form = new MainForm(this);
            _form.FormClosed += (_, _) => { };
        }

        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.ShowInTaskbar = true;
        _form.Activate();
        _form.BringToFront();
    }

    public void HideWindow()
    {
        if (_form is null || _form.IsDisposed) return;
        _form.Hide();
        _form.ShowInTaskbar = false;
    }

    public void StartAll()
    {
        Nine.Start();
        Omni.Start();
    }

    public void StopAll()
    {
        Nine.Stop();
        Omni.Stop();
    }

    public void StopService(ServiceRunner service)
    {
        if (service.State == ServiceState.External)
        {
            var answer = MessageBox.Show(
                $"Port {service.Port} is held by a {service.DisplayName} instance RoutRunner did not start.\n\n" +
                "Kill that process tree so RoutRunner can own the port?",
                "RoutRunner — external listener",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes) return;
            service.Stop(killExternal: true);
            return;
        }

        service.Stop();
    }

    public void OpenEndpoint(ServiceRunner service)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = service.Endpoint,
                UseShellExecute = true
            });
        }
        catch
        {
            // no default browser / handler — nothing to do
        }
    }

    public void ApplyAutoRestart(bool enabled)
    {
        Settings.AutoRestart = enabled;
        Nine.AutoRestart = enabled;
        Omni.AutoRestart = enabled;
        if (_miAutoRestart.Checked != enabled) _miAutoRestart.Checked = enabled;
        Settings.Save();
    }

    public void ApplyAutoStartLogin(bool enabled)
    {
        var ok = Settings.SetAutoStart(enabled, out var message);
        var actual = AppSettings.IsAutoStartEnabled();

        _syncingMenu = true;
        try
        {
            if (_miAutoStartLogin.Checked != actual) _miAutoStartLogin.Checked = actual;
        }
        finally
        {
            _syncingMenu = false;
        }

        _form?.QueueLine("sys", "[startup] " + message);

        if (!ok)
        {
            MessageBox.Show(
                "Could not write the startup task.\n\n" + message +
                "\n\nAutostart needs RoutRunner running as administrator, which is how it installs itself.",
                "RoutRunner — autostart",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    public void ApplyAutoStartServices(bool enabled)
    {
        Settings.AutoStartServices = enabled;
        if (_miAutoStartServices.Checked != enabled) _miAutoStartServices.Checked = enabled;
        Settings.Save();
    }

    public void ApplyPorts(int ninePort, int omniPort)
    {
        Settings.NineRouterPort = ninePort;
        Settings.OmniRoutePort = omniPort;
        Nine.Port = ninePort;
        Omni.Port = omniPort;
        Settings.Save();
        RefreshAll();
    }

    public void RefreshTools()
    {
        Nine.RefreshTool();
        Omni.RefreshTool();
    }

    public void RefreshAll()
    {
        UpdateTraySurface();
        UiRefresh?.Invoke();
    }

    public async Task InstallMissingAsync(Action<string> log)
    {
        var missing = new List<string>();
        if (!Nine.Tool.Installed) missing.Add("9router");
        if (!Omni.Tool.Installed) missing.Add("omniroute");

        if (missing.Count == 0)
        {
            log("[install] nothing missing — re-running a global update check");
            missing.AddRange(new[] { "9router", "omniroute" });
        }

        if (ToolLocator.FindNpm() is null)
        {
            log("[install] npm not found. Install Node.js (>= 22.22 for OmniRoute), then click recheck.");
            return;
        }

        foreach (var id in missing)
        {
            var ok = await Installer.InstallAsync(id, log, CancellationToken.None);
            log(ok ? $"[install] {id} ready" : $"[install] {id} did not complete");
        }

        RefreshTools();
    }

    private void OnServiceChanged()
    {
        // Service state changes arrive from worker threads; every tray mutation belongs on the UI thread.
        if (_marshaler.IsHandleCreated && _marshaler.InvokeRequired)
        {
            try { _marshaler.BeginInvoke(new Action(OnServiceChangedCore)); } catch { }
            return;
        }
        OnServiceChangedCore();
    }

    private void OnServiceChangedCore()
    {
        UpdateTraySurface();
        UiRefresh?.Invoke();
    }

    private void HealthTick()
    {
        Nine.SyncExternalState();
        Omni.SyncExternalState();
    }

    private void UpdateTraySurface()
    {
        Icon icon;
        if (Nine.State == ServiceState.Failed || Omni.State == ServiceState.Failed)
            icon = _iconErr;
        else if (Nine.State == ServiceState.Starting || Omni.State == ServiceState.Starting)
            icon = _iconWarn;
        else if (IsUp(Nine) && IsUp(Omni))
            icon = _iconRun;
        else if (IsUp(Nine) || IsUp(Omni))
            icon = _iconWarn;
        else
            icon = _iconIdle;

        if (!ReferenceEquals(_tray.Icon, icon)) _tray.Icon = icon;

        _miNine.Text = $"9Router   :{Nine.Port}   ·   {Palette.Label(Nine.State)}";
        _miOmni.Text = $"OmniRoute   :{Omni.Port}   ·   {Palette.Label(Omni.State)}";
        _miNine.ForeColor = Palette.ForState(Nine.State);
        _miOmni.ForeColor = Palette.ForState(Omni.State);

        var summary = $"RoutRunner · 9R {Palette.Label(Nine.State)} · OR {Palette.Label(Omni.State)}";
        _tray.Text = summary.Length > 63 ? summary[..63] : summary;

        _form?.RefreshUi();
    }

    private static bool IsUp(ServiceRunner service) =>
        service.State is ServiceState.Running or ServiceState.External;

    public void ExitApp()
    {
        if (Exiting) return;
        Exiting = true;

        _health.Stop();
        _tray.Visible = false;

        try
        {
            _form?.CloseForExit();
        }
        catch
        {
            // window may already be gone
        }

        Nine.Dispose();
        Omni.Dispose();

        try { _showWait?.Unregister(null); } catch { }
        _showSignal?.Dispose();

        _menu.Dispose();
        _tray.Dispose();
        _watcher.Destroy();

        _iconIdle.Dispose();
        _iconRun.Dispose();
        _iconWarn.Dispose();
        _iconErr.Dispose();

        ExitThread();
    }
}

/// <summary>Re-registers the tray icon when Explorer restarts and broadcasts TaskbarCreated.</summary>
internal sealed class TaskbarWatcher : NativeWindow
{
    private readonly uint _message;
    private readonly Action _onTaskbarCreated;

    public TaskbarWatcher(Action onTaskbarCreated)
    {
        _onTaskbarCreated = onTaskbarCreated;
        CreateHandle(new CreateParams { Caption = "RoutRunner.TaskbarWatcher" });
        _message = Interop.RegisterWindowMessageW("TaskbarCreated");
    }

    protected override void WndProc(ref Message m)
    {
        if (_message != 0 && m.Msg == _message)
        {
            try { _onTaskbarCreated(); } catch { }
        }
        base.WndProc(ref m);
    }

    public void Destroy()
    {
        if (Handle != IntPtr.Zero) DestroyHandle();
    }
}
