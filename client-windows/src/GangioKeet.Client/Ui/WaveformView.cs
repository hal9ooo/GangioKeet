using System.Windows;
using System.Windows.Media;

namespace GangioKeet.Client.Ui;

/// <summary>
/// Small symmetric waveform for the pill. The history level is drawn as
/// centered capsule bars mirrored around the horizontal centerline.
/// A thin green rest line is always present underneath: when the voice stops,
/// the bars collapse onto it instead of being cleared, and slow black pulses
/// travel along it at the same one-slot-per-frame speed as the history.
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    private const double RestThickness = 3.2;

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

        // La riga di riposo c'è sempre: le barre alte la coprono, il silenzio
        // la riporta in primo piano senza cancellare nulla.
        Rect line = BuildIdleLine(width, height);
        drawingContext.PushOpacity(0.35 + 0.65 * _idle);
        drawingContext.DrawRoundedRectangle(
            RestLineBrush, null, line, line.Height / 2, line.Height / 2);
        drawingContext.Pop();

        foreach (Rect bar in BuildBars(_levels, width, height))
        {
            double radius = bar.Width / 2;
            drawingContext.DrawRoundedRectangle(WaveBrush, null, bar, radius, radius);
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

        double maxHeight = Math.Max(6, height - 6);
        double levelStep = (maxHeight - RestThickness) / 3;
        double slot = width / count;
        double barWidth = Math.Clamp(slot - 1, 1.5, 3.0);
        double gap = Math.Max(0, slot - barWidth);
        double center = height / 2;
        Rect[] bars = new Rect[count];
        for (int i = 0; i < count; i++)
        {
            double taper = 0.84 + 0.16 * Math.Sin(Math.PI * (i + 0.5) / count);
            double barHeight = Math.Max(
                RestThickness,
                (RestThickness + values[i] * levelStep) * taper);
            double x = i * slot + gap / 2;
            bars[i] = new Rect(x, center - barHeight / 2, barWidth, barHeight);
        }
        return bars;
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
        double minRadius = 0.7;
        double maxRadius = 1.6;
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

    private static Brush CreateWaveBrush()
    {
        LinearGradientBrush brush = new(
            Color.FromRgb(0x9B, 0xF5, 0xA8),
            Color.FromRgb(0x2E, 0xA0, 0x43),
            new Point(0, 0),
            new Point(0, 1));
        brush.Freeze();
        return brush;
    }

    private static Brush CreateRestLineBrush()
    {
        SolidColorBrush brush = new(Color.FromRgb(0x4C, 0xAF, 0x50));
        brush.Freeze();
        return brush;
    }
}
