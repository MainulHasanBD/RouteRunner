// language: C#, file: Program.cs, target: Windows 10/11, .NET 8
// Gate order: single instance -> administrator token -> message loop.
// Both services are launched from this process, so this process must be elevated first.
using RoutRunner.Core;
using RoutRunner.Ui;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Installer hooks: the app owns the startup task definition, the .ps1 files only call it.
        if (args.Any(a => a.Equals("--register-startup", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.Exit(StartupTask.Create(out _) ? 0 : 1);
        }

        if (args.Any(a => a.Equals("--unregister-startup", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.Exit(StartupTask.Delete(out _) ? 0 : 1);
        }

        var minimize = args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        var skipElevation = args.Any(a => a.Equals("--no-elevate", StringComparison.OrdinalIgnoreCase));

        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(true, Instance.MutexName, out var first);
            if (!first)
            {
                // Resident copy wins: ask it to surface, then leave without a dialog.
                Instance.RequestShow();
                mutex.Dispose();
                return;
            }
        }
        catch (AbandonedMutexException)
        {
            // Previous instance died without releasing; we own it now.
        }
        catch
        {
            // ACL on the mutex can refuse an open across integrity levels — treat as first run.
        }

        if (!skipElevation && !Elevation.IsElevated)
        {
            ReleaseQuietly(mutex);

            // Silent path: the on-demand task already carries HighestAvailable, so running it
            // gives an elevated instance with no UAC prompt at all.
            if (StartupTask.TryRunLaunch()) return;

            var relaunched = Elevation.TryRelaunchElevated(minimize ? "--minimized" : string.Empty, out var cancelled);
            if (relaunched) return;

            MessageBox.Show(
                cancelled
                    ? "RoutRunner needs administrator rights so 9Router and OmniRoute inherit an elevated token.\n\n" +
                      "The UAC prompt was cancelled — nothing was started."
                    : "RoutRunner needs administrator rights so 9Router and OmniRoute inherit an elevated token, " +
                      "but the elevated relaunch failed.\n\nReinstall RoutRunner, then run it again.",
                "RoutRunner — administrator rights required",
                MessageBoxButtons.OK,
                cancelled ? MessageBoxIcon.Information : MessageBoxIcon.Error);
            return;
        }

        ApplicationConfiguration.Initialize();

        var settings = AppSettings.Load();
        if (minimize) settings.StartMinimized = true;

        var context = new TrayContext(settings);

        // Bootstrap after the message loop is pumping so the window gets a real first paint.
        var boot = new System.Windows.Forms.Timer { Interval = 120 };
        boot.Tick += (_, _) =>
        {
            boot.Stop();
            boot.Dispose();
            context.Bootstrap();
        };
        boot.Start();

        Application.Run(context);

        ReleaseQuietly(mutex);
    }

    private static void ReleaseQuietly(Mutex? mutex)
    {
        if (mutex is null) return;
        try
        {
            mutex.ReleaseMutex();
        }
        catch
        {
            // not owner / already released
        }
        mutex.Dispose();
    }
}
