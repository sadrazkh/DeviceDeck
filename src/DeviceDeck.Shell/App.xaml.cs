using DeviceDeck.Shell.Hosting;
using Microsoft.UI.Xaml;

namespace DeviceDeck.Shell;

public partial class App : Application
{
    private MainWindow? _window;
    private HostServices? _host;

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
        _host = HostServices.Create();
        _window = new MainWindow(_host);
        Program.Reactivated += (_, _) => _window.DispatcherQueue.TryEnqueue(_window.BringToFront);
        _window.Activate();
        await _host.StartAsync();
    }
}
