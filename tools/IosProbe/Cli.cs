namespace IosProbe;

/// <summary>Parsed command line: a command, positional arguments and --options.</summary>
internal sealed class Cli
{
    public const string Usage = """
        iosprobe — talks to an iPhone through Apple Mobile Device Service (usbmux on 127.0.0.1:27015).

        usage: iosprobe <command> [arguments] [--udid <UDID>] [--timeout <seconds>]

        Setup
          doctor                              check usbmux, Apple Mobile Device Service and pair records (no phone needed)
          list                                phones usbmux sees, with their state
          watch                               print attach / trust / lock / detach events until Ctrl+C

        Device
          info [--reveal]                     summary and detail rows (serial / IMEI masked unless --reveal)
          storage                             capacity, free space and categories
          battery                             level, health, cycles, capacities, temperature

        Files (root = media, or app:<bundleId>)
          roots                               the file roots: media plus each app that shares documents
          ls <root> <path>                    list a folder
          pull <root> <path> <dest>           copy a file off the phone (dest = file or folder)
          push <root> <local-file> <path>     copy a file onto the phone (path = file or existing folder)
          mkdir <root> <path>                 create a folder
          rm <root> <path> --yes [--recursive]   delete a file (or a folder with --recursive)
          docs <bundleId> [path]              list an app's Documents (house_arrest)

        Apps
          apps [--system]                     installed apps with sizes and the file-sharing flag
          icon <bundleId> <out.png>           save an app's home-screen icon
          install <app.ipa>                   upload to PublicStaging and install
          uninstall <bundleId> --yes          remove an app

        Photos (read-only)
          photos [--limit 50] [--album <id>]  index source, albums and the newest assets
          thumb <assetId> <out.jpg> [--size 256]   the phone's own thumbnail, if it has one
          photo-pull <assetId> <dest>         copy an original off the phone

        Diagnostics
          values <domain|-> [--key K]         raw lockdown values of a domain ('-' = top; e.g. com.apple.disk_usage)
          carrier                             the SIMs' carrier bundles (no ICCID / IMSI) and the mapped name
          ioreg <entry-class>                 raw IORegistry properties (e.g. IOPMPowerSource)
          diag <request>                      a diagnostics_relay report (e.g. GasGauge)
          afcinfo                             AFC's view of the Media volume (FSTotalBytes, FSFreeBytes)
          bench-storage [--browse]            time each source of the storage categories
          bench-overview                      storage while apps, photo index and icons load, as the host does

        Power
          reboot --yes                        restart the phone
          shutdown --yes                      shut the phone down

        Exit codes: 0 ok, 2 device error (the code is printed, e.g. error [trust_required]), 64 usage, 1 internal.
        Set DEVICEDECK_IOS_TLS=1.2 to force TLS 1.2 if a phone's secure session fails.
        """;

    private Cli(string command, List<string> positional, Dictionary<string, string?> options)
    {
        Command = command;
        Positional = positional;
        Options = options;
    }

    public string Command { get; }
    public IReadOnlyList<string> Positional { get; }
    public IReadOnlyDictionary<string, string?> Options { get; }

    private static readonly HashSet<string> Flags = ["reveal", "system", "yes", "recursive", "browse"];

    public static Cli Parse(string[] args)
    {
        var positional = new List<string>();
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                var name = a[2..];
                if (Flags.Contains(name))
                    options[name] = null;
                else if (i + 1 < args.Length)
                    options[name] = args[++i];
                else
                    throw new CliException($"--{name} needs a value.");
            }
            else
            {
                positional.Add(a);
            }
        }
        return new Cli(args[0].ToLowerInvariant(), positional, options);
    }

    public string Arg(int index, string name) =>
        index < Positional.Count ? Positional[index] : throw new CliException($"'{Command}' needs <{name}>.");

    public string? OptionalArg(int index) => index < Positional.Count ? Positional[index] : null;

    public bool Has(string flag) => Options.ContainsKey(flag);

    public string? Option(string name) => Options.TryGetValue(name, out var v) ? v : null;

    public int IntOption(string name, int fallback) =>
        Option(name) is { } s ? int.TryParse(s, out var v) ? v : throw new CliException($"--{name} must be a number.") : fallback;
}

internal sealed class CliException(string message) : Exception(message);
