using System.Runtime.InteropServices;

namespace WinQEMU.Core.Input
{
    /// <summary>
    /// Text as the keys a person would press to type it, for a guest that has nothing else listening yet
    /// (no agent, no clipboard): WinQEMU then types an installation command itself, as VMware's tools
    /// installer starts itself from its CD.
    /// </summary>
    /// <remarks>
    /// The keys are those of a keyboard layout of this PC, by default the one in use: a guest set up by
    /// WinQEMU took this PC's layout, and a key sends a position, not a character. AltGr is the right Alt
    /// (both Windows and Linux read it so); a character on a dead key (^ ` ~ ¨ in French) is followed by a
    /// space, which gives the character alone.
    /// Each character's keys go separately: sent all at once, a long command could overflow the guest's
    /// PS/2 keyboard buffer.
    /// </remarks>
    public static class KeyboardText
    {
        public const int Enter = 0x1C;
        public const int LeftShift = 0x2A;
        public const int LeftCtrl = 0x1D;
        public const int LeftAlt = 0x38;
        public const int RightAlt = 0x38 | 0x80;
        public const int LeftWindows = 0x5B | 0x80;
        public const int Space = 0x39;

        /// <summary>The layout this thread types with (the user's current input language).</summary>
        public static IntPtr CurrentLayout() => GetKeyboardLayout(0);

        /// <summary>
        /// One list of key events per character, in order. A line break is Enter. Throws for a character
        /// the layout has no key for.
        /// </summary>
        public static IReadOnlyList<IReadOnlyList<(int Number, bool Down)>> ToKeystrokes(string text, IntPtr layout)
        {
            var strokes = new List<IReadOnlyList<(int, bool)>>();
            foreach (var ch in text.Replace("\r\n", "\n", StringComparison.Ordinal))
            {
                if (ch == '\n')
                {
                    strokes.Add(Press(Enter));
                    continue;
                }

                var scan = VkKeyScanEx(ch, layout);
                if (scan == -1)
                    throw new ArgumentException($"« {ch} » ne se tape pas avec la disposition de clavier de ce PC.", nameof(text));
                var virtualKey = (uint)(scan & 0xFF);
                var shiftState = (scan >> 8) & 0xFF;
                var code = MapVirtualKeyEx(virtualKey, 4, layout); // MAPVK_VK_TO_VSC_EX: 0xE0 in the high byte when extended
                if (code == 0)
                    throw new ArgumentException($"« {ch} » ne se tape pas avec la disposition de clavier de ce PC.", nameof(text));
                var number = (int)(code & 0xFF) | ((code & 0xFF00) == 0xE000 ? 0x80 : 0);

                var modifiers = new List<int>();
                if ((shiftState & 1) != 0)
                    modifiers.Add(LeftShift);
                // Ctrl+Alt in a layout's table is AltGr.
                if ((shiftState & 6) == 6)
                    modifiers.Add(RightAlt);
                else if ((shiftState & 2) != 0)
                    modifiers.Add(LeftCtrl);
                else if ((shiftState & 4) != 0)
                    modifiers.Add(LeftAlt);

                var keys = new List<(int, bool)>();
                keys.AddRange(modifiers.Select(m => (m, true)));
                keys.Add((number, true));
                keys.Add((number, false));
                keys.AddRange(modifiers.AsEnumerable().Reverse().Select(m => (m, false)));
                // A dead key waits for the next one; a space then gives its own character.
                if (IsDeadKey(virtualKey, code, shiftState, layout))
                {
                    keys.Add((Space, true));
                    keys.Add((Space, false));
                }
                strokes.Add(keys);
            }
            return strokes;
        }

        /// <summary>A key pressed and released, modifiers held around it in the order given.</summary>
        public static IReadOnlyList<(int Number, bool Down)> Press(int key, params int[] modifiers)
        {
            var keys = new List<(int, bool)>();
            keys.AddRange(modifiers.Select(m => (m, true)));
            keys.Add((key, true));
            keys.Add((key, false));
            keys.AddRange(modifiers.AsEnumerable().Reverse().Select(m => (m, false)));
            return keys;
        }

        /// <summary>
        /// Whether the key, with these modifiers, is a dead key. MapVirtualKeyEx only knows the key alone, where
        /// AltGr+2 on a French keyboard is "é", not the dead "~"; ToUnicodeEx is asked with the modifiers held,
        /// leaving the keyboard's state alone (flag 4).
        /// </summary>
        private static bool IsDeadKey(uint virtualKey, uint scanCode, int shiftState, IntPtr layout)
        {
            var state = new byte[256];
            if ((shiftState & 1) != 0)
                state[0x10] = 0x80; // VK_SHIFT
            if ((shiftState & 2) != 0)
                state[0x11] = 0x80; // VK_CONTROL
            if ((shiftState & 4) != 0)
                state[0x12] = 0x80; // VK_MENU
            var buffer = new System.Text.StringBuilder(8);
            return ToUnicodeEx(virtualKey, scanCode, state, buffer, buffer.Capacity, 4, layout) < 0;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetKeyboardLayout(uint threadId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ToUnicodeEx(uint virtualKey, uint scanCode, byte[] keyState, System.Text.StringBuilder buffer,
            int bufferSize, uint flags, IntPtr layout);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern short VkKeyScanEx(char ch, IntPtr layout);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKeyEx(uint code, uint mapType, IntPtr layout);
    }
}
