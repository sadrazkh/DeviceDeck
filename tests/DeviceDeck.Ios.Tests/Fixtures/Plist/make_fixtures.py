"""Regenerates the plist fixtures with CPython's plistlib, an implementation independent of ours.

Run from this folder:  python -I make_fixtures.py
Values are made up but follow the keys lockdown returns for GetValue with no domain.
"""
import datetime
import plistlib

mixed = {
    "String": "hello",
    "Unicode": "سلام \u2014 iPhone",
    "Empty": "",
    "Spaces": "   ",
    "Small": 7,
    "Byte": 255,
    "Short": 65535,
    "Int32": 4294967295,
    "Int64": 9007199254740993,
    "Negative": -42,
    "Unsigned64": 18446744073709551615,
    "Real": 3.25,
    "True": True,
    "False": False,
    "Date": datetime.datetime(2026, 10, 20, 8, 30, 15),
    "Data": bytes(range(0, 40)),
    "Array": [1, "two", [3.5], {"four": 4}],
    "Nested": {"Inner": {"Deep": ["x"] * 20}},
    "LongArray": list(range(0, 20)),
}

lockdown_all = {
    "ActivationState": "Activated",
    "BasebandVersion": "2.40.01",
    "BluetoothAddress": "a4:c3:61:00:11:22",
    "BuildVersion": "22A3354",
    "CPUArchitecture": "arm64e",
    "CarrierBundleInfoArray": [
        {
            "CFBundleIdentifier": "com.apple.Irancell_ir",
            "CFBundleVersion": "60.0",
            "IntegratedCircuitCardIdentity": "8998000000000000000",
            "InternationalMobileSubscriberIdentity": "432350000000000",
            "MCC": "432",
            "MNC": "35",
        }
    ],
    "DeviceClass": "iPhone",
    "DeviceColor": "1",
    "DeviceEnclosureColor": "#e3e4df",
    "DeviceName": "Test iPhone",
    "HardwareModel": "D37AP",
    "InternationalMobileEquipmentIdentity": "356000000000001",
    "InternationalMobileEquipmentIdentity2": "356000000000019",
    "ModelNumber": "MYE73",
    "PasswordProtected": True,
    "PhoneNumber": "+98 912 000 0000",
    "ProductName": "iPhone OS",
    "ProductType": "iPhone17,3",
    "ProductVersion": "18.0",
    "RegionInfo": "ZD/A",
    "SerialNumber": "F2LXK000N70F",
    "TimeZone": "Asia/Tehran",
    "UniqueDeviceID": "00008140-000A1B2C3D4E801E",
    "WiFiAddress": "a4:c3:61:00:11:23",
}

for name, value in [("mixed", mixed), ("lockdown_getvalue_all", lockdown_all)]:
    with open(name + ".bplist", "wb") as f:
        plistlib.dump(value, f, fmt=plistlib.FMT_BINARY, sort_keys=True)
    with open(name + ".xml", "wb") as f:
        plistlib.dump(value, f, fmt=plistlib.FMT_XML, sort_keys=True)

# NSKeyedArchiver-style UIDs only exist in binary plists.
with open("uid.bplist", "wb") as f:
    plistlib.dump({"$top": {"root": plistlib.UID(1)}, "$objects": ["$null", "value"]}, f, fmt=plistlib.FMT_BINARY)

# lockdown GetValue Domain=com.apple.disk_usage (keys as lockdown names them; values for a 256 GB phone).
disk_usage = {
    "AmountDataAvailable": 98_000_000_000,
    "AmountDataReserved": 209_715_200,
    "CalculateDiskUsage": "OkilyDokily",
    "CameraUsage": 41_000_000_000,
    "MediaCacheUsage": 1_500_000_000,
    "MobileApplicationUsage": 62_000_000_000,
    "PhotoUsage": 3_000_000_000,
    "TotalDataAvailable": 98_209_715_200,
    "TotalDataCapacity": 245_000_000_000,
    "TotalDiskCapacity": 256_000_000_000,
    "TotalSystemAvailable": 1_000_000_000,
    "TotalSystemCapacity": 11_000_000_000,
    "VoicemailUsage": 0,
    "WebAppCacheUsage": 0,
}

# lockdown GetValue Domain=com.apple.mobile.battery.
battery_domain = {
    "BatteryCurrentCapacity": 76,
    "BatteryIsCharging": True,
    "ExternalChargeCapable": True,
    "ExternalConnected": True,
    "FullyCharged": False,
    "GasGaugeCapability": True,
    "HasBattery": True,
}

# diagnostics_relay IORegistry EntryClass=IOPMPowerSource, the "IORegistry" dictionary.
ioreg_powersource = {
    "AppleRawCurrentCapacity": 2650,
    "AppleRawMaxCapacity": 3279,
    "BatteryData": {"CycleCount": 412, "DesignCapacity": 3561},
    "CurrentCapacity": 76,
    "CycleCount": 412,
    "DesignCapacity": 3561,
    "ExternalConnected": True,
    "FullyCharged": False,
    "IsCharging": True,
    "MaxCapacity": 100,
    "NominalChargeCapacity": 3301,
    "Temperature": 3050,
    "Voltage": 4012,
}

for name, value in [("disk_usage", disk_usage), ("battery_domain", battery_domain), ("ioreg_powersource", ioreg_powersource)]:
    with open(name + ".xml", "wb") as f:
        plistlib.dump(value, f, fmt=plistlib.FMT_XML, sort_keys=True)

# iOS 27 (iPhone 16), as seen on a real phone; sizes rounded. CameraUsage and PhotoUsage carry the same
# figure, there is no MobileApplicationUsage, and TotalDataAvailable is not the free space.
disk_usage_ios27 = {
    "AmountDataAvailable": 4_300_000_000,
    "AmountDataReserved": 209_715_200,
    "AmountRestoreAvailable": 13_300_000_000,
    "CalculateDiskUsage": "OkilyDokily",
    "CalendarUsage": 2_000_000,
    "CameraUsage": 26_000_000_000,
    "MediaCacheUsage": 4096,
    "NotesUsage": 0,
    "PhotoUsage": 26_000_000_000,
    "TotalDataAvailable": 87_600_000_000,
    "TotalDataCapacity": 118_600_000_000,
    "TotalDiskCapacity": 128_000_000_000,
    "TotalSystemAvailable": 0,
    "TotalSystemCapacity": 8_800_000_000,
    "VoicemailUsage": 0,
    "WebAppCacheUsage": 0,
}

# iOS 27 IOPMPowerSource: no Temperature, no AppleRawMaxCapacity; capacities only inside BatteryData.
ioreg_powersource_ios27 = {
    "BatteryData": {
        "CurrentCapacity": 88,
        "DesignCapacity": 3521,
        "FullChargeCapacity": 3321,
        "MaxCapacity": 100,
        "NominalChargeCapacity": 3243,
    },
    "CurrentCapacity": 88,
    "CycleCount": 617,
    "ExternalConnected": True,
    "FullyCharged": False,
    "IsCharging": False,
    "MaxCapacity": 100,
    "Voltage": 4256,
}

carrier_unknown_dual_sim = {
    "CarrierBundleInfoArray": [
        {"CFBundleIdentifier": "com.apple.Unknown", "CFBundleVersion": "72.0", "MCC": "432", "MNC": "11", "Slot": "kOne"},
        {"CFBundleIdentifier": "com.apple.Unknown", "CFBundleVersion": "72.0", "MCC": "432", "MNC": "11", "Slot": "kTwo"},
    ],
    "DeviceColor": "1",
    "DeviceEnclosureColor": "18",
    "ProductType": "iPhone17,3",
}

for name, value in [("disk_usage_ios27", disk_usage_ios27), ("ioreg_powersource_ios27", ioreg_powersource_ios27),
                    ("carrier_unknown_dual_sim", carrier_unknown_dual_sim)]:
    with open(name + ".xml", "wb") as f:
        plistlib.dump(value, f, fmt=plistlib.FMT_XML, sort_keys=True)
