using System.Windows;
using System.Windows.Media;

namespace GangioKeet.Client.Ui;

/// <summary>
/// Wispr Flow-style waveform for the pill. The real Wispr pill is a dense
/// ribbon of thin bars that scrolls sideways while its envelope slowly
/// morphs; it is not a handful of bars oscillating in place. We reproduce
/// that look with <see cref="BarCount"/> thin bars whose heights come from
/// a scrolling value-noise field. The animation is decorative and
/// continuous: it never clears or waits for microphone input.
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    /// <summary>Bar count of the Wispr pill Lottie (`test wave form_v1`).</summary>
    public const int BarCount = 23;

    private const double MinLevel = 0.06;
    private const double MaxLevel = 0.89;
    private const double BarRatio = 0.42;
    private const double ScrollBarsPerSecond = 14;

    private static readonly Brush WaveBrush = CreateWaveBrush();
    private double _time;
    private double _level;

    public void SetTime(double seconds)
    {
        _time = seconds;
        InvalidateVisual();
    }

    /// <summary>
    /// Livello del microfono già smussato (0..1). A differenza del Lottie,
    /// che è puramente decorativo, qui il volume gonfia l'inviluppo delle
    /// barre: a riposo restano al 55%, parlando arrivano al 100%.
    /// </summary>
    public void SetLevel(double level)
    {
        _level = Math.Clamp(level, 0, 1);
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

        IReadOnlyList<Rect> logicalBars = BuildWaveBars(width, height, _time, _level);
        foreach (Rect bar in BuildPixelBars(
            logicalBars, width, center, dpi.DpiScaleX, dpi.DpiScaleY, originXPx))
        {
            drawingContext.DrawRectangle(WaveBrush, null, bar);
        }
    }

    /// <summary>
    /// Costruisce le barre campionando un campo di rumore che scorre verso
    /// sinistra. Il campo cambia anche lentamente nel tempo, così la forma
    /// non è una sagoma rigida che si ripete.
    /// </summary>
    internal static IReadOnlyList<Rect> BuildWaveBars(
        double width,
        double height,
        double time,
        double inputLevel = 1)
    {
        int count = BarCount;
        double slot = width / count;
        double barWidth = slot * BarRatio;
        double gap = slot - barWidth;
        double center = height / 2;
        double scroll = time * ScrollBarsPerSecond;

        // A riposo la wave resta solo un accenno; con la voce si gonfia
        // rapidamente. Il picco PCM sta di solito tra 0,1 e 0,5, quindi la
        // curva resta più bassa della radice quarta per dare uno stacco netto.
        double response = Math.Pow(Math.Clamp(inputLevel, 0, 1), 0.6);
        double gain = 0.16 + 0.84 * response;

        Rect[] bars = new Rect[count];
        for (int i = 0; i < count; i++)
        {
            double u = i + scroll;
            double level = SampleLevel(u, time);
            double barHeight = (MinLevel + (MaxLevel - MinLevel) * level) * height * gain;
            double x = i * slot + gap / 2;
            bars[i] = new Rect(x, center - barHeight / 2, barWidth, barHeight);
        }
        return bars;
    }

    /// <summary>
    /// Altezza normalizzata (0..1) di una barra. Due ottave di rumore
    /// interpolato danno il profilo irregolare della pill di Wispr, mentre
    /// il termine temporale lo fa respirare senza scatti.
    /// </summary>
    private static double SampleLevel(double position, double time)
    {
        double coarse = Noise(position * 0.23 + 41.7 - time * 0.030);
        double medium = Noise(position * 0.61 + 0.3 + time * 0.050);
        double fine = Noise(position * 1.37 + 17.9 - time * 0.020);
        double mixed = coarse * 0.48 + medium * 0.34 + fine * 0.18;

        // Il rumore si concentra intorno a 0,5: allarghiamo la distribuzione
        // per avere sia puntini bassi sia barre alte come nel video.
        mixed = Math.Clamp((mixed - 0.5) * 1.8 + 0.5, 0, 1);
        return Math.Pow(mixed, 1.08);
    }

    private static double Noise(double x)
    {
        int cell = (int)Math.Floor(x);
        double fraction = x - cell;
        double eased = fraction * fraction * (3 - 2 * fraction);
        return Hash(cell) * (1 - eased) + Hash(cell + 1) * eased;
    }

    private static double Hash(int value)
    {
        uint hash = (uint)value * 0x9E3779B1u;
        hash ^= hash >> 16;
        hash *= 0x85EBCA6Bu;
        hash ^= hash >> 13;
        return (hash & 0xFFFFFF) / 16777215.0;
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
        SolidColorBrush brush = new(Color.FromRgb(0xED, 0xE7, 0xDA));
        brush.Freeze();
        return brush;
    }
}
