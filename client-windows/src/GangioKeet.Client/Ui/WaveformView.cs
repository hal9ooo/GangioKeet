using System.Windows;
using System.Windows.Media;

namespace GangioKeet.Client.Ui;

/// <summary>
/// Wispr Flow-style waveform for the pill: ten off-white bars whose heights
/// follow a travelling sine wave. The animation is continuous and does not
/// depend on the microphone history, so there is no silent/reset state.
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    public const int BarCount = 10;

    private const double MinLevel = 0.24;
    private const double MaxLevel = 0.94;
    private const double Speed = 2.4;
    private const double WaveSpan = 1.7;
    private const double BarRatio = 0.53;

    private static readonly Brush WaveBrush = CreateWaveBrush();
    private double _time;

    public void SetTime(double seconds)
    {
        _time = seconds;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 100 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 20 : availableSize.Height);

    protected override void OnRender(DrawingContext drawingContext)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double originXPx = 0;
        double originYPx = 0;
        Point origin = default;
        if (Window.GetWindow(this) is { } window)
        {
            origin = TranslatePoint(new Point(0, 0), window);
        }
        else
        {
            // Nei render off-screen (preview/test) non c'è una Window: usiamo
            // la radice visiva per allineare comunque i pixel assoluti.
            UIElement root = this;
            while (VisualTreeHelper.GetParent(root) is UIElement parent)
            {
                root = parent;
            }
            if (!ReferenceEquals(root, this))
            {
                origin = TranslatePoint(new Point(0, 0), root);
            }
        }
        originXPx = origin.X * dpi.DpiScaleX;
        originYPx = origin.Y * dpi.DpiScaleY;

        double center = (Math.Round(originYPx + (height / 2) * dpi.DpiScaleY) - originYPx)
            / dpi.DpiScaleY;

        IReadOnlyList<Rect> logicalBars = BuildWaveBars(width, height, _time);
        foreach (Rect bar in BuildPixelBars(
            logicalBars, width, center, dpi.DpiScaleX, dpi.DpiScaleY, originXPx))
        {
            drawingContext.DrawRectangle(WaveBrush, null, bar);
        }
    }

    /// <summary>
    /// Riproduce la formula della wave di Wispr Flow: un seno che attraversa
    /// le barre, con una leggera campana che smorza le estremità.
    /// </summary>
    internal static IReadOnlyList<Rect> BuildWaveBars(
        double width,
        double height,
        double time)
    {
        int count = BarCount;
        double slot = width / count;
        double barWidth = slot * BarRatio;
        double gap = slot - barWidth;
        double center = height / 2;
        double t = time * Speed;

        Rect[] bars = new Rect[count];
        for (int i = 0; i < count; i++)
        {
            double xi = count > 1 ? i / (double)(count - 1) : 0.5;
            double wave = 0.6 * Math.Sin((xi * WaveSpan - t) * Math.Tau) +
                          0.4 * Math.Sin((xi * WaveSpan * 0.5 - t * 0.6) * Math.Tau + 1.7);
            double s = (0.5 + 0.5 * wave) * (0.7 + 0.3 * Math.Sin(xi * Math.PI));
            double barHeight = (MinLevel + (MaxLevel - MinLevel) * s) * height;
            double x = i * slot + gap / 2;
            bars[i] = new Rect(x, center - barHeight / 2, barWidth, barHeight);
        }
        return bars;
    }

    /// <summary>
    /// Ricalcola le barre su una griglia fisica uniforme: tutte hanno la stessa
    /// larghezza e lo stesso passo in pixel, così non compaiono righette più
    /// spesse e più sottili quando la finestra è a scala frazionaria.
    /// </summary>
    internal static IReadOnlyList<Rect> BuildPixelBars(
        IReadOnlyList<Rect> bars,
        double width,
        double center,
        double scaleX,
        double scaleY,
        double originXPx = 0)
    {
        int count = bars.Count;
        int widthPx = Math.Max(count, (int)Math.Round(width * scaleX));
        int slotPx = Math.Max(2, widthPx / count);
        int barPx = Math.Max(1, (int)Math.Round(slotPx * BarRatio));
        if (barPx >= slotPx) barPx = slotPx - 1;
        int totalPx = slotPx * count;
        int startPx = Math.Max(0, (widthPx - totalPx) / 2);

        // L'origine della view può cadere su un quarto di pixel: spostiamo
        // lo start in modo che i bordi assoluti finiscano su pixel interi.
        int absoluteStartPx = (int)Math.Round(originXPx + startPx);
        double startDip = (absoluteStartPx - originXPx) / scaleX;
        double pitchDip = slotPx / scaleX;

        Rect[] rendered = new Rect[count];
        for (int i = 0; i < count; i++)
        {
            double half = Math.Round((bars[i].Height / 2) * scaleY) / scaleY;
            half = Math.Max(0.5 / scaleY, half);
            rendered[i] = new Rect(
                startDip + i * pitchDip,
                center - half,
                barPx / scaleX,
                half * 2);
        }
        return rendered;
    }

    private static Brush CreateWaveBrush()
    {
        SolidColorBrush brush = new(Color.FromRgb(0xE2, 0xDD, 0xD0));
        brush.Freeze();
        return brush;
    }
}
