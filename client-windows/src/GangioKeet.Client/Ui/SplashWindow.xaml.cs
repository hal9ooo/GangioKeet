using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace GangioKeet.Client.Ui;

/// <summary>
/// Splash d'avvio: cover, barra di avanzamento in loop e chiusura automatica.
/// Non tocca la taskbar e non ruba il focus (<c>ShowActivated=False</c>).
/// </summary>
public sealed partial class SplashWindow : Window
{
    public const string Payoff = "A one-user project putting local GPUs to work.";

    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(220);

    private readonly DispatcherTimer _timer;
    private bool _closing;

    public SplashWindow(TimeSpan duration)
    {
        InitializeComponent();

        _timer = new DispatcherTimer(duration, DispatcherPriority.Background, (_, _) => Dismiss(), Dispatcher);

        Loaded += OnLoaded;
    }

    /// <summary>Chiude subito (con fade) — usato anche quando l'avvio fallisce.</summary>
    public void Dismiss()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        _timer.Stop();

        DoubleAnimation fade = new(0, FadeDuration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        fade.Completed += (_, _) => Close();

        Root.BeginAnimation(OpacityProperty, fade);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        DoubleAnimation fade = new(1, FadeDuration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        Root.BeginAnimation(OpacityProperty, fade);

        DoubleAnimation grow = new(1, FadeDuration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        SplashScale.BeginAnimation(
            System.Windows.Media.ScaleTransform.ScaleXProperty,
            grow);
        SplashScale.BeginAnimation(
            System.Windows.Media.ScaleTransform.ScaleYProperty,
            grow.Clone());

        DoubleAnimation bar = new(640, TimeSpan.FromSeconds(1.1))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        BarShift.BeginAnimation(
            System.Windows.Media.TranslateTransform.XProperty,
            bar);

        _timer.Start();
    }
}
