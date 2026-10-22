using DeviceDeck.Shell.Hosting;

namespace DeviceDeck.Shell.Tests;

/// <summary>The only links the app opens outside itself (Drivers card, v0.6): web pages, mail and a Store product page.</summary>
public sealed class ShellLinksTests
{
    [Theory]
    [InlineData("https://www.apple.com/itunes/")]
    [InlineData("http://example.com/")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("ms-windows-store://pdp/?ProductId=9NP83LWLPZ9K")]
    public void Web_pages_mail_and_store_product_pages_open(string link) =>
        Assert.True(ShellLinks.IsAllowed(new Uri(link)));

    [Theory]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("ms-settings:privacy")]
    [InlineData("ms-windows-store://home")]
    [InlineData("ms-windows-store://pdp/?ProductId=9NP83LWLPZ9K&cid=x")]
    [InlineData("ms-windows-store://pdp/extra?ProductId=9NP83LWLPZ9K")]
    [InlineData("ms-windows-store://downloadsandupdates")]
    [InlineData("search-ms:query=adb")]
    [InlineData("javascript:alert(1)")]
    public void Anything_else_is_refused(string link) =>
        Assert.False(ShellLinks.IsAllowed(new Uri(link)));

    [Fact]
    public void A_refused_link_is_not_opened() =>
        Assert.False(ShellLinks.Open("file:///C:/Windows/System32/cmd.exe"));
}
