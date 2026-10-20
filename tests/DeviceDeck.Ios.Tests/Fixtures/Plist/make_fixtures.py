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
