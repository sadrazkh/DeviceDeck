using System.Text;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Toolbox;
using DeviceDeck.Ios.Plist;
using DeviceDeck.Ios.Protocol;
using DeviceDeck.Ios.Tests.Fakes;
using DeviceDeck.Ios.Toolbox;

namespace DeviceDeck.Ios.Tests;

/// <summary>The profile itself, checked with the plist reader the phone's other services use, and the MCInstall service's wire shape.</summary>
public class CellularProfileTests
{
    private static CancellationToken Ct => TestContext.Timeout();

    /// <summary>The Ipv6Profile site's test/golden/ipv6-mcinet.mobileconfig: both builders must give this profile.</summary>
    private const string SiteProfile = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
        	<key>PayloadType</key>
        	<string>Configuration</string>
        	<key>PayloadVersion</key>
        	<integer>1</integer>
        	<key>PayloadIdentifier</key>
        	<string>local.ipv6-profile.cellular</string>
        	<key>PayloadUUID</key>
        	<string>7F916E0C-8908-5501-A63A-C2B4E2B477E8</string>
        	<key>PayloadDisplayName</key>
        	<string>IPv6 (mcinet)</string>
        	<key>PayloadDescription</key>
        	<string>Allows IPv4 and IPv6 on the cellular APN mcinet. Remove this profile to go back to the phone's own settings.</string>
        	<key>PayloadRemovalDisallowed</key>
        	<false/>
        	<key>PayloadContent</key>
        	<array>
        		<dict>
        			<key>PayloadType</key>
        			<string>com.apple.cellular</string>
        			<key>PayloadVersion</key>
        			<integer>1</integer>
        			<key>PayloadIdentifier</key>
        			<string>local.ipv6-profile.cellular.apn</string>
        			<key>PayloadUUID</key>
        			<string>0676FD84-9F8E-54B3-8625-EE984E8CC36D</string>
        			<key>PayloadDisplayName</key>
        			<string>Cellular mcinet</string>
        			<key>PayloadDescription</key>
        			<string>APN mcinet: IPv4 and IPv6.</string>
        			<key>AttachAPN</key>
        			<dict>
        				<key>Name</key>
        				<string>mcinet</string>
        				<key>AllowedProtocolMask</key>
        				<integer>3</integer>
        			</dict>
        			<key>APNs</key>
        			<array>
        				<dict>
        					<key>Name</key>
        					<string>mcinet</string>
        					<key>AllowedProtocolMask</key>
        					<integer>3</integer>
        					<key>AllowedProtocolMaskInRoaming</key>
        					<integer>3</integer>
        					<key>AllowedProtocolMaskInDomesticRoaming</key>
        					<integer>3</integer>
        				</dict>
        			</array>
        		</dict>
        	</array>
        </dict>
        </plist>
        """;

    private static PlistDict PayloadOf(byte[] profile) => (PlistDict)PlistSerializer.ParseDict(profile).GetArray("PayloadContent")![0];

    [Fact]
    public void The_app_and_the_site_build_the_same_profile()
    {
        var ours = PlistSerializer.ParseDict(CellularProfile.Build(new CellularProfileOptions("mcinet")));
        var site = PlistSerializer.ParseDict(Encoding.UTF8.GetBytes(SiteProfile));
        Assert.Equal(Encoding.UTF8.GetString(PlistSerializer.ToXml(site)), Encoding.UTF8.GetString(PlistSerializer.ToXml(ours)));
    }

    [Fact]
    public void Ipv6_only_sets_every_mask_to_2()
    {
        var payload = PayloadOf(CellularProfile.Build(new CellularProfileOptions("mtnirancell", CellularProfileMode.Ipv6)));
        var item = (PlistDict)payload.GetArray("APNs")![0];
        Assert.Equal("mtnirancell", item.GetString("Name"));
        Assert.Equal(2, item.GetInt("AllowedProtocolMask"));
        Assert.Equal(2, item.GetInt("AllowedProtocolMaskInRoaming"));
        Assert.Equal(2, item.GetInt("AllowedProtocolMaskInDomesticRoaming"));
        Assert.Equal(2, payload.GetDict("AttachAPN")!.GetInt("AllowedProtocolMask"));
    }

    [Fact]
    public void The_attach_apn_can_be_left_out()
    {
        var payload = PayloadOf(CellularProfile.Build(new CellularProfileOptions("rightel", AttachApn: false)));
        Assert.Null(payload.GetDict("AttachAPN"));
        Assert.Single(payload.GetArray("APNs")!);
    }

    [Fact]
    public void Xlat464_applies_only_to_ipv6_only()
    {
        PlistDict Item(CellularProfileMode mode) => (PlistDict)PayloadOf(CellularProfile.Build(new CellularProfileOptions("mcinet", mode, Xlat464: true))).GetArray("APNs")![0];
        Assert.True(Item(CellularProfileMode.Ipv6).GetBool("EnableXLAT464"));
        Assert.Null(Item(CellularProfileMode.Both).GetBool("EnableXLAT464"));
    }

    [Fact]
    public void Other_choices_get_other_uuids_and_the_same_identifier()
    {
        var one = PlistSerializer.ParseDict(CellularProfile.Build(new CellularProfileOptions("mcinet")));
        var two = PlistSerializer.ParseDict(CellularProfile.Build(new CellularProfileOptions("rightel")));
        Assert.NotEqual(one.GetString("PayloadUUID"), two.GetString("PayloadUUID"));
        Assert.Equal(CellularProfile.Identifier, one.GetString("PayloadIdentifier"));
        Assert.Equal(CellularProfile.Identifier, two.GetString("PayloadIdentifier"));
        Assert.Equal(CellularProfile.Build(new CellularProfileOptions("mcinet")), CellularProfile.Build(new CellularProfileOptions("mcinet")));
    }

    [Theory]
    [InlineData("my-apn.net", true)]
    [InlineData("a", true)]
    [InlineData("under_score", true)]
    [InlineData("", false)]
    [InlineData("a b", false)]
    [InlineData("<x>", false)]
    [InlineData("a&b", false)]
    [InlineData("-a", false)]
    [InlineData("a-", false)]
    public void Apn_names(string apn, bool valid)
    {
        Assert.Equal(valid, CellularProfile.IsValidApn(apn));
        if (!valid)
        {
            Assert.Throws<ArgumentException>(() => CellularProfile.Build(new CellularProfileOptions(apn)));
        }
    }

    [Fact]
    public void A_very_long_apn_is_refused() => Assert.False(CellularProfile.IsValidApn(new string('x', 101)));

    [Fact]
    public async Task Sending_puts_the_profile_in_an_InstallProfile_request()
    {
        var (client, server) = DuplexStream.CreatePair();
        await using var connection = new PlistConnection(client);
        var profile = CellularProfile.Build(new CellularProfileOptions("mcinet"));

        var send = McInstall.SendAsync(connection, profile, Ct);
        var request = await Wire.ReadPlistAsync(server, Ct);
        await Wire.WritePlistAsync(server, new PlistDict { ["Status"] = "Acknowledged" }, Ct);
        await send;

        Assert.Equal("InstallProfile", request!.GetString("RequestType"));
        Assert.Equal(profile, request!.GetData("Payload"));
    }

    [Fact]
    public async Task A_refusal_says_the_phones_reason()
    {
        var (client, server) = DuplexStream.CreatePair();
        await using var connection = new PlistConnection(client);

        var send = McInstall.SendAsync(connection, [1, 2, 3], Ct);
        await Wire.ReadPlistAsync(server, Ct);
        await Wire.WritePlistAsync(server, new PlistDict
        {
            ["Status"] = "Error",
            ["ErrorChain"] = new List<object> { new PlistDict { ["LocalizedDescription"] = "The profile is not valid.", ["ErrorCode"] = 1000L } },
        }, Ct);

        var error = await Assert.ThrowsAsync<DeviceException>(() => send);
        Assert.Equal(DeviceErrorCodes.InstallFailed, error.Code);
        Assert.Equal("The profile is not valid.", error.Message);
    }

    [Fact]
    public async Task The_list_comes_in_the_phones_order_with_names()
    {
        var (client, server) = DuplexStream.CreatePair();
        await using var connection = new PlistConnection(client);

        var list = McInstall.ListAsync(connection, Ct);
        var request = await Wire.ReadPlistAsync(server, Ct);
        await Wire.WritePlistAsync(server, new PlistDict
        {
            ["Status"] = "Acknowledged",
            ["OrderedIdentifiers"] = new List<object> { "b.profile", "a.profile" },
            ["ProfileMetadata"] = new PlistDict
            {
                ["a.profile"] = new PlistDict { ["PayloadDisplayName"] = "First" },
                ["b.profile"] = new PlistDict { ["PayloadDisplayName"] = "Second", ["PayloadOrganization"] = "Org" },
                ["c.profile"] = new PlistDict(),
            },
        }, Ct, binary: true);

        Assert.Equal("GetProfileList", request!.GetString("RequestType"));
        Assert.Equal(
            [new InstalledProfile("b.profile", "Second", "Org"), new InstalledProfile("a.profile", "First", null), new InstalledProfile("c.profile", "c.profile", null)],
            await list);
    }

    [Fact]
    public async Task An_empty_phone_has_an_empty_list()
    {
        var (client, server) = DuplexStream.CreatePair();
        await using var connection = new PlistConnection(client);

        var list = McInstall.ListAsync(connection, Ct);
        await Wire.ReadPlistAsync(server, Ct);
        await Wire.WritePlistAsync(server, new PlistDict { ["Status"] = "Acknowledged" }, Ct);

        Assert.Empty(await list);
    }
}
