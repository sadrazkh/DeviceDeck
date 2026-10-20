using System.Text.Json.Serialization;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Core.Battery;

/// <summary>One reading of a phone's battery, taken when it connected (docs/bridge-contract.md, "Battery history").</summary>
/// <param name="HealthEstimated">
/// iPhone: the health is full-charge capacity over design capacity, which differs from the "Maximum Capacity" in iOS
/// Settings (Apple corrects it with data the phone doesn't share over USB). Android reports its own figure.
/// </param>
public sealed record BatterySnapshot(
    long Id,
    string DeviceId,
    string DeviceName,
    DevicePlatform Platform,
    string Model,
    string OsVersion,
    DateTimeOffset At,
    int? LevelPercent,
    bool IsCharging,
    int? HealthPercent,
    bool HealthEstimated,
    int? CycleCount,
    int? DesignCapacityMah,
    int? FullChargeCapacityMah,
    double? TemperatureCelsius)
{
    /// <summary>Nothing worth keeping: a phone that answered without any battery figure.</summary>
    [JsonIgnore]
    public bool IsEmpty => HealthPercent is null && CycleCount is null && FullChargeCapacityMah is null && LevelPercent is null;

    public static BatterySnapshot From(DeviceSummary device, BatteryInfo info, DateTimeOffset at) => new(
        0,
        device.Id,
        device.Name,
        device.Platform,
        device.MarketingName ?? device.Model,
        device.OsVersion,
        at,
        info.LevelPercent,
        info.IsCharging,
        info.HealthPercent,
        HealthEstimated: device.Platform == DevicePlatform.Ios,
        info.CycleCount,
        info.DesignCapacityMah,
        info.FullChargeCapacityMah,
        info.TemperatureCelsius);
}

/// <summary>A phone with recorded history, connected or not, named as it was at its latest snapshot.</summary>
public sealed record BatteryDevice(
    string DeviceId,
    string Name,
    DevicePlatform Platform,
    string Model,
    int Count,
    DateTimeOffset FirstAt,
    DateTimeOffset LastAt,
    BatterySnapshot Latest);
