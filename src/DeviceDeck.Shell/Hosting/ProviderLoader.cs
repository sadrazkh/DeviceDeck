using System.Reflection;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Fake;

namespace DeviceDeck.Shell.Hosting;

/// <summary>
/// Picks the device providers from DEVICEDECK_PROVIDERS: "fake" (default), "real" (iPhone + Android),
/// "ios", "android", or a '+'-joined mix such as "real+fake". Real providers are found by reflection in
/// DeviceDeck.Ios.dll and DeviceDeck.Android.dll: every public IDeviceProvider with a constructor whose
/// parameters all have defaults. If nothing loads, the fake phones are used so the app always opens.
/// </summary>
internal static class ProviderLoader
{
    public const string Variable = "DEVICEDECK_PROVIDERS";

    private static readonly Dictionary<string, string> RealAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ios"] = "DeviceDeck.Ios",
        ["android"] = "DeviceDeck.Android",
    };

    public static IReadOnlyList<IDeviceProvider> Load(FakeOptions fakeOptions, out FakeDeviceProvider? fake)
    {
        var setting = Environment.GetEnvironmentVariable(Variable);
        var wanted = (string.IsNullOrWhiteSpace(setting) ? "fake" : setting)
            .Split(['+', ',', ' '], StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(part => part.Equals("real", StringComparison.OrdinalIgnoreCase) || part.Equals("all", StringComparison.OrdinalIgnoreCase)
                ? part.Equals("all", StringComparison.OrdinalIgnoreCase) ? ["ios", "android", "fake"] : ["ios", "android"]
                : new[] { part.ToLowerInvariant() })
            .Distinct()
            .ToList();

        var providers = new List<IDeviceProvider>();
        foreach (var name in wanted.Where(RealAssemblies.ContainsKey))
        {
            providers.AddRange(LoadFrom(RealAssemblies[name]));
        }

        fake = null;
        if (wanted.Contains("fake") || providers.Count == 0)
        {
            if (!wanted.Contains("fake"))
            {
                ShellLog.Info($"No real provider loaded for {Variable}={setting}; using the sample phones.");
            }

            fake = new FakeDeviceProvider(fakeOptions);
            providers.Add(fake);
        }

        ShellLog.Info("Providers: " + string.Join(", ", providers.Select(p => $"{p.Id} ({p.GetType().FullName})")));
        return providers;
    }

    private static IEnumerable<IDeviceProvider> LoadFrom(string assemblyName)
    {
        Assembly assembly;
        try
        {
            assembly = Assembly.Load(new AssemblyName(assemblyName));
        }
        catch (Exception ex)
        {
            ShellLog.Error($"Could not load {assemblyName}.", ex);
            yield break;
        }

        var types = assembly.GetExportedTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IDeviceProvider).IsAssignableFrom(t));
        foreach (var type in types)
        {
            var ctor = type.GetConstructors().FirstOrDefault(c => c.GetParameters().All(p => p.HasDefaultValue));
            if (ctor is null)
            {
                ShellLog.Info($"{type.FullName} has no constructor usable without arguments; skipped.");
                continue;
            }

            IDeviceProvider? provider = null;
            try
            {
                provider = (IDeviceProvider)ctor.Invoke(ctor.GetParameters().Select(p => p.DefaultValue).ToArray());
            }
            catch (Exception ex)
            {
                ShellLog.Error($"Could not create {type.FullName}.", ex);
            }

            if (provider is not null)
            {
                yield return provider;
            }
        }
    }
}
