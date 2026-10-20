using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DeviceDeck.Bridge.JsonRpc;
using DeviceDeck.Bridge.Methods;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Hub;
using DeviceDeck.Core.Toolbox;

namespace DeviceDeck.Bridge.Toolbox;

/// <summary>
/// Registers the toolbox methods of docs/bridge-contract.md ("Toolbox") and pushes their events through
/// <paramref name="publish"/>: log.lines, log.ended, toolbox.job, wifi.qr.
/// </summary>
public static class ToolboxMethods
{
    /// <summary>Crash reports bigger than this are shown cut short (export has the whole file).</summary>
    public const int MaxCrashText = 2 * 1024 * 1024;

    public static JsonRpcDispatcher Register(JsonRpcDispatcher rpc, BridgeServices s, Action<string, object> publish)
    {
        var hub = s.Hub;
        var t = s.Toolbox;

        rpc.Register<DeviceParams, ToolboxFeatures>("toolbox.features", (p, _) =>
        {
            var device = hub.GetUsableDevice(p.DeviceId);
            var caps = device.Summary.Capabilities;
            var ios = device.Summary.Platform == DevicePlatform.Ios;
            return Task.FromResult(new ToolboxFeatures(
                Log: caps.HasFlag(DeviceCapability.Syslog) && device is IDeviceLogSource,
                CrashReports: caps.HasFlag(DeviceCapability.CrashReports) && device is ICrashReportSource,
                Mirror: caps.HasFlag(DeviceCapability.ScreenMirror) && device is IScreenMirrorSource,
                Ringtone: !ios && device is IRingtoneTarget { RingtoneExtensions.Count: > 0 },
                WifiSync: ios && device is IWifiSyncSettings,
                Report: caps.HasFlag(DeviceCapability.Info),
                WirelessDebugging: hub.Providers.OfType<IWirelessDebugging>().Any()));
        });

        // ---- live log ----
        rpc.Register<DeviceParams, SessionResult>("log.start", (p, ct) =>
        {
            var source = Feature<IDeviceLogSource>(hub, p.DeviceId, DeviceCapability.Syslog, "show its log");
            // ct is the page's: a reload stops the stream.
            return Task.FromResult(new SessionResult(t.Logs.Start(p.DeviceId, source, publish, t, ct).Id));
        });
        rpc.Register<SessionParams, Empty>("log.stop", async (p, _) =>
        {
            await t.Logs.StopAsync(p.SessionId).ConfigureAwait(false);
            return Empty.Value;
        });
        rpc.Register<SessionParams, Empty>("log.clear", (p, _) =>
        {
            t.Logs.Get(p.SessionId).Clear();
            return Task.FromResult(Empty.Value);
        });
        rpc.Register<LogExportParams, SavedFileResult>("log.export", async (p, ct) =>
        {
            var session = t.Logs.Get(p.SessionId);
            var folder = Folder(p.Destination) ?? Path.Combine(t.OutputFolder, "Logs");
            var path = FreePath(folder, $"{NameOf(hub, session.DeviceId)} log {DateTime.Now:yyyy-MM-dd HHmmss}", ".txt");
            var count = await session.ExportAsync(path, p.MinLevel, p.Search, ct).ConfigureAwait(false);
            return new SavedFileResult(path, count);
        });

        // ---- crash reports ----
        rpc.Register<DeviceParams, CrashReportList>("crash.list", (p, ct) =>
            Feature<ICrashReportSource>(hub, p.DeviceId, DeviceCapability.CrashReports, "show crash reports").ListCrashReportsAsync(ct));
        rpc.Register<CrashParams, CrashTextResult>("crash.read", async (p, ct) =>
        {
            var source = Feature<ICrashReportSource>(hub, p.DeviceId, DeviceCapability.CrashReports, "show crash reports");
            using var buffer = new CappedStream(MaxCrashText);
            await source.ReadCrashReportAsync(p.Id, buffer, ct).ConfigureAwait(false);
            return new CrashTextResult(buffer.Text(), buffer.Truncated, buffer.Total);
        });
        rpc.Register<CrashIdsParams, CrashExportResult>("crash.export", async (p, ct) =>
        {
            var source = Feature<ICrashReportSource>(hub, p.DeviceId, DeviceCapability.CrashReports, "save crash reports");
            var ids = Distinct(p.Ids);
            var folder = Folder(p.Destination)
                         ?? Path.Combine(t.OutputFolder, "Crash reports", Safe($"{NameOf(hub, p.DeviceId)} {DateTime.Now:yyyy-MM-dd HHmm}"));
            Directory.CreateDirectory(folder);
            var files = new List<string>();
            foreach (var id in ids)
            {
                var path = FreePath(folder, Path.GetFileNameWithoutExtension(CrashFileName(id)), Path.GetExtension(CrashFileName(id)));
                await using (var file = File.Create(path))
                {
                    await source.ReadCrashReportAsync(id, file, ct).ConfigureAwait(false);
                }

                files.Add(path);
            }

            return new CrashExportResult(folder, files);
        });
        rpc.Register<CrashIdsParams, DeletedResult>("crash.delete", async (p, ct) =>
        {
            var source = Feature<ICrashReportSource>(hub, p.DeviceId, DeviceCapability.CrashReports, "delete crash reports");
            var ids = Distinct(p.Ids);
            await source.DeleteCrashReportsAsync(ids, ct).ConfigureAwait(false);
            return new DeletedResult(ids.Count);
        });

        // ---- device report ----
        rpc.Register<ReportParams, HtmlResult>("report.preview", async (p, ct) =>
        {
            var device = Device(hub, p.DeviceId, DeviceCapability.Info, "make a report");
            var data = await DeviceReport.GatherAsync(device, ct).ConfigureAwait(false);
            return new HtmlResult(DeviceReport.ToHtml(data, Language(p.Language), p.RevealSerials, DateTimeOffset.Now, s.Version));
        });
        rpc.Register<ReportParams, SavedFileResult>("report.save", async (p, ct) =>
        {
            var device = Device(hub, p.DeviceId, DeviceCapability.Info, "make a report");
            if (p.Format == ReportFormat.Pdf && t.Pdf is null)
            {
                throw new DeviceException(DeviceErrorCodes.NotSupported, "PDF reports are made by the DeviceDeck app; save it as HTML here.");
            }

            var data = await DeviceReport.GatherAsync(device, ct).ConfigureAwait(false);
            var now = DateTimeOffset.Now;
            var html = DeviceReport.ToHtml(data, Language(p.Language), p.RevealSerials, now, s.Version);
            var folder = Folder(p.Destination) ?? Path.Combine(t.OutputFolder, "Reports");
            Directory.CreateDirectory(folder);
            var extension = p.Format == ReportFormat.Pdf ? ".pdf" : ".html";
            var name = DeviceReport.FileName(data.Summary, now, "");
            var path = FreePath(folder, name, extension);
            if (p.Format == ReportFormat.Pdf)
            {
                await t.Pdf!.PrintAsync(html, path, ct).ConfigureAwait(false);
            }
            else
            {
                await File.WriteAllTextAsync(path, html, new UTF8Encoding(false), ct).ConfigureAwait(false);
            }

            return new SavedFileResult(path, 1);
        });

        // ---- HEIC → JPEG (files on the PC) ----
        rpc.Register("heic.status", async ct => new HeicStatusResult(t.Heic is { } heic && await heic.IsAvailableAsync(ct).ConfigureAwait(false)));
        rpc.Register<HeicConvertParams, JobStartedResult>("heic.convert", async (p, ct) =>
        {
            var converter = t.Heic ?? throw new DeviceException(DeviceErrorCodes.NotSupported, "HEIC conversion needs the DeviceDeck app on Windows.");
            if (p.Quality is < 1 or > 100)
            {
                throw new ArgumentException("quality is 1 to 100.");
            }

            if (p.Files.Count == 0 || p.Files.Any(f => !Path.IsPathFullyQualified(f)))
            {
                throw new ArgumentException("files must be full paths on this PC.");
            }

            if (!await converter.IsAvailableAsync(ct).ConfigureAwait(false))
            {
                throw new DeviceException(DeviceErrorCodes.DriverMissing,
                    "Windows can't open HEIC photos yet. Install HEIF Image Extensions from the Microsoft Store.");
            }

            var destination = Folder(p.Destination);
            var items = await Task.Run(() => HeicBatch.Plan(p.Files, destination), ct).ConfigureAwait(false);
            if (items.Count == 0)
            {
                throw new DeviceException(DeviceErrorCodes.PathNotFound, "There are no HEIC photos there.");
            }

            var job = t.Jobs.StartHeic(items, converter, p.Quality, publish);
            return new JobStartedResult(job.Id, job.Total);
        });
        rpc.Register<ToolboxJobParams, Empty>("toolbox.cancelJob", (p, _) =>
        {
            if (!t.Jobs.Cancel(p.JobId))
            {
                throw new DeviceException(DeviceErrorCodes.PathNotFound, "That job has already finished.");
            }

            return Task.FromResult(Empty.Value);
        });

        // ---- mirroring (opens its own window) ----
        rpc.Register<DeviceParams, Empty>("mirror.open", (p, _) =>
        {
            Feature<IScreenMirrorSource>(hub, p.DeviceId, DeviceCapability.ScreenMirror, "mirror its screen");
            var windows = t.MirrorWindows ?? throw new DeviceException(DeviceErrorCodes.NotSupported, "Mirroring opens in a window of the DeviceDeck app.");
            windows.Open(p.DeviceId);
            return Task.FromResult(Empty.Value);
        });

        // ---- Wi-Fi ----
        rpc.Register("wifi.status", _ => Task.FromResult(new WifiStatusResult(hub.Providers.OfType<IWirelessDebugging>().Any())));
        rpc.Register<WifiPairParams, WirelessResult>("wifi.pair", (p, ct) => Wireless(hub).PairAsync(p.Host, p.Port, p.Code.Trim(), ct));
        rpc.Register<WifiConnectParams, WirelessResult>("wifi.connect", (p, ct) => Wireless(hub).ConnectAsync(p.Host, p.Port, ct));
        rpc.Register<DeviceParams, Empty>("wifi.disconnect", async (p, ct) =>
        {
            await Wireless(hub).DisconnectAsync(p.DeviceId, ct).ConfigureAwait(false);
            return Empty.Value;
        });
        rpc.Register("wifi.qrStart", ct => Task.FromResult(StartQr(hub, t, publish, ct)));
        rpc.Register<SessionParams, Empty>("wifi.qrCancel", (p, _) =>
        {
            CancellationTokenSource? cts;
            lock (t.QrSessions)
            {
                t.QrSessions.Remove(p.SessionId, out cts);
            }

            cts?.Cancel();
            return Task.FromResult(Empty.Value);
        });
        rpc.Register<DeviceParams, WifiSyncResult>("wifi.iphoneSync", async (p, ct) =>
            new WifiSyncResult(await WifiSync(hub, p.DeviceId).GetWifiSyncAsync(ct).ConfigureAwait(false)));
        rpc.Register<WifiSyncParams, WifiSyncResult>("wifi.setIphoneSync", async (p, ct) =>
        {
            var settings = WifiSync(hub, p.DeviceId);
            await settings.SetWifiSyncAsync(p.Enabled, ct).ConfigureAwait(false);
            return new WifiSyncResult(await settings.GetWifiSyncAsync(ct).ConfigureAwait(false));
        });

        // ---- ringtones ----
        rpc.Register<RingtoneParams, RingtoneResultDto>("ringtone.create", (p, ct) => CreateRingtoneAsync(hub, t, p, ct));

        return rpc;
    }

    private static async Task<RingtoneResultDto> CreateRingtoneAsync(DeviceHub hub, ToolboxServices t, RingtoneParams p, CancellationToken ct)
    {
        var device = hub.GetUsableDevice(p.DeviceId);
        if (device.Summary.Platform == DevicePlatform.Ios || device is not IRingtoneTarget { RingtoneExtensions.Count: > 0 } target)
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported,
                "An iPhone takes ringtones only through a Finder or iTunes sync, not over this USB connection.");
        }

        if (p.Files.Count != 1 || !Path.IsPathFullyQualified(p.Files[0]))
        {
            throw new ArgumentException("files must hold one full path on this PC.");
        }

        var source = p.Files[0];
        if (!File.Exists(source))
        {
            throw new DeviceException(DeviceErrorCodes.PathNotFound, "That audio file isn't on this PC any more.");
        }

        if (!(p.StartSeconds >= 0) || !(p.EndSeconds > p.StartSeconds) || p.EndSeconds - p.StartSeconds > 300)
        {
            throw new ArgumentException("A ringtone runs from startSeconds to endSeconds, at most five minutes.");
        }

        var extension = Path.GetExtension(source);
        var managed = new ManagedAudioTrimmer();
        IAudioTrimmer trimmer = managed.CanTrim(extension) ? managed
            : t.AudioTrimmer is { } platform && platform.CanTrim(extension) ? platform
            : throw new DeviceException(DeviceErrorCodes.NotSupported, $"DeviceDeck can't cut {extension} files.");

        Directory.CreateDirectory(t.TempFolder);
        var temp = Path.Combine(t.TempFolder, "ringtone-" + Guid.NewGuid().ToString("N")[..8]);
        string? trimmed = null;
        try
        {
            trimmed = await trimmer.TrimAsync(source, TimeSpan.FromSeconds(p.StartSeconds), TimeSpan.FromSeconds(p.EndSeconds), temp, ct).ConfigureAwait(false);
            var outExtension = Path.GetExtension(trimmed).ToLowerInvariant();
            if (!target.RingtoneExtensions.Contains(outExtension))
            {
                throw new DeviceException(DeviceErrorCodes.NotSupported, $"The phone can't play {outExtension} ringtones.");
            }

            var name = string.IsNullOrWhiteSpace(p.Name) ? Path.GetFileNameWithoutExtension(source) : p.Name.Trim();
            await using var stream = File.OpenRead(trimmed);
            var length = stream.Length;
            var result = await target.AddRingtoneAsync(name + outExtension, stream, length, p.SetAsDefault, ct).ConfigureAwait(false);
            return new RingtoneResultDto(result.DevicePath, result.IsDefault, length);
        }
        finally
        {
            // Our own temporary cut, never the user's file.
            if (trimmed is not null)
            {
                try
                {
                    File.Delete(trimmed);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static WifiQrResult StartQr(DeviceHub hub, ToolboxServices t, Action<string, object> publish, CancellationToken page)
    {
        var wireless = Wireless(hub);
        var id = Guid.NewGuid().ToString("N")[..12];
        var name = "devicedeck-" + RandomText(6, "abcdefghijkmnpqrstuvwxyz23456789");
        var password = RandomText(10, "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789");
        var cts = CancellationTokenSource.CreateLinkedTokenSource(page);
        cts.CancelAfter(t.QrTimeout);
        lock (t.QrSessions)
        {
            t.QrSessions[id] = cts;
        }

        void Send(string stage, string? serial = null, string? message = null, ToolboxError? error = null)
        {
            if (!page.IsCancellationRequested)
            {
                publish("wifi.qr", new WifiQrEvent(id, stage, serial, message, error));
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await wireless.PairWithQrAsync(name, password, new Stage(stage => Send(stage)), cts.Token).ConfigureAwait(false);
                Send("done", result.Serial, result.Message);
            }
            catch (OperationCanceledException)
            {
                Send(TimedOut(cts) ? "expired" : "cancelled");
            }
            catch (DeviceException ex)
            {
                Send("failed", error: new ToolboxError(ex.Code, ex.Message));
            }
            catch (Exception ex)
            {
                Send("failed", error: new ToolboxError("internal", ex.Message));
            }
            finally
            {
                lock (t.QrSessions)
                {
                    t.QrSessions.Remove(id);
                }

                cts.Dispose();
            }
        }, CancellationToken.None);

        return new WifiQrResult(id, name, password, $"WIFI:T:ADB;S:{name};P:{password};;");

        bool TimedOut(CancellationTokenSource source)
        {
            lock (t.QrSessions)
            {
                // Still registered = nobody called wifi.qrCancel, so the timer fired.
                return t.QrSessions.ContainsKey(id) && source.IsCancellationRequested;
            }
        }
    }

    /// <summary>Synchronous progress, so stages reach the page in order.</summary>
    private sealed class Stage(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private static string RandomText(int length, string alphabet) =>
        string.Create(length, alphabet, (span, chars) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
            }
        });

    // ---- helpers ----

    private static IDevice Device(DeviceHub hub, string deviceId, DeviceCapability capability, string what)
    {
        var device = hub.GetUsableDevice(deviceId);
        if ((device.Summary.Capabilities & capability) != capability)
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, $"{device.Summary.Name} can't {what}.");
        }

        return device;
    }

    private static T Feature<T>(DeviceHub hub, string deviceId, DeviceCapability capability, string what) where T : class =>
        Device(hub, deviceId, capability, what) as T
        ?? throw new DeviceException(DeviceErrorCodes.NotSupported, $"This phone can't {what} yet.");

    private static IWirelessDebugging Wireless(DeviceHub hub) =>
        hub.Providers.OfType<IWirelessDebugging>().FirstOrDefault()
        ?? throw new DeviceException(DeviceErrorCodes.NotSupported, "Wireless debugging needs DeviceDeck's Android support, which isn't running.");

    private static IWifiSyncSettings WifiSync(DeviceHub hub, string deviceId)
    {
        var device = hub.GetUsableDevice(deviceId);
        return device.Summary.Platform == DevicePlatform.Ios && device is IWifiSyncSettings settings
            ? settings
            : throw new DeviceException(DeviceErrorCodes.NotSupported, "Only an iPhone has the Wi-Fi sync switch.");
    }

    private static string NameOf(DeviceHub hub, string deviceId) =>
        Safe(hub.TryGetDevice(deviceId, out var device) ? device.Summary.Name : deviceId);

    private static string Language(string language) => language == "fa" ? "fa" : "en";

    /// <summary>A destination folder from the page: null when none was given, else it must be a full path.</summary>
    private static string? Folder(string? destination)
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            return null;
        }

        return Path.IsPathFullyQualified(destination) ? destination : throw new ArgumentException("destination must be a full path on this PC.");
    }

    private static List<string> Distinct(IReadOnlyList<string> ids)
    {
        var list = ids.Distinct(StringComparer.Ordinal).ToList();
        return list.Count > 0 ? list : throw new ArgumentException("ids is empty.");
    }

    /// <summary>A Windows-safe file name for a crash report id ("Retired/Foo-2026-….ips", "dropbox/data_app_crash/2026-10-19 21:14:03").</summary>
    internal static string CrashFileName(string id)
    {
        var parts = id.Split('/');
        var name = parts is ["dropbox", var tag, var date]
            ? $"{tag}-{date.Replace(":", "", StringComparison.Ordinal).Replace(' ', '-')}.txt"
            : parts[^1];
        name = Safe(name);
        return Path.GetExtension(name).Length == 0 ? name + ".txt" : name;
    }

    private static string Safe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = string.Concat(name.Select(ch => invalid.Contains(ch) ? '_' : ch)).Trim().TrimEnd('.');
        return safe.Length == 0 ? "DeviceDeck" : safe;
    }

    /// <summary>folder\name.ext, or folder\name (2).ext when that is taken. Never overwrites.</summary>
    internal static string FreePath(string folder, string name, string extension)
    {
        Directory.CreateDirectory(folder);
        name = Safe(name);
        for (var n = 1; ; n++)
        {
            var path = Path.Combine(folder, n == 1 ? name + extension : $"{name} ({n.ToString(CultureInfo.InvariantCulture)}){extension}");
            if (!File.Exists(path))
            {
                return path;
            }
        }
    }

    /// <summary>Keeps the first <paramref name="limit"/> bytes and counts the rest.</summary>
    private sealed class CappedStream(int limit) : Stream
    {
        private readonly MemoryStream _kept = new();

        public long Total { get; private set; }

        public bool Truncated => Total > limit;

        public string Text() => Encoding.UTF8.GetString(_kept.GetBuffer(), 0, (int)_kept.Length);

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var room = (int)Math.Max(0, limit - _kept.Length);
            _kept.Write(buffer[..Math.Min(room, buffer.Length)]);
            Total += buffer.Length;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer.AsSpan(offset, count));
            return Task.CompletedTask;
        }

        public override void Flush()
        {
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Total;
        public override long Position { get => Total; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _kept.Dispose();
            base.Dispose(disposing);
        }
    }
}
