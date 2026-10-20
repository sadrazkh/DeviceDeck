using System.Diagnostics;
using DeviceDeck.Bridge;
using DeviceDeck.Core.Abstractions;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.Storage.Pickers;

namespace DeviceDeck.Shell.Hosting;

/// <summary>shell.pickFolder and shell.revealInExplorer, on the window's UI thread.</summary>
internal sealed class ShellServices(DispatcherQueue dispatcher, WindowId window) : IShellServices
{
    public Task<string?> PickFolderAsync(string? title, CancellationToken ct)
    {
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = dispatcher.TryEnqueue(async () =>
        {
            try
            {
                var picker = new FolderPicker(window)
                {
                    SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                    CommitButtonText = string.IsNullOrWhiteSpace(title) ? "Select folder" : title,
                };
                var folder = await picker.PickSingleFolderAsync();
                result.TrySetResult(folder?.Path);
            }
            catch (Exception ex)
            {
                result.TrySetException(ex);
            }
        });

        if (!queued)
        {
            result.TrySetException(new InvalidOperationException("The window is closing."));
        }

        return result.Task.WaitAsync(ct);
    }

    public void RevealInExplorer(string path)
    {
        var full = Path.GetFullPath(path);
        if (File.Exists(full))
        {
            Start("explorer.exe", $"/select,\"{full}\"");
        }
        else if (Directory.Exists(full))
        {
            Start("explorer.exe", $"\"{full}\"");
        }
        else if (Path.GetDirectoryName(full) is { } parent && Directory.Exists(parent))
        {
            Start("explorer.exe", $"\"{parent}\"");
        }
        else
        {
            throw new DeviceException(DeviceErrorCodes.PathNotFound, "That file or folder isn't on this PC.", new DirectoryNotFoundException(full));
        }
    }

    private static void Start(string file, string arguments) =>
        Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true })?.Dispose();
}
