namespace WinQEMU.Core.Input
{
    /// <summary>
    /// One pointer event for the guest's tablet: a position (<see cref="Button"/> null), or a button going
    /// down or up. Positions are absolute, 0..<see cref="AbsoluteMax"/> across the guest screen.
    /// </summary>
    public readonly record struct PointerEvent(int X, int Y, string? Button = null, bool Down = false)
    {
        /// <summary>QEMU's absolute axis range (INPUT_EVENT_ABS_MAX).</summary>
        public const int AbsoluteMax = 0x7FFF;

        public static PointerEvent Move(int x, int y) => new(x, y);

        public static PointerEvent Press(string button, bool down) => new(0, 0, button, down);

        /// <summary>
        /// Where a point of the console window falls on the guest screen, in QEMU's absolute units. The
        /// image is letterboxed in the window, centred at the largest scale that fits, as QEMU's SDL
        /// display draws it; points on the bars are pinned to the nearest edge.
        /// </summary>
        public static (int X, int Y) ToAbsolute(int x, int y, int windowWidth, int windowHeight, int guestWidth, int guestHeight)
        {
            var scale = Math.Min((double)windowWidth / guestWidth, (double)windowHeight / guestHeight);
            var left = (windowWidth - guestWidth * scale) / 2;
            var top = (windowHeight - guestHeight * scale) / 2;
            var guestX = Math.Clamp((x - left) / scale, 0, guestWidth - 1);
            var guestY = Math.Clamp((y - top) / scale, 0, guestHeight - 1);
            // As QEMU scales a pixel position: x * max / size.
            return ((int)(guestX * AbsoluteMax / guestWidth), (int)(guestY * AbsoluteMax / guestHeight));
        }
    }
}
