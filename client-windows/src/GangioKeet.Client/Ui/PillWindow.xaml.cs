using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using GangioKeet.Client.Context;
using GangioKeet.Client.Controller;

namespace GangioKeet.Client.Ui;

/// <summary>
/// Finestra fluttuante non attivabile che mostra lo stato della dettatura.
/// Usa Win32 style WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_TRANSPARENT
/// per non rubare il focus e non apparire nella taskbar. Posizionata vicino al cursore o
/// al centro-basso del monitor.
/// </summary>
public sealed partial class PillWindow : Window, IPillView
{
    private const int WsExNoactivate = 0x08000000;
    private const int WsExToolwindow = 0x00000080;
    private const int WsExTopmost = 0x00000008;
    private const int WsExTransparent = 0x00000020;

    private const int GwlExstyle = -20;

    private static readonly SolidColorBrush GreenBrush = new(Color.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly SolidColorBrush YellowBrush = new(Color.FromRgb(0xFF, 0xEB, 0x3B));
    private static readonly SolidColorBrush RedBrush = new(Color.FromRgb(0xF4, 0x43, 0x36));
    private static readonly SolidColorBrush GrayBrush = new(Color.FromRgb(0x9E, 0x9E, 0x9E));
    private static readonly SolidColorBrush OrangeBrush = new(Color.FromRgb(0xFF, 0x98, 0x00));

    private readonly WaveformHistory _waveform = new();
    private readonly FrameGate _frameGate = new(TimeSpan.FromMilliseconds(16));
    private double _currentLevel;
    private double _idle;
    private double _idlePhase;
    private bool _rendering;
    private TimeSpan _lastSample;

    public PillWindow()
    {
        InitializeComponent();

        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => ApplyRoundedRegion();
        DpiChanged += (_, _) => ApplyRoundedRegion();
        Closed += (_, _) => StopRendering();
        _ = new WindowInteropHelper(this).EnsureHandle();
        PositionPill();
    }

    /// <summary>Modalità di posizionamento: <c>center_bottom</c> (default) o <c>cursor</c>.</summary>
    public string PositionMode { get; set; } = "center_bottom";

    public void Show(PillState state, string? message, DictationProfile? profile)
    {
        RunOnUi(() =>
        {
            PositionPill();
            Show();
            ActivateStyles();

            StatusText.Text = message ?? StateToDefaultMessage(state);
            StatusDot.Fill = StateToColor(state);

            bool recording = state is PillState.Recording;
            WaveformPanel.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Visibility = recording ? Visibility.Collapsed : Visibility.Visible;

            if (recording)
            {
                _frameGate.Reset();
                _lastSample = default;
                _waveform.Reset();
                _idle = 0;
                _idlePhase = 0;
                RenderWaveform();
                StartRendering();
            }
            else
            {
                StopRendering();
            }
        });
    }

    public new void Hide()
    {
        RunOnUi(() =>
        {
            StopRendering();
            base.Hide();
        });
    }

    private void RunOnUi(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            // Mai Invoke bloccante dal thread degli hook: allo shutdown il Dispatcher UI
            // è fermo e un Invoke causerebbe un deadlock.
            Dispatcher.BeginInvoke(action);
        }
    }

    public void SetLevel(double level)
    {
        Volatile.Write(ref _currentLevel, level);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ActivateStyles();
        ApplyRoundedRegion();
    }

    private void ApplyRoundedRegion()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource hwnd) return;
        if (!GetWindowRect(hwnd.Handle, out RECT rect)) return;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int radius = Math.Max(4, (int)Math.Round(28 * scale));
        nint region = CreateRoundRectRgn(0, 0,
            rect.Right - rect.Left + 1, rect.Bottom - rect.Top + 1, radius, radius);
        if (region == 0) return;
        if (SetWindowRgn(hwnd.Handle, region, redraw: true) == 0)
            DeleteObject(region);
    }

    private void ActivateStyles()
    {
        if (PresentationSource.FromVisual(this) is HwndSource hwnd)
        {
            nint handle = hwnd.Handle;
            int exStyle = GetWindowLong(handle, GwlExstyle);
            SetWindowLong(handle, GwlExstyle, exStyle | WsExNoactivate | WsExToolwindow | WsExTopmost | WsExTransparent);
        }
    }

    private void UpdateLevelDot(TimeSpan elapsed)
    {
        double level = Volatile.Read(ref _currentLevel);
        _waveform.Push(level, elapsed);

        // Senza voce la forma d'onda non si spegne: sfuma in una piccola onda
        // ambientale che continua a muoversi per tutta la registrazione.
        double dt = Math.Clamp(elapsed.TotalSeconds, 0.001, 0.25);
        _idlePhase = (_idlePhase + dt * Math.Tau / 2.4) % Math.Tau;
        double target = _waveform.IsSilent ? 1 : 0;
        double step = dt / (target > _idle ? 0.35 : 0.12);
        _idle = target > _idle
            ? Math.Min(target, _idle + step)
            : Math.Max(target, _idle - step);

        // Scala solo il disegno: il layout della pill resta fermo a 60 Hz.
        double scale = 0.43 + level * 0.57;
        if (_idle > 0)
        {
            double breathe = 0.5 + 0.5 * Math.Sin(_idlePhase * 2);
            scale = Math.Min(1, scale + _idle * (0.10 + 0.08 * breathe));
        }

        StatusDotScale.ScaleX = scale;
        StatusDotScale.ScaleY = scale;

        RenderWaveform();
    }

    private void StartRendering()
    {
        if (_rendering) return;
        _rendering = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopRendering()
    {
        if (!_rendering) return;
        _rendering = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_rendering) return;
        TimeSpan now = e is RenderingEventArgs args
            ? args.RenderingTime
            : TimeSpan.FromTicks(Environment.TickCount64 * TimeSpan.TicksPerMillisecond);
        if (!_frameGate.ShouldRender(now)) return;

        TimeSpan elapsed = _lastSample == default
            ? TimeSpan.FromMilliseconds(50)
            : now - _lastSample;
        _lastSample = now;
        UpdateLevelDot(elapsed);
    }

    private void RenderWaveform()
    {
        WaveformPanel.SetLevels(_waveform.Levels, _idle, _idlePhase);
    }

    private void PositionPill()
    {
        if (!GetCursorPos(out Point cursor))
        {
            return;
        }

        nint monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        MonitorInfo info = new() { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        // Il DPI rilevante è quello del monitor sotto il cursore, non quello della
        // finestra (che al primo Show è ancora sul monitor primario). Con
        // PerMonitorV2 GetDpiForMonitor restituisce il DPI effettivo del target.
        uint dpiX = 96;
        uint dpiY = 96;
        if (GetDpiForMonitor(monitor, MdtEffectiveDpi, out uint monitorDpiX, out uint monitorDpiY) == 0)
        {
            dpiX = monitorDpiX > 0 ? monitorDpiX : 96;
            dpiY = monitorDpiY > 0 ? monitorDpiY : 96;
        }

        double scaleX = dpiX / 96.0;
        double scaleY = dpiY / 96.0;

        RECT work = info.Work;
        PillWorkArea area = new(
            work.Left,
            work.Top,
            work.Right - work.Left,
            work.Bottom - work.Top);

        double widthPx = Width * scaleX;
        double heightPx = Height * scaleY;

        (double left, double top) = string.Equals(PositionMode, "cursor", StringComparison.OrdinalIgnoreCase)
            ? PillPlacement.NearCursor(
                area,
                cursor.X,
                cursor.Y,
                widthPx,
                heightPx,
                PillPlacement.CursorOffset * scaleX)
            : PillPlacement.CenterBottom(
                area,
                widthPx,
                heightPx,
                PillPlacement.BottomMargin * scaleY);

        // SetWindowPos lavora in pixel fisici: evita la doppia conversione DIP/WPF
        // quando la finestra cambia monitor con DPI diversi.
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle != 0)
        {
            SetWindowPos(
                handle,
                IntPtr.Zero,
                (int)Math.Round(left),
                (int)Math.Round(top),
                0,
                0,
                SwpNoSize | SwpNoZOrder | SwpNoActivate);
        }
    }

    private static string StateToDefaultMessage(PillState state) => state switch
    {
        PillState.Arming => "Avvio…",
        PillState.Recording => "Registrazione…",
        PillState.Processing => "Elaborazione…",
        PillState.Warning => "Testo non rifinito",
        PillState.Error => "Errore",
        PillState.EmptyResult => "Testo vuoto",
        _ => "",
    };

    private static Brush StateToColor(PillState state) => state switch
    {
        PillState.Arming => YellowBrush,
        PillState.Recording => GreenBrush,
        PillState.Processing => YellowBrush,
        PillState.Warning => OrangeBrush,
        PillState.Error => RedBrush,
        PillState.EmptyResult => GrayBrush,
        _ => GrayBrush,
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hWnd,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out RECT rect);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom,
        int width, int height);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(nint hWnd, nint region, bool redraw);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint handle);

    private const int MdtEffectiveDpi = 0;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public RECT Monitor;
        public RECT Work;
        public uint Flags;
    }
}
