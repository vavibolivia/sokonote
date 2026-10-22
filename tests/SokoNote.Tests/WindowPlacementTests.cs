using System.Windows;
using SokoNote.Views;

namespace SokoNote.Tests;

public class WindowPlacementTests
{
    // main monitor 1920x1080 at (0,0); in some tests a second monitor sits to the left at (-1920, 0)
    private static readonly Rect Single = new(0, 0, 1920, 1080);
    private static readonly Rect LeftAndMain = new(-1920, 0, 3840, 1080);

    [Theory]
    [InlineData(100, 100, true)]
    [InlineData(1800, 900, true)]          // mostly off to the right but the title bar is reachable
    [InlineData(1900, 100, false)]         // only 20 px left on screen
    [InlineData(-1500, 100, false)]        // a monitor that is no longer connected
    [InlineData(100, -200, false)]         // title bar above the screen
    public void Single_monitor(double left, double top, bool visible)
    {
        Assert.Equal(visible, WindowPlacement.IsReachable(left, top, 1400, Single));
    }

    [Fact]
    public void A_monitor_left_of_the_main_one_counts_as_screen()
    {
        // regression: the check assumed the desktop starts at x = 0, so a window on the left monitor reopened centred
        Assert.True(WindowPlacement.IsReachable(-1500, 100, 1400, LeftAndMain));
        Assert.False(WindowPlacement.IsReachable(-3500, 100, 1400, LeftAndMain));
    }
}
