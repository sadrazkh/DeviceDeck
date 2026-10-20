using System.Text;
using DeviceDeck.Core.Abstractions;
using IosProbe;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(Cli.Usage);
    return 0;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    var cli = Cli.Parse(args);
    return await Commands.RunAsync(cli, cts.Token);
}
catch (CliException ex)
{
    Console.Error.WriteLine($"error [usage]: {ex.Message}");
    Console.Error.WriteLine("Run 'iosprobe --help' for the commands.");
    return 64;
}
catch (DeviceException ex)
{
    Console.Error.WriteLine($"error [{ex.Code}]: {ex.Message}");
    if (ex.InnerException is { } inner)
        Console.Error.WriteLine($"  detail: {inner.GetType().Name}: {inner.Message}");
    return 2;
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    Console.Error.WriteLine("cancelled");
    return 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error [internal]: {ex.GetType().Name}: {ex.Message}");
    Console.Error.WriteLine(ex.StackTrace);
    return 1;
}
