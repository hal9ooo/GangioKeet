using System.Windows;
using System.Windows.Media;

namespace GangioKeet.Client.Ui;

/// <summary>
/// Small symmetric waveform for the pill. The history level is drawn as
/// centered capsule bars mirrored around the horizontal centerline, with a
/// gentle sine taper so it reads as a wave instead of bottom-aligned bars.
/// When no voice is detected it fades into a slow, low ambient wave so the
/// pill keeps looking alive for the whole recording.
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    private static readonly Brush WaveBrush = CreateWaveBrush();
    private static readonly Pen CenterPen = CreateCenterPen();

    private IReadOnlyList<byte> _levels = [];
    private double _idle;
    private double _idlePhase;

    /// <summary>
    /// Aggiorna il livello. <paramref name="idle"/> (0..1) sfuma una piccola onda
    /// ambientale quando il microfono non rileva voce, così la pill continua a
    /// sembrare viva invece di spegnere la forma d'onda.
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

        double center = height / 2;
        drawingContext.DrawLine(CenterPen, new Point(0, center), new Point(width, center));
        if (_levels.Count == 0 && _idle <= 0) return;

        foreach (Rect bar in BuildBars(_levels, width, height, _idle, _idlePhase))
        {
            double radius = bar.Width / 2;
            drawingContext.DrawRoundedRectangle(WaveBrush, null, bar, radius, radius);
        }
    }

    internal static IReadOnlyList<Rect> BuildBars(
        IReadOnlyList<byte> levels,
        double width,
        double height,
        double idle = 0,
        double idlePhase = 0)
    {
        const int count = WaveformHistory.BarCount;
        byte[] values = new byte[count];
        int take = Math.Min(levels.Count, count);
        int offset = count - take;
        for (int i = 0; i < take; i++)
            values[offset + i] = levels[levels.Count - take + i];

        idle = Math.Clamp(idle, 0, 1);
        double slot = width / count;
        double barWidth = Math.Clamp(slot - 1, 1.5, 3.0);
        double gap = Math.Max(0, slot - barWidth);
        double center = height / 2;
        Rect[] bars = new Rect[count];
        for (int i = 0; i < count; i++)
        {
            double taper = 0.84 + 0.16 * Math.Sin(Math.PI * (i + 0.5) / count);
            double barHeight = Math.Max(1.5, (2 + values[i] * 4) * taper);

            if (idle > 0)
            {
                // Onda lenta e bassa: segnala che la registrazione è ancora
                // attiva senza simulare un livello audio realistico.
                double envelope = 0.35 + 0.65 * Math.Sin(Math.PI * (i + 0.5) / count);
                double wobble = 0.5 + 0.5 * Math.Sin(idlePhase + i * 0.42);
                double idleHeight = 3.0 + 5.5 * wobble * envelope;
                barHeight += (idleHeight - barHeight) * idle;
            }

            double x = i * slot + gap / 2;
            bars[i] = new Rect(x, center - barHeight / 2, barWidth, barHeight);
        }
        return bars;
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

    private static Pen CreateCenterPen()
    {
        Pen pen = new(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 0.6);
        pen.Freeze();
        return pen;
    }
}
