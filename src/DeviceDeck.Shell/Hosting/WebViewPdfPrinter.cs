using DeviceDeck.Core.Backups;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;

namespace DeviceDeck.Shell.Hosting;

/// <summary>
/// messages.export to PDF: a hidden WebView2 opens the exported HTML page (so Persian text is shaped and pictures
/// embedded as the page shows them) and prints it. Runs on the window's UI thread, one page at a time.
/// </summary>
internal sealed class WebViewPdfPrinter(DispatcherQueue dispatcher, WindowId window) : IPdfPrinter
{
    private readonly SemaphoreSlim _one = new(1, 1);
    private CoreWebView2Environment? _environment;

    public async Task PrintToPdfAsync(string htmlPath, string pdfPath, CancellationToken ct)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var queued = dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    await PrintOnUiThreadAsync(htmlPath, pdfPath);
                    done.TrySetResult();
                }
                catch (Exception ex)
                {
                    done.TrySetException(ex);
                }
            });
            if (!queued)
                throw new InvalidOperationException("The window is closing.");
            await done.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task PrintOnUiThreadAsync(string htmlPath, string pdfPath)
    {
        // Its own profile folder: the page's WebView2 environment was made with options this one needn't match.
        _environment ??= await CoreWebView2Environment.CreateWithOptionsAsync(
            null,
            Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "DeviceDeck", "WebView2-print")).FullName,
            new CoreWebView2EnvironmentOptions());
        var parent = Win32Interop.GetWindowFromWindowId(window);
        var controller = await _environment.CreateCoreWebView2ControllerAsync(CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)parent));
        try
        {
            controller.IsVisible = false;
            var core = controller.CoreWebView2;
            core.Settings.IsScriptEnabled = false;
            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            core.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
            core.Navigate(new Uri(htmlPath).AbsoluteUri);
            if (!await loaded.Task.WaitAsync(TimeSpan.FromMinutes(2)))
                throw new IOException("The conversation page could not be opened for printing.");

            var settings = _environment.CreatePrintSettings();
            settings.ShouldPrintBackgrounds = true;
            settings.ShouldPrintHeaderAndFooter = false;
            if (!await core.PrintToPdfAsync(pdfPath, settings))
                throw new IOException("WebView2 could not write the PDF.");
        }
        finally
        {
            controller.Close();
        }
    }
}
