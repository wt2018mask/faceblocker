using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using VeilDesk.App.Capture;
using VeilDesk.App.Export;
using VeilDesk.App.Overlay;
using VeilDesk.Core.Censoring;
using VeilDesk.Core.Detection;
using VeilDesk.Core.Geometry;
using VeilDesk.Core.Packs;
using VeilDesk.Core.Sessions;
using VeilDesk.Core.Settings;

namespace VeilDesk.App;

public partial class MainWindow : Window
{
    private static readonly (string Key, string Title, string Subtitle)[] Pages =
    {
        ("Dashboard", "Dashboard", "A private, real-time censor that never sends a frame anywhere."),
        ("Detection", "Detection", "Choose faces and anatomical regions to censor."),
        ("Censoring", "Censoring", "Styles, coordinates, motion prediction, persistence and fail-closed behaviour."),
        ("Displays", "Displays", "Select monitors, verify physical coordinates and review capture limitations."),
        ("Performance", "Performance", "Tune local inference for this machine. Numbers are measured, not claimed."),
        ("Export", "Export", "Write censored screenshots and local MJPEG video. No cloud renderer."),
        ("Packs", "Packs", "Import inert style packs with validated assets and bounded extraction."),
        ("Sessions", "Sessions", "Local history and achievements. Nothing is uploaded."),
        ("Settings", "Settings", "Appearance, data locations and maintenance."),
        ("About", "About & diagnostics", "Licences, provenance, known limitations and a live diagnostics report.")
    };

    private readonly SettingsStore _settingsStore = new();
    private readonly SessionStore _sessionStore = new();
    private readonly VeilDeskEngine _engine;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _saveTimer;
    private readonly List<CheckBox> _regionChecks = new();
    private readonly Dictionary<string, CheckBox> _displayChecks = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Button> _startButtons = new();

    private AppSettings _settings;
    private IReadOnlyList<MonitorDescriptor> _monitors = Array.Empty<MonitorDescriptor>();
    private bool _uiReady;
    private bool _starting;
    private bool _stopping;
    private bool _allowClose;
    private bool _closingStarted;
    private bool _saving;
    private CensorVisual? _previewVisual;
    private BitmapSource? _previewSnapshot;
    private VideoRecorder? _recorder;
    private HwndSource? _hotkeySource;
    private bool _emergencyStopAvailable;
    private bool _emergencyStopping;
    private const int EmergencyStopId = 0x5644;

    public MainWindow()
    {
        InitializeComponent();
        AccessKeyManager.Register("s", HeaderStartButton);
        HeaderStartButton.ToolTip = "Alt+S: start, stop, or cancel initialization";
        _settings = _settingsStore.Load();
        _engine = new VeilDeskEngine(Dispatcher);
        _startButtons.Add(HeaderStartButton);
        _startButtons.Add(HeroStartButton);

        BuildRegionChecks();
        LoadSettingsToUi();
        RefreshDisplays();
        InstallBuiltInPacks();
        RefreshPacks();
        RefreshSessions();
        UpdatePreview();

        _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();

        _saveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveSettings(showStatus: false);
        };

        SourceInitialized += (_, _) =>
        {
            ApplyDarkTitleBar();
            var hwnd = new WindowInteropHelper(this).Handle;
            _hotkeySource = HwndSource.FromHwnd(hwnd);
            _hotkeySource?.AddHook(OnHotkeyMessage);
            _emergencyStopAvailable = RegisterHotKey(hwnd, EmergencyStopId, 0x4006, 0x79);
        };
        Closed += (_, _) =>
        {
            UnregisterHotKey(new WindowInteropHelper(this).Handle, EmergencyStopId);
            _hotkeySource?.RemoveHook(OnHotkeyMessage);
        };
        ApplyPage(0, Pages[0]);
        UpdateStatus();
    }

    private IntPtr OnHotkeyMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0312 || wParam.ToInt32() != EmergencyStopId) return IntPtr.Zero;
        handled = true;
        if (!_emergencyStopping)
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                if (_emergencyStopping) return;
                _emergencyStopping = true;
                try { await StopProtectionAsync(); }
                finally { _emergencyStopping = false; }
            }));
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    private void BuildRegionChecks()
    {
        foreach (var region in BodyRegionExtensions.AllDetectableRegions())
        {
            var check = new CheckBox
            {
                Content = region.ToDisplayName(),
                Tag = region,
                Margin = new Thickness(0, 0, 18, 10),
                Width = 218,
                FontSize = 12.5
            };
            check.Click += SettingChanged;
            _regionChecks.Add(check);
            RegionPanel.Children.Add(check);
        }
    }

    private void FaceOnly_Click(object sender, RoutedEventArgs e)
    {
        if (_starting || _stopping || _engine.IsRunning) return;
        _settings.Detection.EnabledRegions = new() { BodyRegion.Face };
        _settings.Detection.WholeBodyEnabled = false;
        _settings.Censoring.Style = CensorStyle.Solid;
        _settings.Censoring.Color = "#000000";
        _settings.Censoring.Opacity = 1;
        LoadSettingsToUi();
        SaveSettings(showStatus: false);
        UpdatePreview();
        HeroBody.Text = "Face selected · solid black. Start protection (Alt+S) to detect visible faces locally.";
    }

    private void LoadSettingsToUi()
    {
        _uiReady = false;
        try
        {
            var c = _settings.Censoring;
            var d = _settings.Detection;
            FaceConfidenceSlider.Value = d.FaceConfidence;
            LocalFaceGuardCheck.IsChecked = d.LocalFaceUncertaintyCover;
            var p = _settings.Performance;

            foreach (var check in _regionChecks)
                check.IsChecked = d.EnabledRegions.Contains((BodyRegion)check.Tag);

            ConfidenceSlider.Value = d.Confidence;
            NmsSlider.Value = d.NmsIou;
            PersonConfSlider.Value = d.PersonConfidence;
            WholeBodyConfSlider.Value = d.WholeBodyConfidence;
            WholeBodyCheck.IsChecked = d.WholeBodyEnabled;
            NudeOnlyCheck.IsChecked = d.WholeBodyNudeOnly;
            WholeBodyFemaleCheck.IsChecked = d.WholeBodyFemale;
            WholeBodyMaleCheck.IsChecked = d.WholeBodyMale;
            DebugBoxesCheck.IsChecked = c.ShowDebugBoxes;
            DebugLabelsCheck.IsChecked = c.ShowSubjectLabels;

            OpacitySlider.Value = c.Opacity;
            CornerSlider.Value = c.CornerRadius;
            BlurSlider.Value = c.BlurRadius;
            PixelSlider.Value = c.PixelBlock;
            MotionSlider.Value = c.MotionPadding;
            HorizonSlider.Value = c.PredictionHorizonMs;
            PersistenceSlider.Value = c.TrackPersistenceMs;
            ExpansionSlider.Value = c.MaskExpansion;
            FailDelaySlider.Value = c.FailClosedAfterMs;
            FailClosedCheck.IsChecked = c.FailClosed;
            ColorBox.Text = c.Color;
            BorderColorBox.Text = c.BorderColor;
            CustomTextBox.Text = c.CustomText;
            ImagePathBox.Text = c.ImagePath;
            SelectComboByTag(StyleCombo, c.Style.ToString());

            ScanRateSlider.Value = p.ScanRateHz;
            BodyRateSlider.Value = p.BodyScanRateHz;
            SegRateSlider.Value = p.SegmentationRateHz;
            AdaptiveCheck.IsChecked = p.AdaptiveScanning;
            DirectMlCheck.IsChecked = p.UseDirectML;
            ThreadsBox.Text = p.InferenceThreads.ToString(CultureInfo.InvariantCulture);
            SelectComboByTag(PresetCombo, p.Preset.ToString());

            CaptureCursorCheck.IsChecked = _settings.Capture.CaptureCursor;
            ExportDirBox.Text = _settings.Creator.ExportDirectory;
            SelectComboByTag(LanguageCombo, _settings.Ui.Language);
            SelectComboByTag(ThemeCombo, _settings.Ui.Theme);
            StartMinimizedCheck.IsChecked = _settings.Ui.StartMinimized;

            DataRootText.Text = AppPaths.Root;
            ModelsPathText.Text = ModelLocator.Find() ?? "(not found)";
            SettingsPathText.Text = _settingsStore.Path;

            AboutVersion.Text = $"VeilDesk {typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}";
            AboutRuntime.Text = $".NET {Environment.Version} · {RuntimeInformation.OSDescription} · x64";
            AboutModels.Text = "NudeNet 320n (18 anatomical classes), YOLOv8n 320/480 (person), " +
                               "MediaPipe Selfie Segmentation (silhouette) — all ONNX, local only.";
        }
        finally
        {
            _uiReady = true;
        }

        UpdateValueLabels();
        UpdatePreview();
        UpdateDisplayChecks();
        UpdateProviderLabels();
    }

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private static string? SelectedTag(ComboBox combo)
        => (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    private void UpdateValueLabels()
    {
        ConfidenceValue.Text = ConfidenceSlider.Value.ToString("0.00", CultureInfo.InvariantCulture);
        NmsValue.Text = NmsSlider.Value.ToString("0.00", CultureInfo.InvariantCulture);
        PersonConfValue.Text = PersonConfSlider.Value.ToString("0.00", CultureInfo.InvariantCulture);
        WholeBodyConfValue.Text = WholeBodyConfSlider.Value.ToString("0.00", CultureInfo.InvariantCulture);
        OpacityValue.Text = $"{OpacitySlider.Value * 100:0}%";
        CornerValue.Text = $"{CornerSlider.Value:0}";
        BlurValue.Text = $"{BlurSlider.Value:0}";
        PixelValue.Text = $"{PixelSlider.Value:0}";
        MotionValue.Text = $"{MotionSlider.Value * 100:0}%";
        HorizonValue.Text = $"{HorizonSlider.Value:0}";
        PersistenceValue.Text = $"{PersistenceSlider.Value:0}";
        ExpansionValue.Text = $"{ExpansionSlider.Value:0}";
        FailDelayValue.Text = $"{FailDelaySlider.Value:0}";
        ScanRateValue.Text = $"{ScanRateSlider.Value:0}";
        BodyRateValue.Text = $"{BodyRateSlider.Value:0}";
        SegRateValue.Text = $"{SegRateSlider.Value:0}";
    }

    private void SettingChanged(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || _saving) return;
        UpdateValueLabels();
        ReadSettingsFromUi();
        UpdatePreview();
        _engine.UpdateSettings(_settings);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void ReadSettingsFromUi()
    {
        var c = _settings.Censoring;
        var d = _settings.Detection;
        var p = _settings.Performance;

        d.EnabledRegions = _regionChecks
            .Where(x => x.IsChecked == true)
            .Select(x => (BodyRegion)x.Tag)
            .ToHashSet();
        d.FaceConfidence = FaceConfidenceSlider.Value;
        d.LocalFaceUncertaintyCover = LocalFaceGuardCheck.IsChecked == true;
        d.Confidence = ConfidenceSlider.Value;
        d.NmsIou = NmsSlider.Value;
        d.PersonConfidence = PersonConfSlider.Value;
        d.WholeBodyConfidence = WholeBodyConfSlider.Value;
        d.WholeBodyEnabled = WholeBodyCheck.IsChecked == true;
        d.WholeBodyNudeOnly = NudeOnlyCheck.IsChecked == true;
        d.WholeBodyFemale = WholeBodyFemaleCheck.IsChecked == true;
        d.WholeBodyMale = WholeBodyMaleCheck.IsChecked == true;
        c.ShowDebugBoxes = DebugBoxesCheck.IsChecked == true;
        c.ShowSubjectLabels = DebugLabelsCheck.IsChecked == true;

        c.Opacity = OpacitySlider.Value;
        c.CornerRadius = CornerSlider.Value;
        c.BlurRadius = BlurSlider.Value;
        c.PixelBlock = (int)PixelSlider.Value;
        c.MotionPadding = MotionSlider.Value;
        c.PredictionHorizonMs = (int)HorizonSlider.Value;
        c.TrackPersistenceMs = (int)PersistenceSlider.Value;
        c.MaskExpansion = ExpansionSlider.Value;
        c.FailClosedAfterMs = (int)FailDelaySlider.Value;
        c.FailClosed = FailClosedCheck.IsChecked == true;
        if (SettingsValidator.IsHexColor(ColorBox.Text)) c.Color = ColorBox.Text.Trim();
        if (SettingsValidator.IsHexColor(BorderColorBox.Text)) c.BorderColor = BorderColorBox.Text.Trim();
        c.CustomText = CustomTextBox.Text;
        c.ImagePath = ImagePathBox.Text;
        if (Enum.TryParse<CensorStyle>(SelectedTag(StyleCombo), out var style)) c.Style = style;
        if (SettingsValidator.IsHexColor(c.BorderColor)) c.TextColor = c.BorderColor;

        p.ScanRateHz = (int)ScanRateSlider.Value;
        p.BodyScanRateHz = (int)BodyRateSlider.Value;
        p.SegmentationRateHz = (int)SegRateSlider.Value;
        p.AdaptiveScanning = AdaptiveCheck.IsChecked == true;
        p.UseDirectML = DirectMlCheck.IsChecked == true;
        p.InferenceThreads = int.TryParse(ThreadsBox.Text, out var threads) ? threads : 0;
        if (Enum.TryParse<PerformancePreset>(SelectedTag(PresetCombo), out var preset)) p.Preset = preset;

        _settings.Capture.CaptureCursor = CaptureCursorCheck.IsChecked == true;
        var checkedIds = _displayChecks.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();
        _settings.Capture.EnabledMonitorIds = checkedIds.Count == _monitors.Count
            ? new List<string>()
            : checkedIds;
        _settings.Creator.ExportDirectory = ExportDirBox.Text;
        _settings.Ui.Language = SelectedTag(LanguageCombo) ?? "en";
        _settings.Ui.Theme = SelectedTag(ThemeCombo) ?? "Midnight";
        _settings.Ui.StartMinimized = StartMinimizedCheck.IsChecked == true;

        SettingsValidator.ValidateAndClamp(_settings);
    }

    private void UpdatePreview()
    {
        if (PreviewHost is null) return;
        _previewSnapshot ??= BuildPreviewSnapshot();
        PreviewHost.Children.Clear();

        var viewbox = new Viewbox { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
        var canvas = new Canvas { Width = 640, Height = 400, ClipToBounds = true };
        canvas.Children.Add(new Image { Source = _previewSnapshot, Width = 640, Height = 400, Stretch = Stretch.Fill });
        _previewVisual = new CensorVisual();
        var region = new CensorRegion(7, BodyRegion.FemaleBreastExposed, CensorRegionKind.Detection,
            new PixelRect(232, 92, 180, 150), 0.93, null, 0, 0);
        var monitor = new MonitorDescriptor("preview", "PREVIEW", "Preview", 0, 0, 640, 400, 1.0, false);
        _previewVisual.Apply(region, _settings, _previewSnapshot, null, 1.0, monitor,
            new Dictionary<byte[], BitmapSource>(ReferenceEqualityComparer.Instance));
        canvas.Children.Add(_previewVisual);
        viewbox.Child = canvas;
        PreviewHost.Children.Add(viewbox);
    }

    private static BitmapSource BuildPreviewSnapshot()
    {
        const int width = 640, height = 400;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(21, 27, 41), Color.FromRgb(48, 38, 79), 45),
                null, new Rect(0, 0, width, height));
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(76, 66, 98)), null, new Point(320, 130), 72, 86);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(95, 83, 120)), null,
                new Rect(250, 205, 140, 175), 42, 42);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(30, 35, 52)), null, new Rect(0, 330, width, 70));
            var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)), 1);
            for (var x = 0; x < width; x += 40) dc.DrawLine(gridPen, new Point(x, 0), new Point(x, height));
            for (var y = 0; y < height; y += 40) dc.DrawLine(gridPen, new Point(0, y), new Point(width, y));
            var label = new FormattedText("SENSITIVE AREA", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 13,
                new SolidColorBrush(Color.FromArgb(150, 240, 235, 255)), 1.0);
            dc.DrawText(label, new Point(22, 20));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private void RefreshDisplays()
    {
        _settings = _settingsStore.Load();
        _monitors = MonitorEnumerator.Enumerate();
        _displayChecks.Clear();
        DisplaysPanel.Children.Clear();
        DashMonitorsPanel.Children.Clear();

        var enabled = _settings.Capture.EnabledMonitorIds;
        foreach (var monitor in _monitors)
        {
            var check = new CheckBox
            {
                Content = $"{monitor.FriendlyName}  ·  {monitor.Width}×{monitor.Height} @ {monitor.DpiScale:0.##}x" +
                          (monitor.IsPrimary ? "  ·  primary" : ""),
                Tag = monitor.Id,
                IsChecked = enabled.Count == 0 || enabled.Contains(monitor.Id, StringComparer.OrdinalIgnoreCase),
                Margin = new Thickness(0, 0, 0, 10),
                FontSize = 13
            };
            check.Click += SettingChanged;
            _displayChecks[monitor.Id] = check;

            var row = new Border
            {
                Background = (Brush)FindResource("Surface2Brush"),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 8),
                Child = new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                        new ColumnDefinition { Width = GridLength.Auto }
                    }
                }
            };
            var grid = (Grid)row.Child;
            grid.Children.Add(check);
            var detail = new TextBlock
            {
                Text = $"{monitor.Id}  ·  origin ({monitor.X},{monitor.Y})  ·  {monitor.DpiScale * 96:0} DPI",
                Style = (Style)FindResource("Muted"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            Grid.SetColumn(detail, 1);
            grid.Children.Add(detail);
            DisplaysPanel.Children.Add(row);

            var dash = new Border
            {
                Background = (Brush)FindResource("Surface2Brush"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 6),
                Child = new TextBlock
                {
                    Text = $"{(monitor.IsPrimary ? "Primary · " : "")}{monitor.FriendlyName}   {monitor.Width}×{monitor.Height}",
                    FontSize = 12
                }
            };
            DashMonitorsPanel.Children.Add(dash);
        }

        if (_monitors.Count == 0)
        {
            DisplaysPanel.Children.Add(new TextBlock
            {
                Text = "No displays were found.",
                Style = (Style)FindResource("Muted")
            });
        }

        ExportMonitorCombo.Items.Clear();
        var selected = _engine.IsRunning ? _engine.GetActiveMonitors() : _monitors;
        foreach (var monitor in selected)
        {
            ExportMonitorCombo.Items.Add(new ComboBoxItem
            {
                Content = monitor.FriendlyName,
                Tag = monitor.Id
            });
        }
        if (ExportMonitorCombo.Items.Count > 0) ExportMonitorCombo.SelectedIndex = 0;
    }

    private void UpdateDisplayChecks()
    {
        if (_displayChecks.Count == 0) return;
        var enabled = _settings.Capture.EnabledMonitorIds;
        foreach (var (id, check) in _displayChecks)
            check.IsChecked = enabled.Count == 0 || enabled.Contains(id, StringComparer.OrdinalIgnoreCase);
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageTitle is null || PageDashboard is null) return;
        if (NavList.SelectedIndex < 0 || NavList.SelectedIndex >= Pages.Length) return;
        ApplyPage(NavList.SelectedIndex, Pages[NavList.SelectedIndex]);
    }

    private void ApplyPage(int index, (string Key, string Title, string Subtitle) page)
    {
        var pages = new FrameworkElement[]
        {
            PageDashboard, PageDetection, PageCensoring, PageDisplays, PagePerformance,
            PageExport, PagePacks, PageSessions, PageSettings, PageAbout
        };
        for (var i = 0; i < pages.Length; i++) pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = page.Title;
        PageSubtitle.Text = page.Subtitle;

        if (page.Key == "Sessions") RefreshSessions();
        if (page.Key == "Packs") RefreshPacks();
        if (page.Key == "About") UpdateDiagnostics();
    }

    private void GoSettings_Click(object sender, RoutedEventArgs e) => NavList.SelectedIndex = 8;

    private async void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (_stopping) return;
        if (_starting)
        {
            await StopProtectionAsync();
            return;
        }
        if (!_engine.IsRunning && !_emergencyStopAvailable)
        {
            MessageBox.Show(this, "Ctrl+Shift+F10 emergency stop could not be registered. Close the other app using this shortcut, then restart VeilDesk.",
                "Emergency stop unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        AppLog.Write("toggle", "running=" + _engine.IsRunning);
        if (_engine.IsRunning)
        {
            await StopProtectionAsync();
            return;
        }

        ReadSettingsFromUi();
        if (_settings.Detection.EnabledRegions.Count == 0 && !_settings.Detection.WholeBodyEnabled)
        {
            MessageBox.Show(this, "Select Face, an anatomical region, or whole-person mode.",
                "Nothing to censor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_displayChecks.Count > 0 && _displayChecks.All(kv => kv.Value.IsChecked != true))
        {
            MessageBox.Show(this, "Select at least one display in Displays.",
                "No display selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _starting = true;
        SetStartButtons(true, "Cancel initialization");
        StateText.Text = "Loading models";
        HeroEyebrow.Text = "STARTING";
        HeroTitle.Text = "Loading local models and capture…";
        HeroBody.Text = "The first run initialises ONNX Runtime and the Windows Graphics Capture device. No network request is made.";
        try
        {
            SaveSettings(showStatus: false);
            await _engine.StartAsync(_settings, _monitors);
            AppLog.Write("protection-started", $"monitors={_monitors.Count} provider={_engine.Provider}");
            StateText.Text = "Protecting";
            HeroEyebrow.Text = "PROTECTION ON";
            HeroTitle.Text = "Your displays are covered.";
            HeroBody.Text = "Captured frames are analysed locally; overlays are click-through and excluded from capture. Stop protection at any time.";
            if (_settings.Detection.EnabledRegions.Contains(BodyRegion.Face))
                HeroBody.Text += _settings.Detection.LocalFaceUncertaintyCover
                    ? " Uncertain faces receive enlarged opaque local guards; capture or inference failure still hides the screen."
                    : " Strict face uncertainty mode may hide the whole screen for a low-confidence candidate.";
            RefreshExportMonitors();
        }
        catch (OperationCanceledException)
        {
            AppLog.Write("protection-start-cancelled", "user cancelled initialization");
            StateText.Text = "Idle";
            HeroEyebrow.Text = "PROTECTION OFF";
            HeroTitle.Text = "Protection initialization cancelled.";
            HeroBody.Text = "Nothing is being captured. Start again when ready.";
        }
        catch (Exception ex)
        {
            AppLog.WriteException("protection-start-failed", ex);
            StateText.Text = "Idle";
            HeroEyebrow.Text = "PROTECTION OFF";
            HeroTitle.Text = "Your screen is not being captured.";
            HeroBody.Text = "Start protection to capture selected displays, run selected ONNX models locally, and draw click-through censor overlays.";
            MessageBox.Show(this, ex.Message, "Could not start protection",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _starting = false;
            if (!_stopping) SetStartButtons(true, _engine.IsRunning ? "Stop protection" : "Start protection");
            UpdateStatus();
        }
    }

    private async Task StopProtectionAsync()
    {
        if (_stopping) return;
        _stopping = true;
        try
        {
            AppLog.Write("protection-stop-requested");
            var stopTask = _engine.StopAsync();
            SetStartButtons(false, "Stopping…");
            if (_recorder is not null)
            {
                try
                {
                    var recordedPath = await _recorder.StopAsync();
                    RecordStatus.Text = "Saved: " + recordedPath;
                }
                catch (Exception ex)
                {
                    RecordStatus.Text = "Recording error: " + ex.Message;
                }
                _recorder = null;
                RecordButton.Content = "Start recording";
            }
            try { await stopTask; }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Stop reported an error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            SetStartButtons(true, "Start protection");
            StateText.Text = "Idle";
            HeroEyebrow.Text = "PROTECTION OFF";
            HeroTitle.Text = "Your screen is not being captured.";
            HeroBody.Text = "Start protection to capture selected displays, run selected ONNX models locally, and draw click-through censor overlays.";
            RefreshExportMonitors();
            UpdateStatus();
        }
        finally
        {
            _stopping = false;
            SetStartButtons(true, _engine.IsRunning ? "Stop protection" : "Start protection");
        }
    }

    private void SetStartButtons(bool enabled, string text)
    {
        PageDetection.IsEnabled = !_starting && !_stopping && !_engine.IsRunning;
        FaceOnlyButton.IsEnabled = PageDetection.IsEnabled;
        HeaderStartButton.IsEnabled = enabled;
        HeroStartButton.IsEnabled = enabled;
        HeaderStartButton.Content = text;
        HeroStartButton.Content = text;
        HeaderStartButton.Style = _engine.IsRunning
            ? (Style)FindResource("DangerButton")
            : (Style)FindResource("PrimaryButton");
    }

    private void UpdateStatus()
    {
        if (!IsLoaded) return;
        var status = _engine.GetStatus();
        var running = status.Running;
        var accent = running ? (Brush)FindResource("GoodBrush") : (Brush)FindResource("MutedBrush");
        StateDot.Fill = accent;
        SidebarStatusDot.Fill = accent;
        StateText.Text = _starting ? "Starting" : running
            ? (status.Monitors.Any(m => m.FailClosed) ? "Guarding" : "Protecting")
            : "Idle";
        SidebarStatusText.Text = StateText.Text;
        SidebarStatusDetail.Text = running
            ? $"{status.Monitors.Count} display(s) · {status.Provider}"
            : "Nothing is being captured.";
        StatePill.BorderBrush = running ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("BorderBrush");

        if (!_starting && !_stopping)
        {
            HeaderStartButton.Content = running ? "Stop protection" : "Start protection";
            HeroStartButton.Content = HeaderStartButton.Content;
            HeaderStartButton.Style = running
                ? (Style)FindResource("DangerButton")
                : (Style)FindResource("PrimaryButton");
            HeaderStartButton.IsEnabled = true;
            HeroStartButton.IsEnabled = true;
        }

        var monitors = status.Monitors;
        if (running)
        {
            var captureFps = monitors.Count > 0 ? monitors.Average(m => m.CaptureFps) : 0;
            var p50 = monitors.Count > 0 ? monitors.Average(m => m.P50Ms) : 0;
            var p95 = monitors.Count > 0 ? monitors.Max(m => m.P95Ms) : 0;
            DashCapture.Text = $"{captureFps:0.0}";
            DashCaptureSub.Text = $"fps · {monitors.Sum(m => m.DroppedFrames):N0} dropped";
            DashLatency.Text = $"{p50:0} ms";
            DashLatencySub.Text = $"P95 {p95:0} ms · {status.Provider}";
            DashTracks.Text = monitors.Sum(m => m.Tracks).ToString(CultureInfo.InvariantCulture);
            DashTracksSub.Text = $"peak {status.PeakTracks} · {monitors.Sum(m => m.Regions)} regions";
            DashDetections.Text = status.TotalDetections.ToString("N0", CultureInfo.InvariantCulture);
            DashDetectionsSub.Text = $"{status.TotalAnalyses:N0} analysis passes";
            HeroEyebrow.Text = monitors.Any(m => m.FailClosed) ? "FAIL-CLOSED ACTIVE" : "PROTECTION ON";
            PerfProvider.Text = status.Provider;
            PerfLatency.Text = $"{p50:0} / {p95:0} ms";
            PerfFps.Text = $"{captureFps:0.0} / {(monitors.Count > 0 ? monitors.Average(m => m.AnalysisFps) : 0):0.0}";
        }
        else
        {
            DashCapture.Text = "—";
            DashCaptureSub.Text = "frames per second";
            DashLatency.Text = "—";
            DashLatencySub.Text = "full local inference";
            DashTracks.Text = "0";
            DashTracksSub.Text = "persistent subjects";
            DashDetections.Text = "0";
            DashDetectionsSub.Text = "this session";
            PerfProvider.Text = status.Provider;
            PerfLatency.Text = "—";
            PerfFps.Text = "—";
        }

        UpdateDashHealth(status);
        UpdateDiagnostics(status);
    }

    private void UpdateDashHealth(EngineStatus status)
    {
        if (!status.Running)
        {
            DashHealthPanel.Children.Clear();
            DashHealthPanel.Children.Add(new TextBlock
            {
                Text = "Start protection to see provider, overlay exclusion and frame freshness.",
                Style = (Style)FindResource("Muted")
            });
            return;
        }

        DashHealthPanel.Children.Clear();
        AddHealthRow("Provider", status.Provider, string.Equals(status.Provider, "CPU", StringComparison.OrdinalIgnoreCase)
            ? (Brush)FindResource("TextBrush") : (Brush)FindResource("WarnBrush"));
        AddHealthRow("Overlay excluded from capture",
            status.OverlayExcludedFromCapture ? "Yes" : "No — check compatibility",
            status.OverlayExcludedFromCapture ? (Brush)FindResource("GoodBrush") : (Brush)FindResource("BadBrush"));
        AddHealthRow("Overlay running", status.OverlayRunning ? "Yes" : "No",
            status.OverlayRunning ? (Brush)FindResource("GoodBrush") : (Brush)FindResource("BadBrush"));
        foreach (var m in status.Monitors)
        {
            AddHealthRow(m.Name, $"{m.CaptureFps:0.0} fps · {m.P50Ms:0} ms" +
                (m.FailClosed ? " · FAIL-CLOSED" : ""),
                m.FailClosed ? (Brush)FindResource("WarnBrush") :
                m.HasRecentFrame ? (Brush)FindResource("GoodBrush") : (Brush)FindResource("BadBrush"));
            if (!string.IsNullOrWhiteSpace(m.Error) || !string.IsNullOrWhiteSpace(m.CaptureError))
                AddHealthRow("  error", m.Error ?? m.CaptureError!, (Brush)FindResource("BadBrush"));
        }
        if (!string.IsNullOrWhiteSpace(status.DirectMLError))
            AddHealthRow("DirectML", status.DirectMLError!, (Brush)FindResource("WarnBrush"));
    }

    private void AddHealthRow(string label, string value, Brush brush)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Muted") });
        var valueText = new TextBlock
        {
            Text = value,
            FontSize = 11.5,
            Foreground = brush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 180,
            TextAlignment = TextAlignment.Right
        };
        Grid.SetColumn(valueText, 1);
        grid.Children.Add(valueText);
        DashHealthPanel.Children.Add(grid);
    }

    private void RefreshExportMonitors()
    {
        var selectedTag = (ExportMonitorCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var monitors = _engine.IsRunning ? _engine.GetActiveMonitors() : _monitors;
        ExportMonitorCombo.Items.Clear();
        foreach (var monitor in monitors)
        {
            var item = new ComboBoxItem { Content = monitor.FriendlyName, Tag = monitor.Id };
            ExportMonitorCombo.Items.Add(item);
            if (string.Equals(monitor.Id, selectedTag, StringComparison.OrdinalIgnoreCase))
                ExportMonitorCombo.SelectedItem = item;
        }
        if (ExportMonitorCombo.SelectedIndex < 0 && ExportMonitorCombo.Items.Count > 0)
            ExportMonitorCombo.SelectedIndex = 0;
    }

    private async void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        if (!_engine.IsRunning)
        {
            ExportStatus.Text = "Start protection first; a screenshot uses the newest verified capture frame.";
            return;
        }
        var monitorId = (ExportMonitorCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (string.IsNullOrEmpty(monitorId))
        {
            ExportStatus.Text = "Select a display.";
            return;
        }
        try
        {
            ExportStatus.Text = "Rendering…";
            var path = await _engine.SaveScreenshotAsync(monitorId, ExportCensoredCheck.IsChecked == true);
            ExportStatus.Text = "Saved: " + path;
            ExportStatus.ToolTip = path;
        }
        catch (Exception ex)
        {
            ExportStatus.Text = "Export failed: " + ex.Message;
        }
    }

    private async void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder is not null)
        {
            try
            {
                var path = await _recorder.StopAsync();
                RecordStatus.Text = $"Saved: {path} ({_recorder.FramesWritten} frames, " +
                    $"{_recorder.MeasuredSourceFps:0.0} capture fps measured)";
            }
            catch (Exception ex)
            {
                RecordStatus.Text = "Recording error: " + ex.Message;
            }
            finally
            {
                _recorder = null;
                RecordButton.Content = "Start recording";
            }
            return;
        }
        if (!_engine.IsRunning)
        {
            RecordStatus.Text = "Start protection first.";
            return;
        }
        var monitorId = (ExportMonitorCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (string.IsNullOrEmpty(monitorId))
        {
            RecordStatus.Text = "Select a display.";
            return;
        }
        try
        {
            _recorder = new VideoRecorder(_engine, _settings, monitorId, RecordCensoredCheck.IsChecked == true);
            var path = await _recorder.StartAsync();
            RecordButton.Content = "Stop recording";
            RecordStatus.Text = "Recording to " + path;
        }
        catch (Exception ex)
        {
            _recorder = null;
            RecordStatus.Text = "Recording failed: " + ex.Message;
        }
    }

    private void BrowseExportDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose export folder" };
        if (dialog.ShowDialog(this) == true)
        {
            ExportDirBox.Text = dialog.FolderName;
            SettingChanged(sender, e);
        }
    }

    private void OpenExportDir_Click(object sender, RoutedEventArgs e)
    {
        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "VeilDesk");
        if (!ExportDirectory.TryResolveOrDefault(ExportDirBox.Text, fallback, out var dir, out var error))
        {
            // A pasted URL or a relative path must never reach the file system.
            ExportStatus.Text = error ?? "That export folder cannot be used.";
            return;
        }

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            ExportStatus.Text = "Could not create the export folder: " + ex.Message;
            return;
        }

        OpenPath(dir);
    }

    private void BrowseImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a censor image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.gif|All files|*.*"
        };
        if (dialog.ShowDialog(this) == true)
        {
            ImagePathBox.Text = dialog.FileName;
            SettingChanged(sender, e);
        }
    }

    /// <summary>
    /// Copies the read-only built-in packs shipped beside the executable into
    /// the user pack folder once. Existing files are never overwritten, so a
    /// user can remove a built-in pack without it reappearing on every launch.
    /// </summary>
    private static void InstallBuiltInPacks()
    {
        try
        {
            var source = Path.Combine(AppContext.BaseDirectory, "builtin-packs");
            if (!Directory.Exists(source)) return;
            Directory.CreateDirectory(AppPaths.PacksDirectory);
            var marker = Path.Combine(AppPaths.PacksDirectory, ".builtin-packs-installed");
            if (File.Exists(marker)) return;
            foreach (var file in Directory.EnumerateFiles(source, "*.veilpack"))
            {
                var target = Path.Combine(AppPaths.PacksDirectory, Path.GetFileName(file));
                if (File.Exists(target)) continue;

                // Validate before exposing the file in the installed list.
                var check = Path.Combine(Path.GetTempPath(),
                    "veildesk-builtin-" + Guid.NewGuid().ToString("N")[..10]);
                try
                {
                    var result = PackValidator.ValidateAndExtract(file, check);
                    if (result.Success) File.Copy(file, target);
                }
                finally
                {
                    try { if (Directory.Exists(check)) Directory.Delete(check, true); } catch { }
                }
            }
            File.WriteAllText(marker, "Installed once by VeilDesk. Delete packs individually; they are not recreated.");
        }
        catch
        {
            // A missing or unreadable built-in pack must never block startup.
        }
    }

    private void ImportPack_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import a VeilDesk pack",
            Filter = "VeilDesk packs|*.veilpack;*.zip|All files|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        var incoming = Path.Combine(AppPaths.PacksDirectory,
            "incoming-" + Guid.NewGuid().ToString("N")[..8]);
        var result = PackValidator.ValidateAndExtract(dialog.FileName, incoming);
        string? importedName = null;
        try
        {
            if (!result.Success || result.Manifest is null)
            {
                MessageBox.Show(this, result.Error ?? "Pack validation failed.", "Pack rejected",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Directory.CreateDirectory(AppPaths.PacksDirectory);
            var target = Path.Combine(AppPaths.PacksDirectory, result.Manifest.Id + ".veilpack");
            try { File.Copy(dialog.FileName, target, overwrite: true); } catch { }
            importedName = result.Manifest.Name;
        }
        finally
        {
            // Validation extraction is scratch space; the installed pack is the
            // original .veilpack copied above.
            try { if (Directory.Exists(incoming)) Directory.Delete(incoming, true); } catch { }
        }
        RefreshPacks();
        MessageBox.Show(this, $"Imported '{importedName}'.", "Pack imported",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenPacksFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.PacksDirectory);
        OpenPath(AppPaths.PacksDirectory);
    }

    private void RefreshPacks()
    {
        PacksPanel.Children.Clear();
        Directory.CreateDirectory(AppPaths.PacksDirectory);
        var packs = LoadInstalledPacks();
        if (packs.Count == 0)
        {
            PacksPanel.Children.Add(new TextBlock
            {
                Text = "No packs installed. Built-in packs are copied from the application folder on first launch; use Import for a .veilpack ZIP.",
                Style = (Style)FindResource("Muted")
            });
            return;
        }
        foreach (var pack in packs)
        {
            var row = new Border
            {
                Background = (Brush)FindResource("Surface2Brush"),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 8)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = pack.Manifest.Name, FontWeight = FontWeights.SemiBold, FontSize = 13 });
            text.Children.Add(new TextBlock
            {
                Text = $"{pack.Manifest.Description}  ·  v{pack.Manifest.Version}  ·  {pack.Manifest.Author}",
                Style = (Style)FindResource("Muted"),
                Margin = new Thickness(0, 4, 0, 0)
            });
            grid.Children.Add(text);
            var apply = new Button
            {
                Content = "Apply",
                Style = (Style)FindResource("GhostButton"),
                Padding = new Thickness(12, 5, 12, 5),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = pack.Path
            };
            apply.Click += ApplyPack_Click;
            Grid.SetColumn(apply, 1);
            grid.Children.Add(apply);
            row.Child = grid;
            PacksPanel.Children.Add(row);
        }
    }

    private List<(string Path, PackManifest Manifest)> LoadInstalledPacks()
    {
        var result = new List<(string Path, PackManifest Manifest)>();
        foreach (var file in Directory.EnumerateFiles(AppPaths.PacksDirectory, "*.veilpack"))
        {
            try
            {
                using var archive = ZipFile.OpenRead(file);
                var entry = archive.Entries.FirstOrDefault(x =>
                    string.Equals(x.FullName, "manifest.json", StringComparison.OrdinalIgnoreCase));
                if (entry is null) continue;
                using var stream = entry.Open();
                var manifest = JsonSerializer.Deserialize<PackManifest>(stream, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (manifest is not null) result.Add((file, manifest));
            }
            catch { }
        }
        return result.OrderBy(x => x.Manifest.Name).ToList();
    }

    private void ApplyPack_Click(object sender, RoutedEventArgs e)
    {
        if (_starting || _stopping || _engine.IsRunning)
        {
            MessageBox.Show(this, "Stop protection before applying a preset that may change detection targets.", "Stop protection first");
            return;
        }
        if (sender is not Button { Tag: string path }) return;
        var destination = Path.Combine(AppPaths.PacksDirectory,
            "applied-" + Path.GetFileNameWithoutExtension(path) + "-" + Guid.NewGuid().ToString("N")[..6]);
        var result = PackValidator.ValidateAndExtract(path, destination);
        if (!result.Success || result.Manifest is null)
        {
            MessageBox.Show(this, result.Error ?? "Pack is invalid.", "Pack rejected",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var manifest = result.Manifest;
        try
        {
            // Apply only the sections the pack actually provides. Serializing
            // the whole dictionary into AppSettings used to reset detection
            // whenever a style-only preset was applied.
            if (TryGetPreset(manifest, "Detection", out var detectionElement))
            {
                var detection = detectionElement.Deserialize<DetectionSettings>(SettingsStore.JsonOptions);
                if (detection is not null) _settings.Detection = detection;
            }
            if (TryGetPreset(manifest, "Censoring", out var censoringElement))
            {
                var censoring = censoringElement.Deserialize<CensorSettings>(SettingsStore.JsonOptions);
                if (censoring is not null) _settings.Censoring = censoring;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Pack settings preset is not compatible: " + ex.Message,
                "Pack rejected", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (manifest.TextPresets.Count > 0)
            _settings.Censoring.CustomText = manifest.TextPresets[0].Text;
        if (_settings.Censoring.Style == CensorStyle.Image && manifest.Assets.Count > 0)
        {
            var asset = manifest.Assets.FirstOrDefault(a => a.Type == "image");
            if (asset is not null)
                _settings.Censoring.ImagePath = Path.Combine(destination, asset.Path);
        }
        SettingsValidator.ValidateAndClamp(_settings);
        _engine.UpdateSettings(_settings);
        LoadSettingsToUi();
        SaveSettings(showStatus: false);
        MessageBox.Show(this, $"Applied '{manifest.Name}'.", "Pack applied",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static bool TryGetPreset(PackManifest manifest, string section, out JsonElement value)
    {
        value = default;
        if (manifest.SettingsPreset is not { Count: > 0 }) return false;
        foreach (var pair in manifest.SettingsPreset)
        {
            if (string.Equals(pair.Key, section, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }
        return false;
    }

    private void RefreshSessions()
    {
        try
        {
            var summary = _sessionStore.Summarize();
            SessionCount.Text = summary.SessionCount.ToString(CultureInfo.InvariantCulture);
            SessionTotal.Text = FormatDuration(summary.TotalSeconds);
            SessionDetections.Text = summary.TotalDetections.ToString("N0", CultureInfo.InvariantCulture);
            SessionStreak.Text = $"{summary.CurrentStreakDays} day{(summary.CurrentStreakDays == 1 ? "" : "s")}";

            AchievementsPanel.Children.Clear();
            foreach (var achievement in _sessionStore.EvaluateAchievements())
            {
                var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
                var text = new StackPanel();
                text.Children.Add(new TextBlock
                {
                    Text = achievement.Name,
                    FontSize = 12.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = achievement.Unlocked ? (Brush)FindResource("GoodBrush") : (Brush)FindResource("TextBrush")
                });
                text.Children.Add(new TextBlock
                {
                    Text = achievement.Description,
                    Style = (Style)FindResource("Muted"),
                    FontSize = 11
                });
                grid.Children.Add(text);
                var bar = new ProgressBar
                {
                    Value = achievement.Progress01 * 100,
                    Maximum = 100,
                    Height = 6,
                    Width = 110,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("AccentBrush"),
                    Background = (Brush)FindResource("Surface2Brush"),
                    BorderThickness = new Thickness(0)
                };
                Grid.SetColumn(bar, 1);
                grid.Children.Add(bar);
                AchievementsPanel.Children.Add(grid);
            }

            SessionsPanel.Children.Clear();
            var sessions = _sessionStore.LoadAll();
            if (sessions.Count == 0)
            {
                SessionsPanel.Children.Add(new TextBlock
                {
                    Text = "No completed sessions yet. Start protection and stop it to record one.",
                    Style = (Style)FindResource("Muted")
                });
                return;
            }
            foreach (var session in sessions.Take(12))
            {
                var row = new Border
                {
                    Background = (Brush)FindResource("Surface2Brush"),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 9, 12, 9),
                    Margin = new Thickness(0, 0, 0, 6)
                };
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var left = new StackPanel();
                left.Children.Add(new TextBlock
                {
                    Text = session.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                    FontSize = 12.5,
                    FontWeight = FontWeights.SemiBold
                });
                left.Children.Add(new TextBlock
                {
                    Text = $"{session.MonitorCount} display(s) · {session.Style} · {session.DetectionCount:N0} detection events",
                    Style = (Style)FindResource("Muted"),
                    FontSize = 11
                });
                grid.Children.Add(left);
                var duration = new TextBlock
                {
                    Text = FormatDuration(session.DurationSeconds),
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(duration, 1);
                grid.Children.Add(duration);
                row.Child = grid;
                SessionsPanel.Children.Add(row);
            }
        }
        catch (Exception ex)
        {
            SessionsPanel.Children.Clear();
            SessionsPanel.Children.Add(new TextBlock { Text = "Could not read session history: " + ex.Message, Foreground = (Brush)FindResource("BadBrush") });
        }
    }

    private static string FormatDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds}s";
        return $"{t.Seconds}s";
    }

    private void ClearSessions_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Delete all local session history? This cannot be undone.",
                "Clear history", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _sessionStore.DeleteAll();
        RefreshSessions();
    }

    private void SelectExposure_Click(object sender, RoutedEventArgs e)
    {
        foreach (var check in _regionChecks)
        {
            var region = (BodyRegion)check.Tag;
            check.IsChecked = region.IsExplicitExposed();
        }
        SettingChanged(sender, e);
    }

    private void ClearRegions_Click(object sender, RoutedEventArgs e)
    {
        foreach (var check in _regionChecks) check.IsChecked = false;
        SettingChanged(sender, e);
    }

    private void PresetChanged(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        var preset = SelectedTag(PresetCombo);
        if (preset == "Low")
        {
            ScanRateSlider.Value = 8;
            BodyRateSlider.Value = 4;
            SegRateSlider.Value = 2;
        }
        else if (preset == "Balanced")
        {
            ScanRateSlider.Value = 12;
            BodyRateSlider.Value = 6;
            SegRateSlider.Value = 4;
        }
        else if (preset == "High")
        {
            ScanRateSlider.Value = 15;
            BodyRateSlider.Value = 8;
            SegRateSlider.Value = 6;
        }
        UpdateValueLabels();
        ReadSettingsFromUi();
        _engine.UpdateSettings(_settings);
    }

    private async void RefreshDisplays_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.IsRunning)
        {
            MessageBox.Show(this, "Stop protection before refreshing displays; active capture sessions cannot be re-bound safely.",
                "Stop protection first", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        RefreshDisplays();
        await Task.CompletedTask;
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e) => SaveSettings(showStatus: true);

    private void SaveSettings(bool showStatus)
    {
        if (_saving) return;
        try
        {
            _saving = true;
            ReadSettingsFromUi();
            _settingsStore.Save(_settings);
            _engine.UpdateSettings(_settings);
            if (showStatus) SettingsStatus.Text = "Saved to " + _settingsStore.Path;
        }
        catch (Exception ex)
        {
            if (showStatus) SettingsStatus.Text = "Could not save: " + ex.Message;
        }
        finally
        {
            _saving = false;
        }
    }

    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_starting || _stopping || _engine.IsRunning)
        {
            MessageBox.Show(this, "Stop protection before resetting detection targets.", "Stop protection first");
            return;
        }
        if (MessageBox.Show(this, "Reset every setting to its default? Session history and exported files are not touched.",
                "Reset settings", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _settingsStore.ResetToDefaults();
        _settings = _settingsStore.Load();
        _engine.UpdateSettings(_settings);
        LoadSettingsToUi();
        SettingsStatus.Text = "Defaults restored.";
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        AppPaths.EnsureDirectories();
        OpenPath(AppPaths.Root);
    }

    private void OpenModelsFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = ModelLocator.Find();
        if (path is null)
            MessageBox.Show(this, "Models were not found. Run scripts/fetch-models.ps1.", "Models missing",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        else OpenPath(path);
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DiagnosticsBox.Text);
            ProbeStatus.Text = "Diagnostics copied to the clipboard.";
        }
        catch (Exception ex)
        {
            ProbeStatus.Text = "Clipboard unavailable: " + ex.Message;
        }
    }

    private async void RunProbe_Click(object sender, RoutedEventArgs e)
    {
        ProbeStatus.Text = "Starting a 2-second WGC probe…";
        try
        {
            if (_engine.IsRunning)
            {
                var status = _engine.GetStatus();
                ProbeStatus.Text = "Protection is already running: " +
                    string.Join("; ", status.Monitors.Select(m =>
                        $"{m.Name} {m.Width}×{m.Height} capture {m.CaptureFps:0.0} fps"));
                return;
            }

            var monitors = MonitorEnumerator.Enumerate();
            if (monitors.Count == 0)
            {
                ProbeStatus.Text = "No displays were found.";
                return;
            }
            await using var probe = new CaptureService();
            await probe.StartAsync(monitors, new CaptureOptions(10, false));
            await Task.Delay(1500);
            var statuses = probe.GetStatus();
            using var frame = probe.TryGrabLatestFrame(monitors[0].Id);
            if (frame is null) ProbeStatus.Text = "Probe started but produced no frame.";
            else
            {
                var hash = Convert.ToHexString(SHA256.HashData(frame.Span))[..16];
                ProbeStatus.Text = $"Probe OK: {frame.Width}×{frame.Height}, {statuses.Sum(s => s.FramesDelivered)} frames, sha256 {hash}…";
            }
            await probe.ShutdownAsync();
        }
        catch (Exception ex)
        {
            ProbeStatus.Text = "Probe failed: " + ex.Message;
        }
    }

    private void OpenDocs_Click(object sender, RoutedEventArgs e)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 7 && dir is not null; i++)
        {
            var docs = Path.Combine(dir, "docs");
            if (Directory.Exists(docs)) { OpenPath(docs); return; }
            dir = Path.GetDirectoryName(dir);
        }
        MessageBox.Show(this, "Docs are not installed beside this build. See README.md in the source repository.",
            "Docs not found", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    private void UpdateDiagnostics(EngineStatus? status = null)
    {
        if (DiagnosticsBox is null) return;
        status ??= _engine.GetStatus();
        var sb = new StringBuilder();
        sb.AppendLine($"VeilDesk {typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"} diagnostics");
        sb.AppendLine($"Generated: {DateTimeOffset.Now:O}");
        sb.AppendLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"Process: {Environment.ProcessId}, 64-bit={Environment.Is64BitProcess}");
        sb.AppendLine($"Data root: {AppPaths.Root}");
        sb.AppendLine($"Models: {ModelLocator.Find() ?? "(missing)"}");
        sb.AppendLine($"Provider: {status.Provider}");
        if (!string.IsNullOrWhiteSpace(status.DirectMLError)) sb.AppendLine($"DirectML error: {status.DirectMLError}");
        sb.AppendLine($"Running: {status.Running}; overlay={status.OverlayRunning}; excluded={status.OverlayExcludedFromCapture}");
        sb.AppendLine($"Totals: analyses={status.TotalAnalyses}, detectionEvents={status.TotalDetections}, peakTracks={status.PeakTracks}");
        sb.AppendLine($"WorkingSet: {Environment.WorkingSet / (1024 * 1024)} MB; GC {GC.GetTotalMemory(false) / (1024 * 1024)} MB");
        foreach (var monitor in status.Monitors)
        {
            sb.AppendLine($"Display {monitor.MonitorId}: {monitor.Width}x{monitor.Height}; capture={monitor.CaptureFps:0.0}fps; analysis={monitor.AnalysisFps:0.0}fps; p50={monitor.P50Ms:0.0}ms; p95={monitor.P95Ms:0.0}ms; tracks={monitor.Tracks}; regions={monitor.Regions}; failClosed={monitor.FailClosed}");
            if (!string.IsNullOrWhiteSpace(monitor.Error)) sb.AppendLine($"  error: {monitor.Error}");
            if (!string.IsNullOrWhiteSpace(monitor.CaptureError)) sb.AppendLine($"  capture error: {monitor.CaptureError}");
        }
        if (!string.IsNullOrWhiteSpace(status.Error)) sb.AppendLine("Engine error: " + status.Error);

        DiagnosticsBox.Text = sb.ToString();
        AboutProvider.Text = status.Provider + (status.DirectMLError is null ? "" : " (DirectML fallback)");
    }

    private void UpdateProviderLabels()
    {
        AboutProvider.Text = _engine.ProviderDetail;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyDarkTitleBar();
        if (NavList.SelectedIndex != 0) NavList.SelectedIndex = 0;
        ApplyPage(0, Pages[0]);
        UpdateStatus();
        if (_settings.Ui.StartMinimized) WindowState = WindowState.Minimized;
    }

    private async void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closingStarted) return;
        _closingStarted = true;
        AppLog.Write("window-closing");
        _statusTimer.Stop();
        _saveTimer.Stop();
        try
        {
            if (_recorder is not null)
            {
                await _recorder.StopAsync();
                _recorder = null;
            }
            await _engine.DisposeAsync();
        }
        catch { }
        _allowClose = true;
        _ = Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.Normal);
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (IsLoaded) ApplyDarkTitleBar();
    }

    private void ApplyDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var enabled = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref enabled, sizeof(int));
        }
        catch { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
