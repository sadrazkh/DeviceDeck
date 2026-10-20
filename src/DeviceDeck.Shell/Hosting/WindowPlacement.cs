using DeviceDeck.Core.Settings;

namespace DeviceDeck.Shell.Hosting;

/// <summary>The window's last restored bounds (physical pixels, screen coordinates) and whether it was maximised.</summary>
internal sealed record WindowPlacement(int X, int Y, int Width, int Height, bool Maximized)
{
    public static WindowPlacement? Load(string path) => JsonFileStore.Read<WindowPlacement>(path);

    public void Save(string path)
    {
        try
        {
            JsonFileStore.Write(path, this);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShellLog.Error("Could not save the window position.", ex);
        }
    }
}
