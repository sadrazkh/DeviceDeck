namespace DeviceDeck.Core.Abstractions;

/// <summary>
/// Optional: a device whose name can be changed from the PC (device.rename). A device that implements it also
/// reports <see cref="DeviceCapability.Rename"/>. iPhone: lockdown's DeviceName. Android: Settings › About phone › Device name.
/// </summary>
public interface IDeviceRenamer
{
    /// <summary>Renames the phone and raises DeviceChanged with the new <see cref="DeviceSummary.Name"/> once the phone has it.</summary>
    Task RenameAsync(string name, CancellationToken ct);
}

public static class DeviceNames
{
    /// <summary>iOS and Android both cut names at about this length; longer ones are refused before they reach the phone.</summary>
    public const int MaxLength = 64;

    /// <summary>The trimmed name, or <see cref="ArgumentException"/> when it is empty, too long or has control characters.</summary>
    public static string Validate(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("The name can't be empty.");
        }

        if (trimmed.Length > MaxLength)
        {
            throw new ArgumentException($"The name can be at most {MaxLength} characters.");
        }

        if (trimmed.Any(char.IsControl))
        {
            throw new ArgumentException("The name can't contain control characters.");
        }

        return trimmed;
    }
}
