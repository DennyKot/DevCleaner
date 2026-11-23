using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace DevCleaner.Gui.Controls;

/// <summary>
/// Before/after viewer: both images share one view; a draggable divider shows the original on the left and the
/// compressed result on the right. Wheel zooms around the cursor, drag pans, double-click toggles fit / 100%.
/// </summary>
public sealed class ImageCompareControl : Control
{
    public static readonly StyledProperty<Bitmap?> BeforeProperty =
        AvaloniaProperty.Register<ImageCompareControl, Bitmap?>(nameof(Before));

    public static readonly StyledProperty<Bitmap?> AfterProperty =
        AvaloniaProperty.Register<ImageCompareControl, Bitmap?>(nameof(After));

    public static readonly StyledProperty<string> BeforeLabelProperty =
        AvaloniaProperty.Register<ImageCompareControl, string>(nameof(BeforeLabel), "ORIGINAL");

    public static readonly StyledProperty<string> AfterLabelProperty =
        AvaloniaProperty.Register<ImageCompareControl, string>(nameof(AfterLabel), "COMPRESSED");

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<ImageCompareControl, string?>(nameof(Title));

    private static readonly IBrush Backdrop = new SolidColorBrush(Color.Parse("#1B1D2E"));
    private static readonly IBrush LabelBg = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0));
    private static readonly IPen DividerPen = new Pen(Brushes.White, 2);
    private static readonly IPen HandlePen = new Pen(new SolidColorBrush(Color.Parse("#5046E5")), 2);
    private static readonly Typeface LabelFont = new("Inter", FontStyle.Normal, FontWeight.SemiBold);
    private const double HandleRadius = 16;

    private double _split = 0.5;
    private double _scale;            // screen px per image px; 0 = fit
    private Point _origin;            // screen position of the image's top-left corner
    private Point? _panFrom;
    private bool _dragDivider;

    static ImageCompareControl()
    {
        AffectsRender<ImageCompareControl>(BeforeProperty, AfterProperty, BeforeLabelProperty, AfterLabelProperty, TitleProperty);
        FocusableProperty.OverrideDefaultValue<ImageCompareControl>(true);
    }

    public Bitmap? Before
    {
        get => GetValue(BeforeProperty);
        set => SetValue(BeforeProperty, value);
    }

    public Bitmap? After
    {
        get => GetValue(AfterProperty);
        set => SetValue(AfterProperty, value);
    }

    public string BeforeLabel
    {
        get => GetValue(BeforeLabelProperty);
        set => SetValue(BeforeLabelProperty, value);
    }

    /// <summary>Shown bottom-right, e.g. the file name.</summary>
    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string AfterLabel
    {
        get => GetValue(AfterLabelProperty);
        set => SetValue(AfterLabelProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // A different photo starts fitted; the compressed half arriving later keeps the current view.
        if (change.Property == BeforeProperty)
            _scale = 0;
    }

    private Size ImageSize => Before?.Size ?? After?.Size ?? default;

    private double FitScale
    {
        get
        {
            var img = ImageSize;
            if (img.Width <= 0 || img.Height <= 0 || Bounds.Width <= 0 || Bounds.Height <= 0)
                return 1;
            return Math.Min(Bounds.Width / img.Width, Bounds.Height / img.Height);
        }
    }

    private double Scale => _scale > 0 ? _scale : FitScale;

    private Point Origin
    {
        get
        {
            if (_scale > 0)
                return _origin;
            var img = ImageSize;
            var s = FitScale;
            return new Point((Bounds.Width - img.Width * s) / 2, (Bounds.Height - img.Height * s) / 2);
        }
    }

    private double DividerX => Bounds.Width * _split;

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Backdrop, bounds);
        if (Before is null && After is null)
            return;

        var s = Scale;
        var img = ImageSize;
        var dest = new Rect(Origin, new Size(img.Width * s, img.Height * s));
        var mode = s >= 2 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality;
        var x = DividerX;
        using (context.PushClip(bounds))
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = mode }))
        {
            if (Before is { } before)
            {
                using (context.PushClip(new Rect(0, 0, x, bounds.Height)))
                    context.DrawImage(before, new Rect(before.Size), dest);
            }

            if (After is { } after)
            {
                using (context.PushClip(new Rect(x, 0, Math.Max(0, bounds.Width - x), bounds.Height)))
                    context.DrawImage(after, new Rect(after.Size), dest);
            }
        }

        if (After is not null)
        {
            context.DrawLine(DividerPen, new Point(x, 0), new Point(x, bounds.Height));
            var c = new Point(x, bounds.Height / 2);
            context.DrawEllipse(Brushes.White, HandlePen, c, HandleRadius, HandleRadius);
            var arrows = new StreamGeometry();
            using (var g = arrows.Open())
            {
                g.BeginFigure(new Point(c.X - 4, c.Y - 6), true);
                g.LineTo(new Point(c.X - 10, c.Y));
                g.LineTo(new Point(c.X - 4, c.Y + 6));
                g.EndFigure(true);
                g.BeginFigure(new Point(c.X + 4, c.Y - 6), true);
                g.LineTo(new Point(c.X + 10, c.Y));
                g.LineTo(new Point(c.X + 4, c.Y + 6));
                g.EndFigure(true);
            }

            context.DrawGeometry(HandlePen.Brush, null, arrows);
            Label(context, AfterLabel, new Point(bounds.Width - 10, 10), alignRight: true);
        }

        Label(context, BeforeLabel, new Point(10, 10), alignRight: false);
        var zoom = _scale > 0 ? $"{s * 100:0}%" : $"Fit {s * 100:0}%";
        Label(context, zoom, new Point(10, bounds.Height - 32), alignRight: false);
        if (!string.IsNullOrEmpty(Title))
            Label(context, Title, new Point(bounds.Width - 10, bounds.Height - 32), alignRight: true);
    }

    private static void Label(DrawingContext context, string text, Point at, bool alignRight)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFont, 11, Brushes.White);
        var x = alignRight ? at.X - ft.Width - 16 : at.X;
        context.DrawRectangle(LabelBg, null, new RoundedRect(new Rect(x, at.Y, ft.Width + 16, ft.Height + 8), 6));
        context.DrawText(ft, new Point(x + 8, at.Y + 4));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        if (e.ClickCount == 2)
        {
            ToggleActualSize(p);
            e.Handled = true;
            return;
        }

        _dragDivider = After is not null && Math.Abs(p.X - DividerX) <= HandleRadius;
        if (!_dragDivider)
        {
            Freeze();
            _panFrom = p;
        }

        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_dragDivider)
        {
            _split = Math.Clamp(p.X / Math.Max(1, Bounds.Width), 0, 1);
            InvalidateVisual();
        }
        else if (_panFrom is { } from)
        {
            _origin += p - from;
            _panFrom = p;
            InvalidateVisual();
        }

        Cursor = _dragDivider || (After is not null && Math.Abs(p.X - DividerX) <= HandleRadius)
            ? new Cursor(StandardCursorType.SizeWestEast)
            : new Cursor(_panFrom is null ? StandardCursorType.Hand : StandardCursorType.SizeAll);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragDivider = false;
        _panFrom = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var factor = Math.Pow(1.2, e.Delta.Y);
        ZoomAt(e.GetPosition(this), Math.Clamp(Scale * factor, Math.Min(FitScale, 1) * 0.5, 16));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        switch (e.Key)
        {
            case Key.Add or Key.OemPlus:
                ZoomAt(center, Math.Min(16, Scale * 1.25));
                break;
            case Key.Subtract or Key.OemMinus:
                ZoomAt(center, Scale / 1.25);
                break;
            case Key.D0 or Key.NumPad0:
                _scale = 0;
                InvalidateVisual();
                break;
            case Key.D1 or Key.NumPad1:
                ToggleActualSize(center);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void ToggleActualSize(Point at)
    {
        if (_scale > 0 && Math.Abs(_scale - 1) < 0.001)
        {
            _scale = 0;
            InvalidateVisual();
        }
        else
        {
            ZoomAt(at, 1);
        }
    }

    /// <summary>Switches from "fit" to an explicit transform with the same on-screen result.</summary>
    private void Freeze()
    {
        if (_scale > 0)
            return;
        _origin = Origin;
        _scale = FitScale;
    }

    private void ZoomAt(Point p, double newScale)
    {
        Freeze();
        var ratio = newScale / _scale;
        _origin = new Point(p.X - (p.X - _origin.X) * ratio, p.Y - (p.Y - _origin.Y) * ratio);
        _scale = newScale;
        InvalidateVisual();
    }
}
