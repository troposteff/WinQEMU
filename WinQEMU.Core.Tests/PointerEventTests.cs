using WinQEMU.Core.Input;

namespace WinQEMU.Core.Tests;

/// <summary>
/// The pointer WinQEMU sends for QEMU when the guest image is scaled: QEMU's own SDL mapping scales the
/// position against the window instead of the image (the window's centre reached a 1280x800 guest at
/// 81% and 85% of the way), so the shell works out the guest position itself.
/// </summary>
public class PointerEventTests
{
    [Fact]
    public void The_centre_of_the_window_is_the_centre_of_the_guest_whatever_the_scale()
    {
        Assert.Equal((16383, 16383), PointerEvent.ToAbsolute(789, 472, 1578, 944, 1280, 800));
        Assert.Equal((16383, 16383), PointerEvent.ToAbsolute(500, 300, 1000, 600, 1280, 800));
    }

    [Fact]
    public void Letterbox_bars_are_left_out_and_pinned_to_the_nearest_edge()
    {
        // 1280x800 in 1600x800: the image is 1280 wide, with 160 pixels of bar on each side.
        Assert.Equal(0, PointerEvent.ToAbsolute(160, 400, 1600, 800, 1280, 800).X);
        Assert.Equal(0, PointerEvent.ToAbsolute(10, 400, 1600, 800, 1280, 800).X);
        Assert.Equal(PointerEvent.AbsoluteMax * 1279 / 1280, PointerEvent.ToAbsolute(1590, 400, 1600, 800, 1280, 800).X);
    }

    [Fact]
    public void A_scaled_down_image_maps_its_corners_to_the_guests()
    {
        // 1280x800 shown at 1000x625 (scale 0.78125) in a 1000x700 window: 37.5 pixels of bar above and below.
        Assert.Equal((0, 0), PointerEvent.ToAbsolute(0, 37, 1000, 700, 1280, 800));
        var (x, y) = PointerEvent.ToAbsolute(999, 661, 1000, 700, 1280, 800);
        Assert.InRange(x, PointerEvent.AbsoluteMax - 40, PointerEvent.AbsoluteMax);
        Assert.InRange(y, PointerEvent.AbsoluteMax - 100, PointerEvent.AbsoluteMax);
    }
}
