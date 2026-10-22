using System.IO;
using System.Windows;
using System.Windows.Threading;
using SokoNote.Data;
using SokoNote.Services;
using SokoNote.Views;

namespace SokoNote.Tests;

/// <summary>
/// One long-lived STA thread that owns the WPF Application for all UI tests (an Application and its resources are
/// tied to the thread that created them, and only one can exist per process).
/// </summary>
internal static class UiThread
{
    // Tests run directly on this thread (not inside Dispatcher.Run/Invoke, where a shown window never loaded and the
    // app shut itself down); each test pumps the dispatcher itself with nested frames.
    private static readonly System.Collections.Concurrent.BlockingCollection<Action> Work = new();

    private static readonly Lazy<Thread> Ui = new(() =>
    {
        var t = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            // A plain Application with the app's resources — NOT SokoNote.App: constructing App queues App.OnStartup,
            // which opens the user's real data folder, backs it up and (with SokoNote running) activates the user's window.
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (var part in new[] { "Resources/Strings.ko.xaml", "Themes/Styles.xaml" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/SokoNote;component/{part}", UriKind.Absolute),
                });
            foreach (var work in Work.GetConsumingEnumerable()) work();
        }) { IsBackground = true, Name = "UI tests" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return t;
    });

    public static void Run(Action action)
    {
        _ = Ui.Value;
        Exception? error = null;
        using var done = new ManualResetEventSlim();
        Work.Add(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        });
        done.Wait();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    /// <summary>Lets the dispatcher work (layout, Loaded, posted callbacks) until <paramref name="done"/> or the time is up.</summary>
    public static void Pump(TimeSpan max, Func<bool>? done = null)
    {
        var until = DateTime.UtcNow + max;
        do
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Background,
                (s, _) => { ((DispatcherTimer)s!).Stop(); frame.Continue = false; }, Dispatcher.CurrentDispatcher);
            Dispatcher.PushFrame(frame);
        } while (DateTime.UtcNow < until && done?.Invoke() != true);
    }

    /// <summary>A fresh data folder + repository wired into the app, and an off-screen, never-activated main window.</summary>
    public static (MainWindow window, WorkLogRepository repo, List<Exception> errors) OpenMainWindow(Action<WorkLogRepository>? seed = null)
    {
        AppPaths.Init(Path.Combine(Path.GetTempPath(), "sokonote-tests", "ui-" + Guid.NewGuid().ToString("N")));
        var repo = new WorkLogRepository(AppPaths.DbPath);
        repo.SetSetting("ui.left", "-20000");
        repo.SetSetting("ui.top", "-20000");
        seed?.Invoke(repo);
        App.Repository = repo;
        var errors = new List<Exception>();
        Dispatcher.CurrentDispatcher.UnhandledException += (_, e) => { errors.Add(e.Exception); e.Handled = true; };
        var window = new MainWindow
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
        };
        window.Show();
        Pump(TimeSpan.FromSeconds(3), () => window.IsLoaded);
        Pump(TimeSpan.FromMilliseconds(200));
        return (window, repo, errors);
    }
}
