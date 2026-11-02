using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DevCleaner.Core;
using DevCleaner.Core.DiskUsage;

namespace DevCleaner.Gui.Controls;

/// <summary>Binary-split treemap of a <see cref="SizeNode"/>'s children; click a tile to drill in.</summary>
public sealed class TreemapControl : Control
{
    public static readonly StyledProperty<SizeNode?> NodeProperty =
        AvaloniaProperty.Register<TreemapControl, SizeNode?>(nameof(Node));

    private static readonly IBrush[] Palette =
    [
        new SolidColorBrush(Color.Parse("#4E79A7")), new SolidColorBrush(Color.Parse("#F28E2B")),
        new SolidColorBrush(Color.Parse("#59A14F")), new SolidColorBrush(Color.Parse("#E15759")),
        new SolidColorBrush(Color.Parse("#76B7B2")), new SolidColorBrush(Color.Parse("#EDC948")),
        new SolidColorBrush(Color.Parse("#B07AA1")), new SolidColorBrush(Color.Parse("#FF9DA7")),
        new SolidColorBrush(Color.Parse("#9C755F")), new SolidColorBrush(Color.Parse("#BAB0AC")),
    ];

    private static readonly IPen Border = new Pen(Brushes.Black, 1);
    private readonly List<(Rect Rect, SizeNode Node)> _tiles = [];

    static TreemapControl() => AffectsRender<TreemapControl>(NodeProperty);

    public SizeNode? Node
    {
        get => GetValue(NodeProperty);
        set => SetValue(NodeProperty, value);
    }

    public event EventHandler<SizeNode>? NodeActivated;

    public override void Render(DrawingContext context)
    {
        _tiles.Clear();
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.Transparent, bounds);
        if (Node is null)
            return;
        var items = Node.Children.Where(c => c.Size > 0).ToList();
        for (var i = 0; i < items.Count; i++)
            _colors[items[i]] = Palette[i % Palette.Length];
        Split(context, items, bounds, depth: 0);
    }

    private readonly Dictionary<SizeNode, IBrush> _colors = [];

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        var hit = _tiles.FirstOrDefault(t => t.Rect.Contains(p));
        if (hit.Node is not null)
            NodeActivated?.Invoke(this, hit.Node);
    }

    private void Split(DrawingContext ctx, List<SizeNode> items, Rect rect, int depth)
    {
        if (items.Count == 0 || rect.Width < 1 || rect.Height < 1)
            return;
        if (items.Count == 1)
        {
            DrawTile(ctx, items[0], rect, depth);
            return;
        }

        double total = items.Sum(i => i.Size);
        double acc = 0;
        var k = 0;
        while (k < items.Count - 1 && acc + items[k].Size <= total / 2)
            acc += items[k++].Size;
        if (k == 0)
            acc = items[k++].Size;

        var frac = acc / total;
        Rect a, b;
        if (rect.Width >= rect.Height)
        {
            a = new Rect(rect.X, rect.Y, rect.Width * frac, rect.Height);
            b = new Rect(rect.X + a.Width, rect.Y, rect.Width - a.Width, rect.Height);
        }
        else
        {
            a = new Rect(rect.X, rect.Y, rect.Width, rect.Height * frac);
            b = new Rect(rect.X, rect.Y + a.Height, rect.Width, rect.Height - a.Height);
        }

        Split(ctx, items.GetRange(0, k), a, depth);
        Split(ctx, items.GetRange(k, items.Count - k), b, depth);
    }

    private void DrawTile(DrawingContext ctx, SizeNode node, Rect rect, int depth)
    {
        var brush = depth == 0 ? _colors.GetValueOrDefault(node, Palette[0]) : _colors.GetValueOrDefault(node, Brushes.Gray);
        ctx.FillRectangle(brush, rect);
        ctx.DrawRectangle(Border, rect);
        if (depth == 0)
            _tiles.Add((rect, node));

        if (rect.Width > 50 && rect.Height > 18)
        {
            var text = new FormattedText($"{node.Name} ({Bytes.Format(node.Size)})", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                Typeface.Default, 11, Brushes.White);
            using (ctx.PushClip(rect))
                ctx.DrawText(text, rect.TopLeft + new Point(3, 2));
        }

        if (depth == 0 && node.IsDirectory && node.Children.Count > 0 && rect.Width > 60 && rect.Height > 50)
        {
            var inner = new Rect(rect.X + 3, rect.Y + 18, rect.Width - 6, rect.Height - 21);
            var children = node.Children.Where(c => c.Size > 0).ToList();
            var baseColor = ((ISolidColorBrush)brush).Color;
            foreach (var c in children)
                _colors[c] = new SolidColorBrush(Color.FromArgb(255, (byte)(baseColor.R * 0.8), (byte)(baseColor.G * 0.8), (byte)(baseColor.B * 0.8)));
            Split(ctx, children, inner, depth + 1);
        }
    }
}
