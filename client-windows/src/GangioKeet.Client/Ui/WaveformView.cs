using System.Windows;
using System.Windows.Media;

namespace GangioKeet.Client.Ui;

/// <summary>
/// Small symmetric waveform for the pill. The history level is drawn as
/// centered capsule bars mirrored around the horizontal centerline.
/// During silence it crossfades to a flat green line crossed by slow black
/// pulses, so the pill keeps looking alive without pretending to hear audio.
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    private static readonly Brush WaveBrush = CreateWaveBrush();
    private static readonly Brush IdleLineBrush = CreateIdleLineBrush();
    private static readonly Pen CenterPen = CreateCenterPen();

    private IReadOnlyList<byte> _levels = [];
    private double _idle;
    private double _idlePhase;

    /// <summary>
    /// Aggiorna il livello. <paramref name="idle"/> (0..1) dissolve la forma
    /// d'onda nella riga piatta animata quando il microfono non rileva voce.
    /// </summary>
    public void SetLevels(IReadOnlyList<byte> levels, double idle = 0, double idlePhase = 0)
    {
        _levels = levels ?? [];
        _idle = Math.Clamp(idle, 0, 1);
        _idlePhase = idlePhase;
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

        drawingContext.DrawLine(CenterPen, new Point(0, height / 2), new Point(width, height / 2));

        if (_levels.Count > 0 && _idle < 1)
        {
            drawingContext.PushOpacity(1 - _idle);
            foreach (Rect bar in BuildBars(_levels, width, height))
            {
                double radius = bar.Width / 2;
                drawingContext.DrawRoundedRectangle(WaveBrush, null, bar, radius, radius);
            }
            drawingContext.Pop();
        }

        if (_idle > 0)
        {
            drawingContext.PushOpacity(_idle);
            Rect line = BuildIdleLine(width, height);
            drawingContext.DrawRoundedRectangle(
                IdleLineBrush, null, line, line.Height / 2, line.Height / 2);

            foreach (Rect dot in BuildIdleDots(width, height, _idlePhase))
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

        double maxHeight = Math.Max(6, height - 2);
        double levelStep = (maxHeight - 2) / 3;
        double slot = width / count;
        double barWidth = Math.Clamp(slot - 1, 1.5, 3.0);
        double gap = Math.Max(0, slot - barWidth);
        double center = height / 2;
        Rect[] bars = new Rect[count];
        for (int i = 0; i < count; i++)
        {
            double taper = 0.84 + 0.16 * Math.Sin(Math.PI * (i + 0.5) / count);
            double barHeight = Math.Max(2, (2 + values[i] * levelStep) * taper);
            double x = i * slot + gap / 2;
            bars[i] = new Rect(x, center - barHeight / 2, barWidth, barHeight);
        }
        return bars;
    }

    internal static Rect BuildIdleLine(double width, double height)
    {
        double thickness = Math.Clamp(height * 0.18, 3.5, 4.5);
        return new Rect(0, height / 2 - thickness / 2, width, thickness);
    }

    internal static IReadOnlyList<Rect> BuildIdleDots(double width, double height, double phase)
    {
        const int count = 3;
        double center = height / 2;
        double minRadius = 0.7;
        double maxRadius = 1.6;
        double offset = phase / Math.Tau;

        Rect[] dots = new Rect[count];
        for (int i = 0; i < count; i++)
        {
            double position = (offset + i / (double)count) % 1.0;
            if (position < 0) position += 1;

            // I puntini entrano da destra e scorrono verso sinistra.
            double x = (1 - position) * width;
            double pulse = 0.5 + 0.5 * Math.Sin(phase * 1.6 + i * 2.1);
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

    private static Brush CreateIdleLineBrush()
    {
        SolidColorBrush brush = new(Color.FromRgb(0x4C, 0xAF, 0x50));
        brush.Freeze();
        return brush;
    }

    private static Pen CreateCenterPen()
    {
        Pen pen = new(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 0.6);
        pen.Freeze();
        return pen;
    }
}
