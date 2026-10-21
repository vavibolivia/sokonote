using System.Windows;
using System.Windows.Threading;
using SokoNote.Data;
using SokoNote.Services;
using SokoNote.Views;

namespace SokoNote;

public partial class App : Application
{
    private static Mutex? _instanceMutex;

    public static WorkLogRepository Repository { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Logger.Error("Unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Logger.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        string? dataDir = null;
        for (int i = 0; i < e.Args.Length - 1; i++)
            if (e.Args[i] == "--data") dataDir = e.Args[i + 1];

        try
        {
            AppPaths.Init(dataDir);
            Repository = new WorkLogRepository(AppPaths.DbPath);
            Loc.Apply(Loc.Resolve(Repository.GetSetting("ui.language")));
            Logger.Info($"Startup · data={AppPaths.DataDir} · lang={Loc.Language}");
            try { BackupService.RunDailyBackup(Repository); }
            catch (Exception ex) { Logger.Error("Daily backup failed", ex); }
        }
        catch (Exception ex)
        {
            Logger.Error("Startup failed", ex);
            MessageBox.Show(string.Format(Loc.T("S.ErrorStartup"), ex.Message), "SokoNote", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // second instance → just focus the first one (only when using the default data folder)
        if (dataDir is null)
        {
            _instanceMutex = new Mutex(true, "SokoNote.SingleInstance", out var createdNew);
            if (!createdNew)
            {
                SingleInstance.ActivateOther();
                Shutdown(0);
                return;
            }
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("Dispatcher exception", e.Exception);
        e.Handled = true;
        try
        {
            (MainWindow as MainWindow)?.SaveNow();
        }
        catch (Exception ex)
        {
            Logger.Error("Emergency save failed", ex);
        }
        MessageBox.Show(string.Format(Loc.T("S.ErrorUnexpected"), e.Exception.Message), Loc.T("S.Error"),
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Info("Exit");
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}

internal static class SingleInstance
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public static void ActivateOther()
    {
        try
        {
            var me = System.Diagnostics.Process.GetCurrentProcess();
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(me.ProcessName))
            {
                if (p.Id == me.Id || p.MainWindowHandle == IntPtr.Zero) continue;
                ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE
                SetForegroundWindow(p.MainWindowHandle);
                return;
            }
        }
        catch
        {
            // best effort
        }
    }
}
