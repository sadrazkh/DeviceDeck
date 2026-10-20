using System.Diagnostics;
using System.Text;
using AndroidProbe;
using DeviceDeck.Android;
using DeviceDeck.Core.Abstractions;

Console.OutputEncoding = Encoding.UTF8;
var cli = new CommandLine(args);
if (cli.Command is null or "help" or "--help" or "-h")
{
    Commands.PrintHelp();
    return 0;
}

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

var options = new AndroidProviderOptions
{
    AdbPath = cli.Option("adb"),
    ServerPort = int.TryParse(cli.Option("port"), out int port) ? port : AndroidProviderOptions.DefaultServerPort,
    CacheDirectory = cli.Flag("no-cache") ? null : new AndroidProviderOptions().CacheDirectory,
};

try
{
    await using var provider = new AndroidDeviceProvider(options);
    var commands = new Commands(provider, cli, cancel.Token);
    return await commands.RunAsync();
}
catch (DeviceException ex)
{
    Console.Error.WriteLine($"error [{ex.Code}]: {ex.Message}");
    if (cli.Flag("verbose") && ex.InnerException != null)
    {
        Console.Error.WriteLine(ex.InnerException);
    }
    return 2;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("cancelled");
    return 130;
}
catch (UsageException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine("Run 'androidprobe help' for the commands.");
    return 64;
}

namespace AndroidProbe
{
    internal sealed class UsageException(string message) : Exception(message);

    /// <summary>"command positional... --name value --flag".</summary>
    internal sealed class CommandLine
    {
        private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

        public CommandLine(string[] args)
        {
            var positional = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    string name = args[i][2..];
                    string? value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) && IsValued(name) ? args[++i] : null;
                    _options[name] = value;
                }
                else
                {
                    positional.Add(args[i]);
                }
            }
            Command = positional.Count > 0 ? positional[0].ToLowerInvariant() : null;
            Arguments = positional.Skip(1).ToList();
        }

        public string? Command { get; }

        public IReadOnlyList<string> Arguments { get; }

        private static bool IsValued(string name) => name is "serial" or "adb" or "port" or "root" or "limit" or "album" or "size" or "offset";

        public string? Option(string name) => _options.TryGetValue(name, out string? value) ? value : null;

        public bool Flag(string name) => _options.ContainsKey(name);

        public string Argument(int index, string what) =>
            index < Arguments.Count ? Arguments[index] : throw new UsageException($"Missing {what}.");
    }

    internal static class Format
    {
        public static string Bytes(long bytes)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            double value = bytes;
            int unit = 0;
            while (value >= 1000 && unit < units.Length - 1)
            {
                value /= 1000;
                unit++;
            }
            return unit == 0 ? $"{bytes} B" : $"{value:0.0} {units[unit]}";
        }

        public static string Elapsed(Stopwatch watch) => $"{watch.Elapsed.TotalSeconds:0.0} s";
    }
}
