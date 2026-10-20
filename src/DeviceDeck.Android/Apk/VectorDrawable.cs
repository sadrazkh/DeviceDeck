using SkiaSharp;

namespace DeviceDeck.Android.Apk;

/// <summary>
/// A compiled &lt;vector&gt;: groups (rotate/scale/translate about a pivot), paths (SVG path data, fill and stroke
/// colours or gradients, alpha, fill type) and clip-paths. Enough for launcher foregrounds; tints and
/// trim-path animations are ignored.
/// </summary>
internal sealed class VectorDrawable : IconDrawable
{
    private readonly float _viewportWidth;
    private readonly float _viewportHeight;
    private readonly float _alpha;
    private readonly Node _root;

    private VectorDrawable(float viewportWidth, float viewportHeight, float alpha, Node root)
    {
        _viewportWidth = viewportWidth;
        _viewportHeight = viewportHeight;
        _alpha = alpha;
        _root = root;
    }

    public static async Task<VectorDrawable?> LoadAsync(XmlElement element, IconRenderer renderer, int depth, CancellationToken ct)
    {
        float width = Float(element, AndroidAttr.ViewportWidth, "viewportWidth", 0);
        float height = Float(element, AndroidAttr.ViewportHeight, "viewportHeight", 0);
        if (width <= 0 || height <= 0)
        {
            return null;
        }
        var root = await LoadNodeAsync(element, renderer, depth, ct).ConfigureAwait(false);
        return new VectorDrawable(width, height, Float(element, AndroidAttr.Alpha, "alpha", 1f), root);
    }

    private static async Task<Node> LoadNodeAsync(XmlElement element, IconRenderer renderer, int depth, CancellationToken ct)
    {
        var node = new Node(element.Name);
        if (element.Name == "group")
        {
            float px = Float(element, AndroidAttr.PivotX, "pivotX", 0);
            float py = Float(element, AndroidAttr.PivotY, "pivotY", 0);
            // VectorDrawable.VGroup.updateLocalMatrix
            node.Matrix = SKMatrix.CreateTranslation(-px, -py)
                .PostConcat(SKMatrix.CreateScale(Float(element, AndroidAttr.ScaleX, "scaleX", 1), Float(element, AndroidAttr.ScaleY, "scaleY", 1)))
                .PostConcat(SKMatrix.CreateRotationDegrees(Float(element, AndroidAttr.Rotation, "rotation", 0)))
                .PostConcat(SKMatrix.CreateTranslation(
                    Float(element, AndroidAttr.TranslateX, "translateX", 0) + px,
                    Float(element, AndroidAttr.TranslateY, "translateY", 0) + py));
        }
        if (element.Name is "path" or "clip-path")
        {
            string? data = element.Attribute(AndroidAttr.PathData, "pathData")?.StringValue;
            node.Path = data == null ? null : SKPath.ParseSvgPathData(data);
            if (node.Path != null && element.Attribute(AndroidAttr.FillType, "fillType")?.Value is { IsInt: true, Data: 1 })
            {
                node.Path.FillType = SKPathFillType.EvenOdd;
            }
        }
        if (element.Name == "path")
        {
            node.Fill = await LoadPaintAsync(element.Attribute(AndroidAttr.FillColor, "fillColor"), renderer, depth, ct).ConfigureAwait(false);
            node.Stroke = await LoadPaintAsync(element.Attribute(AndroidAttr.StrokeColor, "strokeColor"), renderer, depth, ct).ConfigureAwait(false);
            node.FillAlpha = Float(element, AndroidAttr.FillAlpha, "fillAlpha", 1f);
            node.StrokeAlpha = Float(element, AndroidAttr.StrokeAlpha, "strokeAlpha", 1f);
            node.StrokeWidth = Float(element, AndroidAttr.StrokeWidth, "strokeWidth", 0f);
        }
        foreach (var child in element.Children.Where(c => c.Name is "group" or "path" or "clip-path"))
        {
            node.Children.Add(await LoadNodeAsync(child, renderer, depth, ct).ConfigureAwait(false));
        }
        return node;
    }

    private static async Task<PaintSpec?> LoadPaintAsync(XmlAttribute? attribute, IconRenderer renderer, int depth, CancellationToken ct)
    {
        if (attribute == null)
        {
            return null;
        }
        if (attribute.Value.IsColor)
        {
            return new PaintSpec(IconRenderer.ToColor(attribute.Value), null);
        }
        if (attribute.StringValue is { } text && text.StartsWith('#') && SKColor.TryParse(text, out var parsed))
        {
            return new PaintSpec(parsed, null);
        }
        // @color/x, a colour state list, or an inlined <gradient> (aapt:attr) compiled into its own file.
        if (await renderer.LoadReferencedXmlAsync(attribute.Value, ct).ConfigureAwait(false) is { Name: "gradient" } gradient)
        {
            return await GradientSpec.LoadAsync(gradient, renderer, depth, ct).ConfigureAwait(false) is { } spec ? new PaintSpec(null, spec) : null;
        }
        return await renderer.ResolveColorAsync(attribute.Value, depth, ct).ConfigureAwait(false) is { } color ? new PaintSpec(color, null) : null;
    }

    public override void Draw(SKCanvas canvas, SKRect bounds)
    {
        canvas.Save();
        canvas.Translate(bounds.Left, bounds.Top);
        canvas.Scale(bounds.Width / _viewportWidth, bounds.Height / _viewportHeight);
        if (_alpha < 1f)
        {
            using var layer = new SKPaint { Color = SKColors.White.WithAlpha((byte)(Math.Clamp(_alpha, 0f, 1f) * 255)) };
            canvas.SaveLayer(layer);
        }
        DrawNode(canvas, _root);
        canvas.Restore();
        if (_alpha < 1f)
        {
            canvas.Restore();
        }
    }

    private static void DrawNode(SKCanvas canvas, Node node)
    {
        switch (node.Kind)
        {
            case "clip-path":
                if (node.Path != null)
                {
                    canvas.ClipPath(node.Path, SKClipOperation.Intersect, antialias: true);
                }
                return;
            case "path":
                DrawPath(canvas, node);
                return;
        }
        canvas.Save();
        if (node.Matrix is { } matrix)
        {
            canvas.Concat(in matrix);
        }
        foreach (var child in node.Children)
        {
            DrawNode(canvas, child);
        }
        canvas.Restore();
    }

    private static void DrawPath(SKCanvas canvas, Node node)
    {
        if (node.Path == null)
        {
            return;
        }
        if (node.Fill != null)
        {
            using var paint = node.Fill.CreatePaint(node.FillAlpha);
            paint.Style = SKPaintStyle.Fill;
            canvas.DrawPath(node.Path, paint);
        }
        if (node.Stroke != null && node.StrokeWidth > 0)
        {
            using var paint = node.Stroke.CreatePaint(node.StrokeAlpha);
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = node.StrokeWidth;
            canvas.DrawPath(node.Path, paint);
        }
    }

    internal static float Float(XmlElement element, uint id, string name, float fallback)
    {
        var attribute = element.Attribute(id, name);
        if (attribute == null)
        {
            return fallback;
        }
        if (attribute.Value.Type is ResValue.TypeFloat or ResValue.TypeDimension or ResValue.TypeFraction || attribute.Value.IsInt)
        {
            return attribute.Value.AsFloat();
        }
        return float.TryParse(attribute.StringValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float parsed)
            ? parsed
            : fallback;
    }

    public override void Dispose() => _root.Dispose();

    private sealed class Node(string kind) : IDisposable
    {
        public string Kind { get; } = kind;
        public SKMatrix? Matrix { get; set; }
        public SKPath? Path { get; set; }
        public PaintSpec? Fill { get; set; }
        public PaintSpec? Stroke { get; set; }
        public float FillAlpha { get; set; } = 1f;
        public float StrokeAlpha { get; set; } = 1f;
        public float StrokeWidth { get; set; }
        public List<Node> Children { get; } = [];

        public void Dispose()
        {
            Path?.Dispose();
            foreach (var child in Children)
            {
                child.Dispose();
            }
        }
    }

    private sealed record PaintSpec(SKColor? Color, GradientSpec? Gradient)
    {
        public SKPaint CreatePaint(float alpha)
        {
            var paint = new SKPaint { IsAntialias = true };
            byte a = (byte)(Math.Clamp(alpha, 0f, 1f) * 255);
            if (Gradient != null)
            {
                paint.Shader = Gradient.CreateShader();
                paint.Color = SKColors.Black.WithAlpha(a);
            }
            else if (Color is { } color)
            {
                paint.Color = color.WithAlpha((byte)(color.Alpha * a / 255));
            }
            return paint;
        }
    }

    /// <summary>&lt;gradient&gt; as used for vector fills: linear (0), radial (1) or sweep (2), with start/center/end colours or &lt;item&gt; stops.</summary>
    private sealed record GradientSpec(int Type, SKPoint Start, SKPoint End, SKPoint Center, float Radius, SKColor[] Colors, float[]? Positions, SKShaderTileMode Tile)
    {
        public static async Task<GradientSpec?> LoadAsync(XmlElement gradient, IconRenderer renderer, int depth, CancellationToken ct)
        {
            var colors = new List<SKColor>();
            var positions = new List<float>();
            foreach (var item in gradient.Children.Where(c => c.Name == "item"))
            {
                if (item.Attribute(AndroidAttr.Color, "color") is { } attribute
                    && await renderer.ResolveColorAsync(attribute.Value, depth, ct).ConfigureAwait(false) is { } color)
                {
                    colors.Add(color);
                    positions.Add(Float(item, AndroidAttr.Offset, "offset", colors.Count == 1 ? 0 : 1));
                }
            }
            if (colors.Count < 2)
            {
                colors.Clear();
                positions.Clear();
                foreach (var (id, name) in new[] { (AndroidAttr.StartColor, "startColor"), (AndroidAttr.CenterColor, "centerColor"), (AndroidAttr.EndColor, "endColor") })
                {
                    if (gradient.Attribute(id, name) is { } attribute
                        && await renderer.ResolveColorAsync(attribute.Value, depth, ct).ConfigureAwait(false) is { } color)
                    {
                        colors.Add(color);
                    }
                }
            }
            if (colors.Count == 0)
            {
                return null;
            }
            if (colors.Count == 1)
            {
                colors.Add(colors[0]);
            }
            int type = (int)(gradient.Attribute(0, "type")?.Value.Data ?? 0);
            int tile = (int)(gradient.Attribute(0, "tileMode")?.Value.Data ?? 0);
            return new GradientSpec(
                type,
                new SKPoint(Float(gradient, AndroidAttr.StartX, "startX", 0), Float(gradient, AndroidAttr.StartY, "startY", 0)),
                new SKPoint(Float(gradient, AndroidAttr.EndX, "endX", 0), Float(gradient, AndroidAttr.EndY, "endY", 0)),
                new SKPoint(Float(gradient, 0, "centerX", 0), Float(gradient, 0, "centerY", 0)),
                Float(gradient, 0, "gradientRadius", 0),
                colors.ToArray(),
                positions.Count == colors.Count ? positions.ToArray() : null,
                tile switch { 1 => SKShaderTileMode.Repeat, 2 => SKShaderTileMode.Mirror, _ => SKShaderTileMode.Clamp });
        }

        public SKShader CreateShader() => Type switch
        {
            1 when Radius > 0 => SKShader.CreateRadialGradient(Center, Radius, Colors, Positions, Tile),
            2 => SKShader.CreateSweepGradient(Center, Colors, Positions),
            _ => SKShader.CreateLinearGradient(Start, End, Colors, Positions, Tile),
        };
    }
}
