namespace WinQEMU.Core.Input
{
    /// <summary>
    /// Which keys held down are repeated to the guest, as a real keyboard's typematic repeat would.
    /// </summary>
    /// <remarks>
    /// Only edges were sent at first, on the belief that the guest repeats by itself. A desktop does (the compositor
    /// repeats from the key's state), but a Linux text console does not: its PS/2 driver relies on the keyboard sending
    /// the make code again, so holding Down in archinstall moved one line. Windows' own repeat is forwarded instead, as
    /// QEMU's SDL front end does. A desktop guest is not repeated twice: the kernel reports the extra make codes as
    /// repeats, which the compositor ignores. Modifiers and locks are left out: a held Shift has nothing to repeat, a
    /// repeated Caps Lock could toggle, and each repeated Left Ctrl would go through the AltGr filter's wait again.
    /// </remarks>
    public static class KeyRepeat
    {
        private static readonly HashSet<int> NotRepeated = new()
        {
            0x2A, 0x36,               // Shift
            AltGrFilter.LeftCtrl, 0x1D | 0x80, // Ctrl
            0x38, AltGrFilter.RightAlt,       // Alt, AltGr
            0x5B | 0x80, 0x5C | 0x80, // Windows keys
            0x3A, 0x45, 0x46          // Caps, Num and Scroll Lock
        };

        /// <summary>True when a repeat of this AT set-1 scancode (0x80 marking extended keys) goes to the guest.</summary>
        public static bool Repeats(int scancode) => !NotRepeated.Contains(scancode);
    }
}
