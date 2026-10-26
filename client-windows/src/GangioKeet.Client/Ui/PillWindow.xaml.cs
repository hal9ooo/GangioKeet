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

        _levelTimer = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(50),
            System.Windows.Threading.DispatcherPriority.Normal,
            (_, _) => UpdateLevelDot(),
            Dispatcher);
    }

    public void Show(PillState state, string? message, DictationProfile? profile)
    {
        Dispatcher.Invoke(() =>
        {
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
}
