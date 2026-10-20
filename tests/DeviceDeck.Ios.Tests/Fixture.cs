namespace DeviceDeck.Ios.Tests;

internal static class Fixture
{
    public static string PathOf(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "Fixtures", .. parts]);

    public static byte[] Bytes(params string[] parts) => File.ReadAllBytes(PathOf(parts));

    public static string Text(params string[] parts) => File.ReadAllText(PathOf(parts));
}
