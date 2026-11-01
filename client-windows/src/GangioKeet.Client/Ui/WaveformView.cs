using System.Windows;
using System.Windows.Media;

namespace GangioKeet.Client.Ui;

/// <summary>
/// Small symmetric waveform for the pill. The history level is drawn as
/// centered capsule bars mirrored around the horizontal centerline, with a
/// gentle sine taper so it reads as a wave instead of bottom-aligned bars.
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    private static readonly Brush WaveBrush = CreateWaveBrush();
    private static readonly Pen CenterPen = CreateCenterPen();

    private IReadOnlyList<byte> _levels = [];

    public void SetLevels(IReadOnlyList<byte> levels)
    {
        _levels = levels ?? [];
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
        if (_levels.Count == 0) return;

        foreach (Rect bar in BuildBars(_levels, width, height))
        {
            double radius = bar.Width / 2;
            drawingContext.DrawRoundedRectangle(WaveBrush, null, bar, radius, radius);
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

        double slot = width / count;
        double barWidth = Math.Clamp(slot - 1, 1.5, 3.0);
        double gap = Math.Max(0, slot - barWidth);
        double center = height / 2;
        Rect[] bars = new Rect[count];
        for (int i = 0; i < count; i++)
        {
            double taper = 0.84 + 0.16 * Math.Sin(Math.PI * (i + 0.5) / count);
            double barHeight = Math.Max(1.5, (2 + values[i] * 4) * taper);
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
