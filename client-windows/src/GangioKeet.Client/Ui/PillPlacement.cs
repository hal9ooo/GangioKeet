namespace GangioKeet.Client.Ui;

/// <summary>Work area di un monitor in DIP: origine anche negativa.</summary>
public readonly record struct PillWorkArea(double Left, double Top, double Width, double Height);

/// <summary>
/// Geometria pura della pill: bottom-center o vicino al cursore, sempre dentro
/// la work area del monitor (clamp sui bordi, origini negative incluse).
/// </summary>
public static class PillPlacement
{
    public const double BottomMargin = 24;
    public const double CursorOffset = 16;

    public static (double Left, double Top) CenterBottom(
        PillWorkArea work,
        double width,
        double height,
        double margin = BottomMargin)
    {
        double left = work.Left + ((work.Width - width) / 2.0);
        double top = work.Top + work.Height - height - margin;
        return (ClampLeft(work, width, left), ClampTop(work, height, top));
    }

    public static (double Left, double Top) NearCursor(
        PillWorkArea work,
        double cursorX,
        double cursorY,
        double width,
        double height,
        double offset = CursorOffset)
    {
        return (
            ClampLeft(work, width, cursorX + offset),
            ClampTop(work, height, cursorY + offset));
    }

    private static double ClampLeft(PillWorkArea work, double width, double left)
    {
        double max = work.Left + Math.Max(0, work.Width - width);
        return Math.Clamp(left, work.Left, max);
    }

    private static double ClampTop(PillWorkArea work, double height, double top)
    {
        double max = work.Top + Math.Max(0, work.Height - height);
        return Math.Clamp(top, work.Top, max);
    }
}
