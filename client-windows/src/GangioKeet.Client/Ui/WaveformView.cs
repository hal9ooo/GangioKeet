using System.Windows;
using System.Windows.Media;

namespace GangioKeet.Client.Ui;

/// <summary>
/// Small symmetric waveform for the pill. The history level is drawn as
/// centered rectangular bars mirrored around the horizontal centerline.
/// A thin off-white rest line is always present underneath: when the voice
/// stops, the bars collapse onto it instead of being cleared, and small black
/// pulses travel along it at the same one-slot-per-frame speed as the history.
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    private const double RestThickness = 1.8;

    private static readonly Brush WaveBrush = CreateWaveBrush();
    private static readonly Brush RestLineBrush = CreateRestLineBrush();

    private IReadOnlyList<byte> _levels = [];
    private double _idle;
    private double _travelSlots;

    /// <summary>
    /// Aggiorna il livello. <paramref name="idle"/> (0..1) fa emergere i puntini
    /// quando il microfono è in silenzio; <paramref name="travelSlots"/> è lo
    /// stesso contatore di campioni che fa scorrere la cronologia della wave.
    /// </summary>
    public void SetLevels(
        IReadOnlyList<byte> levels,
        double idle = 0,
        double travelSlots = 0)
    {
        _levels = levels ?? [];
        _idle = Math.Clamp(idle, 0, 1);
        _travelSlots = travelSlots;
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

        // Le barre sono molto sottili: senza allineamento ai pixel fisici
        // l'antialiasing le fa sembrare smerate e pastello, soprattutto a 125%.
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

        // La riga di riposo c'è sempre: le barre alte la coprono, il silenzio
        // la riporta in primo piano senza cancellare nulla.
        Rect line = SnapAroundCenter(
            BuildIdleLine(width, height), center, dpi.DpiScaleX, dpi.DpiScaleY);
        drawingContext.PushOpacity(0.35 + 0.65 * _idle);
        drawingContext.DrawRectangle(RestLineBrush, null, line);
        drawingContext.Pop();

        IReadOnlyList<Rect> logicalBars = BuildBars(_levels, width, height);
        foreach (Rect bar in BuildPixelBars(
            logicalBars, width, center, dpi.DpiScaleX, dpi.DpiScaleY, originXPx))
        {
            drawingContext.DrawRectangle(WaveBrush, null, bar);
        }

        if (_idle > 0)
        {
            drawingContext.PushOpacity(_idle);
            foreach (Rect dot in BuildIdleDots(width, height, _travelSlots))
            {
                drawingContext.DrawEllipse(
                    Brushes.Black,
                    null,
                    new Point(dot.Left + dot.Width / 2, dot.Top + dot.Height / 2),
                    dot.Width / 2,
                    dot.Height / 2);
            }
            drawingContext.Pop();
        }
    }

    internal static IReadOnlyList<Rect> BuildBars(IReadOnlyList<byte> levels, double width, double height)
    {
        const int count = WaveformHistory.BarCount;
        byte[] values = new byte[count];
        int take = Math.Min(levels.Count, count);
        int offset = count - take;
        for (int i = 0; i < take; i++)
            values[offset + i] = levels[levels.Count - take + i];

        double maxHeight = Math.Max(6, height - 1);
        double levelStep = (maxHeight - RestThickness) / 3;
        double slot = width / count;
        double barWidth = Math.Clamp(slot * 0.42, 1.5, 2.2);
        double gap = Math.Max(0, slot - barWidth);
        double center = height / 2;
        Rect[] bars = new Rect[count];
        for (int i = 0; i < count; i++)
        {
            double barHeight = Math.Max(RestThickness, RestThickness + values[i] * levelStep);
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
        int barPx = Math.Max(1, (int)Math.Round(slotPx * 0.42));
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

    internal static Rect BuildIdleLine(double width, double height) =>
        new(0, height / 2 - RestThickness / 2, width, RestThickness);

    internal static IReadOnlyList<Rect> BuildIdleDots(
        double width,
        double height,
        double travelSlots)
    {
        const int count = 3;
        double center = height / 2;
        double minRadius = 0.5;
        double maxRadius = 1.2;
        double slot = width / WaveformHistory.BarCount;

        Rect[] dots = new Rect[count];
        for (int i = 0; i < count; i++)
        {
            double slotIndex = (travelSlots + i * (WaveformHistory.BarCount / (double)count))
                % WaveformHistory.BarCount;
            if (slotIndex < 0) slotIndex += WaveformHistory.BarCount;

            // Stessa cadenza della cronologia: una barra per fotogramma,
            // quindi i puntini scorrono da destra verso sinistra.
            double x = width - slotIndex * slot;
            double pulse = 0.5 + 0.5 * Math.Sin(travelSlots * 0.06 + i * 2.1);
            double radius = minRadius + (maxRadius - minRadius) * pulse;
            dots[i] = new Rect(x - radius, center - radius, radius * 2, radius * 2);
        }
        return dots;
    }

    internal static Rect SnapAroundCenter(
        Rect rect,
        double center,
        double scaleX,
        double scaleY)
    {
        double left = Math.Round(rect.Left * scaleX) / scaleX;
        double right = Math.Round(rect.Right * scaleX) / scaleX;
        double half = Math.Round((rect.Height / 2) * scaleY) / scaleY;
        half = Math.Max(0.5 / scaleY, half);

        return new Rect(
            left,
            center - half,
            Math.Max(1 / scaleX, right - left),
            half * 2);
    }

    private static Brush CreateWaveBrush()
    {
        SolidColorBrush brush = new(Color.FromRgb(0xE2, 0xDD, 0xD0));
        brush.Freeze();
        return brush;
    }

    private static Brush CreateRestLineBrush()
    {
        SolidColorBrush brush = new(Color.FromRgb(0xCF, 0xC9, 0xBA));
        brush.Freeze();
        return brush;
    }
}
