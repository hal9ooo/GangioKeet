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

    private readonly System.Windows.Threading.DispatcherTimer _levelTimer;
    private double _currentLevel;

    public PillWindow()
    {
        InitializeComponent();

        SourceInitialized += OnSourceInitialized;
        _ = new WindowInteropHelper(this).EnsureHandle();
        PositionPill();

        _levelTimer = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(50),
            System.Windows.Threading.DispatcherPriority.Normal,
            (_, _) => UpdateLevelDot(),
            Dispatcher);
    }

    /// <summary>Modalità di posizionamento: <c>center_bottom</c> (default) o <c>cursor</c>.</summary>
    public string PositionMode { get; set; } = "center_bottom";

    public void Show(PillState state, string? message, DictationProfile? profile)
    {
        Dispatcher.Invoke(() =>
        {
            PositionPill();
            Show();
            ActivateStyles();

            StatusText.Text = message ?? StateToDefaultMessage(state);
            StatusDot.Fill = StateToColor(state);
            _levelTimer.IsEnabled = state is PillState.Recording;
        });
    }

    public new void Hide()
    {
        Dispatcher.Invoke(() =>
        {
            _levelTimer.IsEnabled = false;
            base.Hide();
        });
    }

    public void SetLevel(double level)
    {
        _currentLevel = level;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ActivateStyles();
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

    private void UpdateLevelDot()
    {
        double size = 6 + _currentLevel * 8; // da 6 a 14 px
        StatusDot.Width = size;
        StatusDot.Height = size;
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

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1;
        double scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1;

        RECT work = info.Work;
        PillWorkArea area = new(
            work.Left / scaleX,
            work.Top / scaleY,
            (work.Right - work.Left) / scaleX,
            (work.Bottom - work.Top) / scaleY);

        (double left, double top) = string.Equals(PositionMode, "cursor", StringComparison.OrdinalIgnoreCase)
            ? PillPlacement.NearCursor(area, cursor.X / scaleX, cursor.Y / scaleY, Width, Height)
            : PillPlacement.CenterBottom(area, Width, Height);

        Left = left;
        Top = top;
    }

    private static string StateToDefaultMessage(PillState state) => state switch
    {
        PillState.Arming => "Connessione…",
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
