using System.Windows;

namespace SokoNote.Views;

/// <summary>Decides whether a remembered window position can be restored.</summary>
public static class WindowPlacement
{
    /// <summary>
    /// True when at least 100 px of the title bar would be on the desktop, so the window can be grabbed and moved.
    /// <paramref name="desktop"/> is the whole virtual screen, which starts left of/above 0 when a monitor sits
    /// to the left of or above the main one.
    /// </summary>
    public static bool IsReachable(double left, double top, double width, Rect desktop)
    {
        var horizontal = Math.Min(left + width, desktop.Right) - Math.Max(left, desktop.Left);
        return horizontal >= 100 && top >= desktop.Top - 10 && top <= desktop.Bottom - 50;
    }

    public static Rect VirtualScreen => new(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
        SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
}
