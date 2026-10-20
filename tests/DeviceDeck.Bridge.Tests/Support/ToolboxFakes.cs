using System.Collections.Concurrent;
using DeviceDeck.Bridge.Toolbox;
using DeviceDeck.Core.Toolbox;

namespace DeviceDeck.Bridge.Tests.Support;

/// <summary>The Shell's toolbox services, faked, with every folder inside the test's temp root.</summary>
internal sealed class ToolboxFakes
{
    public ToolboxFakes(string root)
    {
        Services = new ToolboxServices
        {
            Heic = Heic,
            Pdf = Pdf,
            MirrorWindows = Mirrors,
            OutputFolder = Path.Combine(root, "out"),
            PicturesFolder = Path.Combine(root, "pictures"),
            TempFolder = Path.Combine(root, "temp"),
            LogFlushInterval = TimeSpan.FromMilliseconds(20),
            QrTimeout = TimeSpan.FromSeconds(30),
        };
    }

    public FakeHeicConverter Heic { get; } = new();
    public FakePdfPrinter Pdf { get; } = new();
    public FakeMirrorWindows Mirrors { get; } = new();
    public ToolboxServices Services { get; }
}

internal sealed class FakeHeicConverter : IHeicConverter
{
    public bool Available { get; set; } = true;

    /// <summary>Lets a test hold conversions mid-job (cancel).</summary>
    public TaskCompletionSource? Gate { get; set; }

    public ConcurrentQueue<(string Source, string Destination, int Quality)> Calls { get; } = new();

    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(Available);

    public async Task ConvertAsync(string source, string destination, int quality, CancellationToken ct)
    {
        Calls.Enqueue((source, destination, quality));
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(ct);
        }

        if (Path.GetFileName(source).Contains("broken", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Windows couldn't read this photo.");
        }

        await File.WriteAllBytesAsync(destination, [0xFF, 0xD8, 0xFF, 0xD9], ct);
    }
}

internal sealed class FakePdfPrinter : IPdfPrinter
{
    public List<string> Printed { get; } = [];

    public async Task PrintAsync(string html, string pdfPath, CancellationToken ct)
    {
        Printed.Add(html);
        await File.WriteAllTextAsync(pdfPath, "%PDF-fake", ct);
    }
}

internal sealed class FakeMirrorWindows : IMirrorWindows
{
    public ConcurrentQueue<string> Opened { get; } = new();

    public void Open(string deviceId) => Opened.Enqueue(deviceId);
}
