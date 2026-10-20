using DeviceDeck.Core.Toolbox;
using DeviceDeck.Shell.Hosting;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;

namespace DeviceDeck.Shell.Toolbox;

/// <summary>
/// HTML → PDF with WebView2's PrintToPdfAsync, in a browser that is never shown: a hidden window hosts a controller,
/// the page is loaded with NavigateToString, printed on A4 with backgrounds, and everything is closed again.
/// </summary>
internal sealed class WebViewPdfPrinter(DispatcherQueue dispatcher, string userDataFolder) : IPdfPrinter
{
    public Task PrintAsync(string html, string pdfPath, CancellationToken ct)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    await PrintOnUiThreadAsync(html, pdfPath, ct);
                    done.TrySetResult();
                }
                catch (Exception ex)
                {
                    done.TrySetException(ex);
                }
            }))
        {
            done.TrySetException(new InvalidOperationException("The app is closing."));
        }

        return done.Task.WaitAsync(ct);
    }

    private async Task PrintOnUiThreadAsync(string html, string pdfPath, CancellationToken ct)
    {
        var host = new Window { Title = "DeviceDeck report" };
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(host);
        CoreWebView2Controller? controller = null;
        try
        {
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, userDataFolder, new CoreWebView2EnvironmentOptions());
            controller = await environment.CreateCoreWebView2ControllerAsync(CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)hwnd));
            controller.IsVisible = false;
            var core = controller.CoreWebView2;
            core.Settings.IsScriptEnabled = false;

            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            core.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
            core.NavigateToString(html);
            if (!await loaded.Task.WaitAsync(TimeSpan.FromSeconds(20), ct))
            {
                throw new InvalidOperationException("The report page didn't load for printing.");
            }

            var settings = environment.CreatePrintSettings();
            settings.ShouldPrintBackgrounds = true;
            settings.ShouldPrintHeaderAndFooter = false;
            settings.PageWidth = 8.27;  // A4, inches
            settings.PageHeight = 11.69;
            if (!await core.PrintToPdfAsync(pdfPath, settings))
            {
                throw new IOException("WebView2 couldn't write the PDF.");
            }
        }
        finally
        {
            controller?.Close();
            host.Close();
        }

        ShellLog.Info($"Saved a PDF report to {pdfPath}.");
    }
}
