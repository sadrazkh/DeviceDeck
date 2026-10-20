using SkiaSharp;

namespace DeviceDeck.Android.Apk;

/// <summary>
/// Draws an app icon from its APK resources with SkiaSharp: a PNG/WebP at the highest density, or an adaptive icon
/// (background + foreground, each a bitmap, colour, inset, layer list or vector drawable) masked to a rounded square.
/// Covers what launcher icons use in practice; anything else falls back to the best bitmap or gives null.
/// </summary>
internal sealed class IconRenderer(ZipReader zip, ResourceTable table)
{
    private const int MaxDepth = 8;
    private const int XhdpiDensity = 320;

    public async Task<byte[]?> RenderPngAsync(uint resourceId, int size, CancellationToken ct)
    {
        using var drawable = await LoadReferenceAsync(resourceId, 0, ct).ConfigureAwait(false);
        if (drawable == null)
        {
            return null;
        }
        var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(SKColors.Transparent);
        drawable.Draw(surface.Canvas, new SKRect(0, 0, size, size));
        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png?.ToArray();
    }

    private async Task<IconDrawable?> LoadReferenceAsync(uint resourceId, int depth, CancellationToken ct)
    {
        if (depth > MaxDepth)
        {
            return null;
        }
        var entries = table.Get(resourceId);
        if (entries.Count == 0)
        {
            return null;
        }
        var day = entries.Where(e => !e.Config.IsNight).ToList();
        if (day.Count > 0)
        {
            entries = day;
        }

        var bitmaps = entries.Where(e => IsBitmapPath(PathOf(e.Value))).OrderByDescending(e => EffectiveDensity(e.Config)).ToList();
        var xmls = entries.Where(e => PathOf(e.Value)?.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) == true)
            .OrderByDescending(e => e.Config.SdkVersion).ToList();

        // A pre-rendered launcher bitmap at xhdpi or better looks exactly as intended; prefer it.
        if (bitmaps.Count > 0 && EffectiveDensity(bitmaps[0].Config) >= XhdpiDensity)
        {
            return await LoadBitmapAsync(PathOf(bitmaps[0].Value)!, ct).ConfigureAwait(false);
        }
        foreach (var xml in xmls)
        {
            if (await LoadXmlAsync(PathOf(xml.Value)!, depth, ct).ConfigureAwait(false) is { } drawable)
            {
                return drawable;
            }
        }
        if (bitmaps.Count > 0)
        {
            return await LoadBitmapAsync(PathOf(bitmaps[0].Value)!, ct).ConfigureAwait(false);
        }
        var other = entries[0].Value;
        return await LoadValueAsync(other, depth + 1, ct).ConfigureAwait(false);
    }

    private async Task<IconDrawable?> LoadValueAsync(ResValue value, int depth, CancellationToken ct)
    {
        if (value.IsColor)
        {
            return new ColorDrawable(ToColor(value));
        }
        if (value.IsReference)
        {
            return await LoadReferenceAsync(value.Data, depth + 1, ct).ConfigureAwait(false);
        }
        return null;
    }

    private async Task<IconDrawable?> LoadAttributeAsync(XmlAttribute? attribute, int depth, CancellationToken ct) =>
        attribute == null ? null : await LoadValueAsync(attribute.Value, depth, ct).ConfigureAwait(false);

    private async Task<IconDrawable?> LoadBitmapAsync(string path, CancellationToken ct)
    {
        byte[]? bytes = await zip.ReadAsync(path, ct).ConfigureAwait(false);
        if (bytes == null)
        {
            return null;
        }
        var bitmap = SKBitmap.Decode(bytes);
        return bitmap == null ? null : new BitmapDrawable(bitmap);
    }

    private async Task<IconDrawable?> LoadXmlAsync(string path, int depth, CancellationToken ct)
    {
        byte[]? bytes = await zip.ReadAsync(path, ct).ConfigureAwait(false);
        if (bytes == null)
        {
            return null;
        }
        XmlElement root;
        try
        {
            root = BinaryXml.Parse(bytes);
        }
        catch (InvalidDataException)
        {
            return null;
        }
        return await LoadElementAsync(root, depth, ct).ConfigureAwait(false);
    }

    private async Task<IconDrawable?> LoadElementAsync(XmlElement element, int depth, CancellationToken ct)
    {
        if (depth > MaxDepth)
        {
            return null;
        }
        switch (element.Name)
        {
            case "adaptive-icon":
            {
                var background = await LayerAsync("background").ConfigureAwait(false);
                var foreground = await LayerAsync("foreground").ConfigureAwait(false);
                return background == null && foreground == null ? null : new AdaptiveIconDrawable(background, foreground);

                async Task<IconDrawable?> LayerAsync(string name)
                {
                    var layer = element.Children.FirstOrDefault(c => c.Name == name);
                    if (layer == null)
                    {
                        return null;
                    }
                    return await LoadAttributeAsync(layer.Attribute(AndroidAttr.Drawable, "drawable"), depth, ct).ConfigureAwait(false)
                        ?? (layer.Children.Count > 0 ? await LoadElementAsync(layer.Children[0], depth + 1, ct).ConfigureAwait(false) : null);
                }
            }
            case "bitmap":
            case "nine-patch":
                return await LoadAttributeAsync(element.Attribute(AndroidAttr.Src, "src"), depth, ct).ConfigureAwait(false);
            case "inset":
            {
                var inner = await LoadAttributeAsync(element.Attribute(AndroidAttr.Drawable, "drawable"), depth, ct).ConfigureAwait(false)
                    ?? (element.Children.Count > 0 ? await LoadElementAsync(element.Children[0], depth + 1, ct).ConfigureAwait(false) : null);
                if (inner == null)
                {
                    return null;
                }
                float all = InsetFraction(element.Attribute(AndroidAttr.Inset, "inset"));
                return new InsetDrawable(inner,
                    InsetFraction(element.Attribute(AndroidAttr.InsetLeft, "insetLeft"), all),
                    InsetFraction(element.Attribute(AndroidAttr.InsetTop, "insetTop"), all),
                    InsetFraction(element.Attribute(AndroidAttr.InsetRight, "insetRight"), all),
                    InsetFraction(element.Attribute(AndroidAttr.InsetBottom, "insetBottom"), all));
            }
            case "layer-list":
            {
                var layers = new List<IconDrawable>();
                foreach (var item in element.Children.Where(c => c.Name == "item"))
                {
                    var layer = await LoadAttributeAsync(item.Attribute(AndroidAttr.Drawable, "drawable"), depth, ct).ConfigureAwait(false)
                        ?? (item.Children.Count > 0 ? await LoadElementAsync(item.Children[0], depth + 1, ct).ConfigureAwait(false) : null);
                    if (layer != null)
                    {
                        layers.Add(layer);
                    }
                }
                return layers.Count == 0 ? null : new LayerDrawable(layers);
            }
            case "selector":
            case "level-list":
            {
                var item = element.Children.LastOrDefault(c => c.Name == "item");
                return item == null ? null : await LoadAttributeAsync(item.Attribute(AndroidAttr.Drawable, "drawable"), depth, ct).ConfigureAwait(false);
            }
            case "shape":
            {
                var solid = element.Children.FirstOrDefault(c => c.Name == "solid");
                var color = solid?.Attribute(AndroidAttr.Color, "color");
                return color == null ? null : await ResolveColorAsync(color.Value, depth, ct).ConfigureAwait(false) is { } c ? new ColorDrawable(c) : null;
            }
            case "color":
            {
                var color = element.Attribute(AndroidAttr.Color, "color");
                return color == null ? null : await ResolveColorAsync(color.Value, depth, ct).ConfigureAwait(false) is { } c ? new ColorDrawable(c) : null;
            }
            case "vector":
                return await VectorDrawable.LoadAsync(element, this, depth, ct).ConfigureAwait(false);
            default:
                return null;
        }
    }

    /// <summary>A colour value, a @color reference, or a colour state list / gradient file (first usable colour).</summary>
    internal async Task<SKColor?> ResolveColorAsync(ResValue value, int depth, CancellationToken ct)
    {
        if (value.IsColor)
        {
            return ToColor(value);
        }
        if (!value.IsReference || depth > MaxDepth)
        {
            return null;
        }
        var entries = table.Get(value.Data);
        var entry = entries.FirstOrDefault(e => !e.Config.IsNight) ?? entries.FirstOrDefault();
        if (entry == null)
        {
            return null;
        }
        if (entry.Value.IsColor || entry.Value.IsReference)
        {
            return await ResolveColorAsync(entry.Value, depth + 1, ct).ConfigureAwait(false);
        }
        if (PathOf(entry.Value) is { } path && path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
            && await zip.ReadAsync(path, ct).ConfigureAwait(false) is { } bytes)
        {
            var root = BinaryXml.Parse(bytes);
            var item = root.Name == "selector" ? root.Children.LastOrDefault(c => c.Name == "item") : null;
            var color = item?.Attribute(AndroidAttr.Color, "color") ?? root.Attribute(AndroidAttr.StartColor, "startColor");
            return color == null ? null : await ResolveColorAsync(color.Value, depth + 1, ct).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>The parsed root of a referenced XML file (a gradient used as a vector fill).</summary>
    internal async Task<XmlElement?> LoadReferencedXmlAsync(ResValue value, CancellationToken ct)
    {
        if (!value.IsReference)
        {
            return null;
        }
        var entry = table.Get(value.Data).FirstOrDefault(e => PathOf(e.Value)?.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) == true);
        if (entry == null || await zip.ReadAsync(PathOf(entry.Value)!, ct).ConfigureAwait(false) is not { } bytes)
        {
            return null;
        }
        try
        {
            return BinaryXml.Parse(bytes);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    internal static SKColor ToColor(ResValue value)
    {
        uint argb = value.Data;
        if (value.Type is ResValue.TypeColorRgb8 or ResValue.TypeColorRgb4)
        {
            argb |= 0xFF000000;
        }
        return new SKColor(argb);
    }

    private string? PathOf(ResValue value) => value.Type == ResValue.TypeString ? table.Strings.Get(value.Data) : null;

    private static bool IsBitmapPath(string? path) =>
        path != null && !path.EndsWith(".9.png", StringComparison.OrdinalIgnoreCase)
        && (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase));

    private static int EffectiveDensity(ResConfig config) => config.Density switch
    {
        0 or ResConfig.DensityNone => 160,
        ResConfig.DensityAny => 0,
        var d => d,
    };

    /// <summary>Inset as a share of the drawable: fractions as given, dp against the 108 dp adaptive-icon canvas.</summary>
    private static float InsetFraction(XmlAttribute? attribute, float fallback = 0f)
    {
        if (attribute == null)
        {
            return fallback;
        }
        var value = attribute.Value;
        float result = value.Type switch
        {
            ResValue.TypeFraction => value.AsFloat(),
            ResValue.TypeDimension => value.AsFloat() / 108f,
            _ => fallback,
        };
        return Math.Clamp(result, 0f, 0.45f);
    }
}

internal abstract class IconDrawable : IDisposable
{
    public abstract void Draw(SKCanvas canvas, SKRect bounds);

    public virtual void Dispose()
    {
    }
}

internal sealed class ColorDrawable(SKColor color) : IconDrawable
{
    public override void Draw(SKCanvas canvas, SKRect bounds)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawRect(bounds, paint);
    }
}

internal sealed class BitmapDrawable(SKBitmap bitmap) : IconDrawable
{
    private readonly SKImage _image = SKImage.FromBitmap(bitmap);

    public override void Draw(SKCanvas canvas, SKRect bounds) =>
        canvas.DrawImage(_image, bounds, new SKSamplingOptions(SKCubicResampler.Mitchell));

    public override void Dispose()
    {
        _image.Dispose();
        bitmap.Dispose();
    }
}

internal sealed class InsetDrawable(IconDrawable inner, float left, float top, float right, float bottom) : IconDrawable
{
    public override void Draw(SKCanvas canvas, SKRect bounds) =>
        inner.Draw(canvas, new SKRect(
            bounds.Left + bounds.Width * left,
            bounds.Top + bounds.Height * top,
            bounds.Right - bounds.Width * right,
            bounds.Bottom - bounds.Height * bottom));

    public override void Dispose() => inner.Dispose();
}

internal sealed class LayerDrawable(IReadOnlyList<IconDrawable> layers) : IconDrawable
{
    public override void Draw(SKCanvas canvas, SKRect bounds)
    {
        foreach (var layer in layers)
        {
            layer.Draw(canvas, bounds);
        }
    }

    public override void Dispose()
    {
        foreach (var layer in layers)
        {
            layer.Dispose();
        }
    }
}

/// <summary>
/// AdaptiveIconDrawable: both layers are 108 dp and the launcher shows the middle 72 dp through a mask, so each
/// layer is drawn 1.5x the output size, centred, and clipped to a rounded square.
/// </summary>
internal sealed class AdaptiveIconDrawable(IconDrawable? background, IconDrawable? foreground) : IconDrawable
{
    public override void Draw(SKCanvas canvas, SKRect bounds)
    {
        canvas.Save();
        float radius = bounds.Width * 0.23f;
        using (var mask = new SKPath())
        {
            mask.AddRoundRect(bounds, radius, radius);
            canvas.ClipPath(mask, SKClipOperation.Intersect, antialias: true);
        }
        var layerBounds = SKRect.Inflate(bounds, bounds.Width * 0.25f, bounds.Height * 0.25f);
        background?.Draw(canvas, layerBounds);
        foreground?.Draw(canvas, layerBounds);
        canvas.Restore();
    }

    public override void Dispose()
    {
        background?.Dispose();
        foreground?.Dispose();
    }
}
