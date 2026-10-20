using DeviceDeck.Android.Apk;
using SkiaSharp;

namespace DeviceDeck.Android.Tests.Infrastructure;

/// <summary>Small APKs built in memory with real Android binary formats.</summary>
internal static class TestApks
{
    public static byte[] Png(int size, SKColor color)
    {
        using var bitmap = new SKBitmap(size, size);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static byte[] Jpeg(int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    public static TestElement Manifest(string package, int versionCode, string versionName, TestAttr? label, TestAttr? icon, bool launcherLabel = false)
    {
        var applicationAttributes = new List<TestAttr>();
        if (label != null && !launcherLabel) applicationAttributes.Add(label);
        if (icon != null) applicationAttributes.Add(icon);
        var activityAttributes = new List<TestAttr> { TestAttr.String("name", AndroidAttr.Name, ".MainActivity") };
        if (label != null && launcherLabel) activityAttributes.Add(label);

        var activity = new TestElement("activity", activityAttributes,
        [
            new TestElement("intent-filter", [],
            [
                new TestElement("action", TestAttr.String("name", AndroidAttr.Name, "android.intent.action.MAIN")),
                new TestElement("category", TestAttr.String("name", AndroidAttr.Name, "android.intent.category.LAUNCHER")),
            ]),
        ]);
        return new TestElement("manifest",
        [
            TestAttr.Int("versionCode", AndroidAttr.VersionCode, versionCode),
            TestAttr.String("versionName", AndroidAttr.VersionName, versionName),
            TestAttr.String("package", 0, package),
        ],
        [
            new TestElement("uses-sdk", TestAttr.Int("minSdkVersion", 0x0101020c, 24)),
            new TestElement("application", applicationAttributes, [activity]),
        ]);
    }

    /// <summary>Label "Test App" (plus a Persian translation), icon = red mdpi PNG and green xxxhdpi PNG.</summary>
    public static byte[] BitmapIcon(ArscLayout layout = ArscLayout.Dense, bool compact = false, bool utf8 = true, string package = "com.test.app")
    {
        var arsc = new ArscWriter { Layout = layout, Compact = compact, Utf8Pool = utf8 };
        uint appName = arsc.Id("string", 3);
        uint icon = arsc.Id("mipmap", 0);
        arsc.String(appName, "برنامه آزمایشی", language: "fa");
        arsc.String(appName, "Test App");
        arsc.File(icon, "res/mipmap-mdpi-v4/ic_launcher.png", density: 160);
        arsc.File(icon, "res/mipmap-xxxhdpi-v4/ic_launcher.png", density: 640);

        var manifest = Manifest(package, 42, "1.2.3",
            TestAttr.Reference("label", AndroidAttr.Label, appName),
            TestAttr.Reference("icon", AndroidAttr.Icon, icon));
        return ApkZip.Build(new Dictionary<string, byte[]>
        {
            ["AndroidManifest.xml"] = AxmlWriter.Write(manifest),
            ["classes.dex"] = new byte[1000],
            ["res/mipmap-mdpi-v4/ic_launcher.png"] = Png(48, SKColors.Red),
            ["res/mipmap-xxxhdpi-v4/ic_launcher.png"] = Png(192, new SKColor(0, 200, 0)),
            ["resources.arsc"] = arsc.Build(),
        });
    }

    /// <summary>Adaptive icon: blue colour background, vector foreground with a yellow square in the middle third.</summary>
    public static byte[] AdaptiveIcon()
    {
        var arsc = new ArscWriter();
        uint appName = arsc.Id("string", 0);
        uint icon = arsc.Id("mipmap", 0);
        uint background = arsc.Id("color", 0);
        uint foreground = arsc.Id("drawable", 0);
        arsc.String(appName, "Adaptive");
        arsc.File(icon, "res/mipmap-anydpi-v26/ic_launcher.xml", density: ResConfig.DensityAny, sdk: 26);
        arsc.File(icon, "res/mipmap-mdpi-v4/ic_launcher.png", density: 160);
        arsc.Color(background, 0xFF0000FF);
        arsc.File(foreground, "res/drawable/ic_launcher_foreground.xml");

        var adaptive = new TestElement("adaptive-icon", [],
        [
            new TestElement("background", TestAttr.Reference("drawable", AndroidAttr.Drawable, background)),
            new TestElement("foreground", TestAttr.Reference("drawable", AndroidAttr.Drawable, foreground)),
        ]);
        var vector = new TestElement("vector", [
            TestAttr.Float("viewportWidth", AndroidAttr.ViewportWidth, 108),
            TestAttr.Float("viewportHeight", AndroidAttr.ViewportHeight, 108),
        ],
        [
            new TestElement("group", [TestAttr.Float("translateX", AndroidAttr.TranslateX, 0)],
            [
                new TestElement("path",
                    TestAttr.Color("fillColor", AndroidAttr.FillColor, 0xFFFFFF00),
                    TestAttr.String("pathData", AndroidAttr.PathData, "M36,36h36v36h-36z")),
            ]),
        ]);
        var manifest = Manifest("com.test.adaptive", 7, "7.0",
            TestAttr.Reference("label", AndroidAttr.Label, appName),
            TestAttr.Reference("icon", AndroidAttr.Icon, icon));
        return ApkZip.Build(new Dictionary<string, byte[]>
        {
            ["AndroidManifest.xml"] = AxmlWriter.Write(manifest),
            ["res/mipmap-anydpi-v26/ic_launcher.xml"] = AxmlWriter.Write(adaptive),
            ["res/drawable/ic_launcher_foreground.xml"] = AxmlWriter.Write(vector),
            ["res/mipmap-mdpi-v4/ic_launcher.png"] = Png(48, SKColors.Red),
            ["resources.arsc"] = arsc.Build(),
        });
    }
}
