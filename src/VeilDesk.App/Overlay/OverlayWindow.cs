using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using VeilDesk.Core.Censoring;
using VeilDesk.Core.Detection;
using VeilDesk.Core.Geometry;
using VeilDesk.Core.Settings;

namespace VeilDesk.App.Overlay;

/// <summary>
/// One transparent, click-through, topmost overlay window per display. All
/// geometry is supplied in monitor-local physical pixels and converted to WPF
/// device-independent units with the window's actual DPI. The window is marked
/// WDA_EXCLUDEFROMCAPTURE so the censored content cannot be recorded by WGC,
/// OBS display capture, or the built-in screenshot exporter.
/// </summary>
public sealed class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExLayered = 0x00080000;
    private const long WsExNoActivate = 0x08000000;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint WdaExcludeFromCapture = 0x00000011;
    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly MonitorDescriptor _monitor;
    private readonly Canvas _canvas = new();
    private readonly List<CensorVisual> _visuals = new();
    private readonly Dictionary<byte[], BitmapSource> _maskCache = new(ReferenceEqualityComparer.Instance);
    private BitmapSource? _snapshot;
    private ImageSource? _customImage;
    private string _customImagePath = "";
    private double _dpiScale;
    private bool _positioned;

    public OverlayWindow(MonitorDescriptor monitor)
    {
        _monitor = monitor;
        _dpiScale = monitor.DpiScale > 0 ? monitor.DpiScale : 1.0;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = false;

        _canvas.Background = Brushes.Transparent;
        _canvas.IsHitTestVisible = false;
        _canvas.SnapsToDevicePixels = true;
        Content = _canvas;

        SourceInitialized += (_, _) => ApplyNativeStyles();
        Loaded += (_, _) =>
        {
            _dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            PositionWindow();
            _positioned = true;
        };
        SizeChanged += (_, _) => UpdateCanvasSize();
    }

    public string MonitorId => _monitor.Id;
    public bool IsExcludedFromCapture { get; private set; }

    public void ShowOverlay()
    {
        if (!IsVisible) Show();
        PositionWindow();
        // Re-assert topmost after other topmost windows (e.g. a game overlay).
        SetWindowPos(new WindowInteropHelper(this).Handle, HwndTopmost,
            _monitor.X, _monitor.Y, _monitor.Width, _monitor.Height,
            SwpNoActivate | SwpShowWindow);
    }

    public void HideOverlay()
    {
        if (IsVisible) Hide();
    }

    private void ApplyNativeStyles()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        style |= WsExTransparent | WsExToolWindow | WsExLayered | WsExNoActivate;
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style));
#if DEBUG
        // Debug-only visual verification: overlays can be made visible to a
        // screenshot so alignment can be checked. Release builds always
        // exclude overlays from capture.
        IsExcludedFromCapture =
            Environment.GetEnvironmentVariable("VEILDESK_DISABLE_CAPTURE_EXCLUSION") != "1" &&
            SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);
#else
        IsExcludedFromCapture = SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);
#endif
        PositionWindow();
    }

    private void PositionWindow()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HwndTopmost, _monitor.X, _monitor.Y, _monitor.Width, _monitor.Height,
            SwpNoActivate | SwpShowWindow);
        UpdateCanvasSize();
    }

    private void UpdateCanvasSize()
    {
        _canvas.Width = Math.Max(1, _monitor.Width / _dpiScale);
        _canvas.Height = Math.Max(1, _monitor.Height / _dpiScale);
    }

    public void Render(IReadOnlyList<CensorRegion> regions, AppSettings settings, BitmapSource? snapshot)
    {
        if (!_positioned) PositionWindow();
        _snapshot = snapshot;
        _customImage = LoadCustomImage(settings.Censoring.ImagePath);

        var visible = 0;
        // Fail-closed is a full-screen cover; drawing tracked regions below it
        // would waste work and could composite outside the cover.
        var failClosed = regions.Any(r => r.Kind == CensorRegionKind.FailClosed);
        foreach (var region in regions)
        {
            if (failClosed && region.Kind != CensorRegionKind.FailClosed) continue;
            var visual = GetVisual(visible++);
            visual.Apply(region, settings, _snapshot, _customImage, _dpiScale, _monitor,
                _maskCache);
            visual.Visibility = Visibility.Visible;
        }

        for (var i = visible; i < _visuals.Count; i++) _visuals[i].Visibility = Visibility.Collapsed;
    }

    public void Clear()
    {
        foreach (var visual in _visuals) visual.Visibility = Visibility.Collapsed;
        _snapshot = null;
    }

    private CensorVisual GetVisual(int index)
    {
        while (_visuals.Count <= index)
        {
            var visual = new CensorVisual();
            _visuals.Add(visual);
            _canvas.Children.Add(visual);
        }
        return _visuals[index];
    }

    private ImageSource? LoadCustomImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (string.Equals(path, _customImagePath, StringComparison.OrdinalIgnoreCase)) return _customImage;
        _customImagePath = path;
        try
        {
            if (!File.Exists(path))
            {
                _customImage = null;
                return null;
            }
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.EndInit();
            image.Freeze();
            _customImage = image;
        }
        catch
        {
            _customImage = null;
        }
        return _customImage;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
}

/// <summary>
/// A single reusable censor rectangle. Elements are created once and only
/// their properties change while protection is running, which keeps the
/// 30-60 Hz overlay path free of per-frame WPF object allocation.
/// </summary>
internal sealed class CensorVisual : Grid
{
    private readonly Border _fill = new();
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly Border _tint = new();
    private readonly TextBlock _label = new();
    private readonly Border _outline = new();
    private readonly BlurEffect _blur = new() { Radius = 18, KernelType = KernelType.Gaussian };
    private readonly SolidColorBrush _solid = new();

    public CensorVisual()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        Children.Add(_fill);
        Children.Add(_image);
        Children.Add(_tint);
        Children.Add(_label);
        Children.Add(_outline);
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);

        _fill.IsHitTestVisible = false;
        _image.IsHitTestVisible = false;
        _tint.IsHitTestVisible = false;
        _label.IsHitTestVisible = false;
        _outline.IsHitTestVisible = false;
        _label.HorizontalAlignment = HorizontalAlignment.Center;
        _label.VerticalAlignment = VerticalAlignment.Center;
        _label.FontWeight = FontWeights.SemiBold;
        _label.FontFamily = new FontFamily("Segoe UI");
        _label.TextAlignment = TextAlignment.Center;
        _label.Margin = new Thickness(8);
        _label.TextWrapping = TextWrapping.Wrap;
    }

    public void Apply(CensorRegion region, AppSettings settings, BitmapSource? snapshot,
        ImageSource? customImage, double dpiScale, MonitorDescriptor monitor,
        Dictionary<byte[], BitmapSource> maskCache)
    {
        var c = settings.Censoring;
        var box = region.Box.Clip(monitor.LocalBounds);
        if (box.IsEmpty)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        var left = box.X / dpiScale;
        var top = box.Y / dpiScale;
        var width = Math.Max(1, box.Width / dpiScale);
        var height = Math.Max(1, box.Height / dpiScale);
        Width = width;
        Height = height;
        Canvas.SetLeft(this, left);
        Canvas.SetTop(this, top);

        var radius = Math.Min(Math.Min(width, height) / 2, c.CornerRadius);
        Clip = new RectangleGeometry(new Rect(0, 0, width, height), radius, radius);
        _fill.CornerRadius = new CornerRadius(radius);
        _outline.CornerRadius = new CornerRadius(radius);
        _fill.Width = _outline.Width = width;
        _fill.Height = _outline.Height = height;
        _image.Width = _tint.Width = width;
        _image.Height = _tint.Height = height;

        var style = region.Kind == CensorRegionKind.FailClosed ? CensorStyle.Solid : c.Style;
        var color = ParseColor(region.Kind == CensorRegionKind.FailClosed ? "#03040A" : c.Color);
        var opacity = region.Kind == CensorRegionKind.FailClosed ? 1.0 : Math.Clamp(c.Opacity, 0.1, 1.0);
        var showLabel = settings.Censoring.ShowSubjectLabels && region.TrackId is not null;
        _solid.Color = color;

        _image.Visibility = Visibility.Collapsed;
        _image.Effect = null;
        _tint.Visibility = Visibility.Collapsed;
        _tint.Background = Brushes.Transparent;
        _outline.BorderThickness = new Thickness(0);
        _outline.BorderBrush = Brushes.Transparent;
        _label.Visibility = Visibility.Collapsed;
        _label.Text = "";
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _fill.Opacity = opacity;

        switch (style)
        {
            case CensorStyle.Blur:
                SetCropImage(box, snapshot, customImage, null);
                if (_image.Visibility == Visibility.Visible)
                {
                    _blur.Radius = Math.Max(2, c.BlurRadius);
                    _image.Effect = _blur;
                    _fill.Background = Brushes.Black;
                    _fill.Opacity = 0.45 * opacity;
                }
                else
                {
                    _fill.Background = _solid;
                }
                break;

            case CensorStyle.Pixelate:
                SetCropImage(box, snapshot, customImage, c.PixelBlock);
                if (_image.Visibility == Visibility.Visible)
                {
                    RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
                    _fill.Background = Brushes.Black;
                    _fill.Opacity = 0.10 * opacity;
                }
                else
                {
                    _fill.Background = _solid;
                }
                break;

            case CensorStyle.Image:
                if (customImage is not null)
                {
                    _image.Source = customImage;
                    _image.Visibility = Visibility.Visible;
                    _fill.Background = _solid;
                    _fill.Opacity = 0.35 * opacity;
                }
                else
                {
                    _fill.Background = _solid;
                }
                break;

            case CensorStyle.Text:
                _fill.Background = _solid;
                _label.Text = string.IsNullOrWhiteSpace(c.CustomText) ? "HIDDEN" : c.CustomText;
                _label.Foreground = BrushFromHex(c.TextColor);
                _label.FontSize = Math.Clamp(Math.Min(width, height) * 0.18, 11, 42);
                _label.Visibility = Visibility.Visible;
                break;

            case CensorStyle.Outline:
                // Outline remains a censorship style: a thin, bright frame over a
                // translucent but still obscuring fill. It is not "show the
                // original with a box around it".
                _fill.Background = _solid;
                _fill.Opacity = opacity * 0.72;
                _outline.BorderThickness = new Thickness(Math.Max(2, c.BorderThickness));
                _outline.BorderBrush = BrushFromHex(c.BorderColor);
                break;

            case CensorStyle.Silhouette:
                if (region.HasMask)
                {
                    _image.Source = MaskBitmap(region, c.Color, maskCache);
                    _image.Visibility = Visibility.Visible;
                    _fill.Opacity = 0;
                }
                else
                {
                    _fill.Background = _solid;
                }
                break;

            case CensorStyle.Marker:
                _fill.Background = BuildMarkerBrush(c.Color, c.BorderColor);
                break;

            case CensorStyle.NightVision:
                SetCropImage(box, snapshot, customImage, null);
                _fill.Background = _solid;
                _fill.Opacity = 0.35 * opacity;
                if (_image.Visibility == Visibility.Visible)
                {
                    _tint.Background = new SolidColorBrush(Color.FromArgb(120, 24, 255, 96));
                    _tint.Visibility = Visibility.Visible;
                }
                break;

            case CensorStyle.FrostedGlass:
                SetCropImage(box, snapshot, customImage, null);
                if (_image.Visibility == Visibility.Visible)
                {
                    _blur.Radius = Math.Max(4, c.BlurRadius * 1.4);
                    _image.Effect = _blur;
                    _tint.Background = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
                    _tint.Visibility = Visibility.Visible;
                }
                _fill.Background = _solid;
                _fill.Opacity = 0.25 * opacity;
                _outline.BorderThickness = new Thickness(1);
                _outline.BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
                break;

            default:
                _fill.Background = _solid;
                break;
        }

        if (showLabel)
        {
            _label.Text = region.Kind == CensorRegionKind.WholePerson
                ? $"Person {region.TrackId}"
                : $"#{region.TrackId} {Pretty(region.Source)}";
            _label.Foreground = Brushes.White;
            _label.FontSize = Math.Clamp(Math.Min(width, height) * 0.09, 10, 20);
            _label.Visibility = Visibility.Visible;
        }

        if (settings.Censoring.ShowDebugBoxes)
        {
            _outline.BorderThickness = new Thickness(1.5);
            _outline.BorderBrush = new SolidColorBrush(Color.FromArgb(210, 0, 220, 255));
        }

        if (region.Kind == CensorRegionKind.FailClosed)
        {
            _label.Text = "VEILDESK · PROTECTION PAUSED\nDetection is not confident. Screen hidden.";
            _label.Foreground = BrushFromHex("#E8ECF5");
            _label.FontSize = 20;
            _label.Visibility = Visibility.Visible;
        }
    }

    private void SetCropImage(PixelRect box, BitmapSource? snapshot, ImageSource? customImage,
        int? pixelBlock)
    {
        if (customImage is not null)
        {
            _image.Source = customImage;
            _image.Visibility = Visibility.Visible;
            return;
        }
        if (snapshot is null) return;

        var x = (int)Math.Floor(box.X);
        var y = (int)Math.Floor(box.Y);
        var w = (int)Math.Ceiling(box.Width);
        var h = (int)Math.Ceiling(box.Height);
        x = Math.Clamp(x, 0, Math.Max(0, snapshot.PixelWidth - 1));
        y = Math.Clamp(y, 0, Math.Max(0, snapshot.PixelHeight - 1));
        w = Math.Clamp(w, 1, snapshot.PixelWidth - x);
        h = Math.Clamp(h, 1, snapshot.PixelHeight - y);
        var crop = new CroppedBitmap(snapshot, new Int32Rect(x, y, w, h));
        crop.Freeze();

        if (pixelBlock is { } block && block > 1)
        {
            var down = new TransformedBitmap(crop, new ScaleTransform(1.0 / block, 1.0 / block));
            down.Freeze();
            _image.Source = down;
        }
        else
        {
            _image.Source = crop;
        }
        _image.Visibility = Visibility.Visible;
    }

    private BitmapSource? MaskBitmap(CensorRegion region, string colorHex,
        Dictionary<byte[], BitmapSource> cache)
    {
        if (region.Mask is null || region.MaskWidth <= 0 || region.MaskHeight <= 0)
            return null;
        if (cache.TryGetValue(region.Mask, out var existing)) return existing;

        var color = ParseColor(colorHex);
        var stride = region.MaskWidth * 4;
        var pixels = new byte[stride * region.MaskHeight];
        for (var i = 0; i < region.Mask.Length && i < region.MaskWidth * region.MaskHeight; i++)
        {
            var alpha = region.Mask[i];
            var j = i * 4;
            pixels[j] = color.B;      // BGRA
            pixels[j + 1] = color.G;
            pixels[j + 2] = color.R;
            pixels[j + 3] = alpha;
        }
        var bitmap = BitmapSource.Create(region.MaskWidth, region.MaskHeight, 96, 96,
            PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        if (cache.Count > 24) cache.Clear();
        cache[region.Mask] = bitmap;
        return bitmap;
    }

    private static Brush BuildMarkerBrush(string colorHex, string borderHex)
    {
        var color = ParseColor(colorHex);
        var stripe = new DrawingBrush
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 18, 18),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None
        };
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(color),
            null, new RectangleGeometry(new Rect(0, 0, 18, 18))));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(ParseColor(borderHex)),
            null, new GeometryGroup
            {
                Children =
                {
                    new RectangleGeometry(new Rect(0, 0, 9, 9)),
                    new RectangleGeometry(new Rect(9, 9, 9, 9))
                }
            }));
        stripe.Drawing = group;
        return stripe;
    }

    private static string Pretty(BodyRegion region) => region switch
    {
        BodyRegion.FemaleBreastExposed => "Breast",
        BodyRegion.FemaleGenitaliaExposed => "Genitalia",
        BodyRegion.MaleGenitaliaExposed => "Genitalia",
        BodyRegion.ButtocksExposed => "Buttocks",
        BodyRegion.AnusExposed => "Anus",
        BodyRegion.Person => "Person",
        _ => region.ToString()
    };

    private static Color ParseColor(string value)
    {
        try
        {
            if (ColorConverter.ConvertFromString(value) is Color color) return color;
        }
        catch
        {
        }
        return Color.FromRgb(11, 13, 18);
    }

    private static Brush BrushFromHex(string value) => new SolidColorBrush(ParseColor(value));
}
