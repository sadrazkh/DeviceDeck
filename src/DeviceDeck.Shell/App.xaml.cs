using DeviceDeck.Shell.Hosting;
using Microsoft.UI.Xaml;

namespace DeviceDeck.Shell;

public partial class App : Application
{
    private MainWindow? _window;

    /// <summary>The running host, for Program.Main to stop after the last window has closed.</summary>
    internal static HostServices? Host { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => ShellLog.Error("Unhandled exception on the UI thread.", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ShellLog.Error("Unhandled exception.", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ShellLog.Error("Unobserved task exception.", e.Exception);
            e.SetObserved();
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var host = HostServices.Create();
        Host = host;
        _window = new MainWindow(host);
        Program.Reactivated += (_, activation) =>
        {
            if (Program.IsQuit(activation))
            {
                _window.Quit();
            }
            else
            {
                _window.DispatcherQueue.TryEnqueue(_window.BringToFront);
            }
        };
        _window.Activate();
        await host.StartAsync();
        _window.ArmNotifications();
    }
}
