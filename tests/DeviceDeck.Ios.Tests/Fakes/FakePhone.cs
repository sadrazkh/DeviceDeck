using DeviceDeck.Ios.Plist;

namespace DeviceDeck.Ios.Tests.Fakes;

/// <summary>A phone's lockdownd and the services DeviceDeck uses, answering from in-memory state.</summary>
internal sealed class FakePhone
{
    private const int LockdownPort = 62078;
    private readonly Dictionary<int, string> _servicePorts = [];
    private int _nextPort = 49152;

    public FakePhone(string udid)
    {
        Udid = udid;
        Values["DevicePublicKey"] = System.Text.Encoding.ASCII.GetBytes(TestPairing.DeviceKey.ExportRSAPublicKeyPem());
    }

    public string Udid { get; }

    public bool Network { get; set; }

    /// <summary>GetValue with no domain, after StartSession.</summary>
    public PlistDict Values { get; set; } = PlistSerializer.ParseDict(Fixture.Bytes("Plist", "lockdown_getvalue_all.xml"));

    /// <summary>What GetValue gives without a session (before pairing).</summary>
    public HashSet<string> UnpairedKeys { get; } = ["DeviceName", "ProductType", "ProductVersion", "BuildVersion", "DeviceClass", "UniqueDeviceID", "DevicePublicKey"];

    /// <summary>Answers to successive Pair requests: a lockdown error, or null to accept.</summary>
    public Queue<string?> PairReplies { get; } = new();

    /// <summary>The PairRecord of the accepted Pair request.</summary>
    public PlistDict? AcceptedPairRecord { get; private set; }

    public Dictionary<string, PlistDict> Domains { get; } = [];

    /// <summary>Keys a domain answers only when asked for by name, not in its full dump (as iOS 27 does).</summary>
    public Dictionary<string, PlistDict> ByNameOnly { get; } = [];

    /// <summary>Lockdown error for StartSession (e.g. "InvalidHostID", "PasswordProtected"); null = succeed.</summary>
    public string? StartSessionError { get; set; }

    /// <summary>Lockdown error for StartService; null = succeed.</summary>
    public string? StartServiceError { get; set; }

    public string? ExpectedHostId { get; set; }

    public FakeAfcServer Media { get; } = new();

    public Dictionary<string, FakeAfcServer> AppContainers { get; } = new(StringComparer.Ordinal);

    public List<PlistDict> Apps { get; } = [];

    public Dictionary<string, byte[]> Icons { get; } = new(StringComparer.Ordinal);

    public PlistDict? PowerSource { get; set; }

    public List<string> Log { get; } = [];

    /// <summary>Install: the progress statuses to send before Complete, or an Error to fail with.</summary>
    public string? InstallError { get; set; }

    public bool HasPort(int port) => port == LockdownPort || _servicePorts.ContainsKey(port);

    public Task ServePortAsync(int port, Stream s, CancellationToken ct) =>
        port == LockdownPort ? ServeLockdownAsync(s, ct) : ServeServiceAsync(_servicePorts[port], s, ct);

    private async Task ServeLockdownAsync(Stream s, CancellationToken ct)
    {
        var session = false;
        while (await Wire.ReadPlistAsync(s, ct) is { } msg)
        {
            var request = msg.GetString("Request") ?? "";
            lock (Log)
                Log.Add("lockdown:" + request);
            PlistDict reply = new() { ["Request"] = request };
            switch (request)
            {
                case "QueryType":
                    reply["Type"] = "com.apple.mobile.lockdown";
                    break;
                case "GetValue":
                {
                    var domain = msg.GetString("Domain");
                    var key = msg.GetString("Key");
                    lock (Log)
                        Log.Add($"lockdown:GetValue {domain ?? "-"} {key ?? "*"}");
                    PlistDict source = domain is null ? Values : Domains.GetValueOrDefault(domain) ?? new PlistDict();
                    if (!session && domain is null)
                        source = new PlistDict(source.Where(kv => UnpairedKeys.Contains(kv.Key)).ToDictionary());
                    else if (!session)
                    {
                        reply["Error"] = "InvalidSession";
                        break;
                    }
                    if (key is null)
                        reply["Value"] = source;
                    else if (source.TryGetValue(key, out var v))
                        reply["Value"] = v;
                    else if (domain is not null && ByNameOnly.GetValueOrDefault(domain)?.TryGetValue(key, out var hiddenValue) == true)
                        reply["Value"] = hiddenValue!;
                    else
                        reply["Error"] = "MissingValue";
                    break;
                }
                case "StartSession":
                    if (StartSessionError is not null)
                        reply["Error"] = StartSessionError;
                    else if (ExpectedHostId is not null && msg.GetString("HostID") != ExpectedHostId)
                        reply["Error"] = "InvalidHostID";
                    else
                    {
                        session = true;
                        reply["SessionID"] = "FAKE-SESSION";
                        reply["EnableSessionSSL"] = false;
                    }
                    break;
                case "StopSession":
                    session = false;
                    break;
                case "Pair":
                {
                    var record = msg.GetDict("PairRecord");
                    var answer = PairReplies.Count > 0 ? PairReplies.Dequeue() : null;
                    if (record is null)
                        reply["Error"] = "InvalidPairRecord";
                    else if (answer is not null)
                        reply["Error"] = answer;
                    else
                    {
                        AcceptedPairRecord = record;
                        ExpectedHostId = record.GetString("HostID");
                        reply["EscrowBag"] = new byte[] { 0xE5, 0xC0 };
                    }
                    break;
                }
                case "StartService":
                    if (!session)
                        reply["Error"] = "InvalidSession";
                    else if (StartServiceError is not null)
                        reply["Error"] = StartServiceError;
                    else
                    {
                        var port = _nextPort++;
                        _servicePorts[port] = msg.GetString("Service") ?? "";
                        reply["Port"] = (long)port;
                        reply["Service"] = msg.GetString("Service") ?? "";
                    }
                    break;
                default:
                    reply["Error"] = "InvalidRequest";
                    break;
            }
            await Wire.WritePlistAsync(s, reply, ct);
        }
    }

    private async Task ServeServiceAsync(string service, Stream s, CancellationToken ct)
    {
        lock (Log)
            Log.Add("service:" + service);
        switch (service)
        {
            case "com.apple.afc":
                await Media.ServeAsync(s, ct);
                return;
            case "com.apple.mobile.house_arrest":
            {
                var msg = await Wire.ReadPlistAsync(s, ct);
                var id = msg?.GetString("Identifier") ?? "";
                if (msg?.GetString("Command") != "VendDocuments" || !AppContainers.TryGetValue(id, out var container))
                {
                    await Wire.WritePlistAsync(s, new PlistDict { ["Error"] = "ApplicationLookupFailed" }, ct);
                    return;
                }
                await Wire.WritePlistAsync(s, new PlistDict { ["Status"] = "Complete" }, ct);
                await container.ServeAsync(s, ct);
                return;
            }
            case "com.apple.mobile.installation_proxy":
                await ServeInstallationProxyAsync(s, ct);
                return;
            case "com.apple.springboardservices":
                while (await Wire.ReadPlistAsync(s, ct) is { } msg)
                {
                    var id = msg.GetString("bundleId") ?? "";
                    await Wire.WritePlistAsync(s, Icons.TryGetValue(id, out var png)
                        ? new PlistDict { ["pngData"] = png }
                        : new PlistDict { ["pngData"] = Array.Empty<byte>() }, ct, binary: true);
                }
                return;
            case "com.apple.mobile.diagnostics_relay":
                while (await Wire.ReadPlistAsync(s, ct) is { } msg)
                {
                    var request = msg.GetString("Request") ?? "";
                    lock (Log)
                        Log.Add("diagnostics:" + request);
                    PlistDict reply = request switch
                    {
                        "IORegistry" when PowerSource is not null => new PlistDict
                        {
                            ["Status"] = "Success",
                            ["Diagnostics"] = new PlistDict { ["IORegistry"] = PowerSource },
                        },
                        "Restart" or "Shutdown" or "Goodbye" => new PlistDict { ["Status"] = "Success" },
                        _ => new PlistDict { ["Status"] = "UnknownRequest" },
                    };
                    await Wire.WritePlistAsync(s, reply, ct);
                }
                return;
        }
    }

    private async Task ServeInstallationProxyAsync(Stream s, CancellationToken ct)
    {
        while (await Wire.ReadPlistAsync(s, ct) is { } msg)
        {
            var command = msg.GetString("Command");
            lock (Log)
                Log.Add("instproxy:" + command);
            switch (command)
            {
                case "Browse":
                {
                    var type = msg.GetDict("ClientOptions")?.GetString("ApplicationType") ?? "Any";
                    var apps = Apps.Where(a => type == "Any" || a.GetString("ApplicationType") == type).ToList();
                    // Sent in batches of two, like the phone does in batches.
                    for (var i = 0; i < apps.Count; i += 2)
                    {
                        await Wire.WritePlistAsync(s, new PlistDict
                        {
                            ["Status"] = "BrowsingApplications",
                            ["CurrentIndex"] = (long)i,
                            ["CurrentAmount"] = (long)Math.Min(2, apps.Count - i),
                            ["Total"] = (long)apps.Count,
                            ["CurrentList"] = apps.Skip(i).Take(2).Cast<object>().ToList(),
                        }, ct);
                    }
                    await Wire.WritePlistAsync(s, new PlistDict { ["Status"] = "Complete" }, ct);
                    break;
                }
                case "Install":
                {
                    lock (Log)
                        Log.Add("install:" + msg.GetString("PackagePath"));
                    if (InstallError is not null)
                    {
                        await Wire.WritePlistAsync(s, new PlistDict { ["Error"] = InstallError, ["ErrorDescription"] = "Fake failure" }, ct);
                        break;
                    }
                    foreach (var (status, pct) in new[] { ("CreatingStagingDirectory", 5L), ("ExtractingPackage", 15L), ("InstallingApplication", 60L) })
                        await Wire.WritePlistAsync(s, new PlistDict { ["Status"] = status, ["PercentComplete"] = pct }, ct);
                    await Wire.WritePlistAsync(s, new PlistDict { ["Status"] = "Complete" }, ct);
                    break;
                }
                case "Uninstall":
                {
                    var id = msg.GetString("ApplicationIdentifier");
                    var app = Apps.FirstOrDefault(a => a.GetString("CFBundleIdentifier") == id);
                    if (app is null)
                    {
                        await Wire.WritePlistAsync(s, new PlistDict { ["Error"] = "APIInternalError" }, ct);
                        break;
                    }
                    Apps.Remove(app);
                    await Wire.WritePlistAsync(s, new PlistDict { ["Status"] = "RemovingApplication", ["PercentComplete"] = 50L }, ct);
                    await Wire.WritePlistAsync(s, new PlistDict { ["Status"] = "Complete" }, ct);
                    break;
                }
            }
        }
    }
}
