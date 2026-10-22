using System.Windows;
using System.Windows.Threading;
using SokoNote.Data;
using SokoNote.Services;
using SokoNote.Views;

namespace SokoNote;

public partial class App : Application
{
    private static Mutex? _instanceMutex;

    public static WorkLogRepository Repository { get; internal set; } = null!;

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
            Repository = OpenRepository();
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

    /// <summary>Opens the database; when it is damaged, offers to fall back to the newest usable daily backup.</summary>
    private static WorkLogRepository OpenRepository()
    {
        try
        {
            return new WorkLogRepository(AppPaths.DbPath);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            Logger.Error("Database could not be opened", ex);
            Loc.Apply(Loc.DetectDefault()); // settings live in the broken db
            var answer = MessageBox.Show(string.Format(Loc.T("S.DbDamaged"), ex.Message), "SokoNote",
                MessageBoxButton.YesNo, MessageBoxImage.Error, MessageBoxResult.Yes);
            if (answer != MessageBoxResult.Yes) throw;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var used = DatabaseRecovery.RestoreLatestBackup(AppPaths.DbPath, AppPaths.BackupDir);
            if (used is null) throw new InvalidOperationException(Loc.T("S.DbNoBackup"), ex);
            Logger.Info($"Restored database from {used}");
            MessageBox.Show(string.Format(Loc.T("S.DbRestored"), System.IO.Path.GetFileName(used)), "SokoNote",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return new WorkLogRepository(AppPaths.DbPath);
        }
    }

    private readonly Queue<DateTime> _recentErrors = new();
    private bool _showingError;

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("Dispatcher exception", e.Exception);
        e.Handled = true;

        // an error raised from layout repeats on every pass: save once and stop instead of looping dialogs
        var now = DateTime.UtcNow;
        _recentErrors.Enqueue(now);
        while (_recentErrors.Count > 0 && now - _recentErrors.Peek() > TimeSpan.FromSeconds(5)) _recentErrors.Dequeue();
        var storm = _recentErrors.Count > 20;
        if (_showingError && !storm) return;
        try
        {
            (MainWindow as MainWindow)?.SaveNow();
        }
        catch (Exception ex)
        {
            Logger.Error("Emergency save failed", ex);
        }
        if (storm)
        {
            Logger.Error("Repeated errors, shutting down after an emergency save");
            Shutdown(2);
            return;
        }
        _showingError = true;
        try
        {
            MessageBox.Show(string.Format(Loc.T("S.ErrorUnexpected"), e.Exception.Message), Loc.T("S.Error"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _showingError = false;
        }
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
