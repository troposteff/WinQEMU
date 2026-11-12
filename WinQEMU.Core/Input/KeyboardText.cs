using System.Globalization;
using System.Runtime.InteropServices;
using WinQEMU.Core.Text;

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
    /// space, which gives the character alone, and a letter no key carries (ê, ë) is its accent's dead key
    /// then the letter.
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
        /// <param name="linuxGuest">
        /// Whether a Linux guest reads the keys. Windows makes the accents behind AltGr dead keys (French: ~ on AltGr+2,
        /// ` on AltGr+7); Linux's layouts of the same name give the character at once, on the console as under X and
        /// Wayland. Typed the Windows way, "~/x" came out "~ /x" in a French Fedora and Debian: the space meant to
        /// release the dead key was a space. For such a guest these keys are plain ones, and a letter that needs one as
        /// its accent (ñ in French) has no key. Dead keys without AltGr (^ and ¨) are dead in both. Only for the French
        /// layout, where it was seen: Linux's Belgian layout, for one, keeps its AltGr accents dead as Windows does.
        /// </param>
        public static IReadOnlyList<IReadOnlyList<(int Number, bool Down)>> ToKeystrokes(string text, IntPtr layout, bool linuxGuest = false)
        {
            linuxGuest = linuxGuest && IsFrench(layout);
            var strokes = new List<IReadOnlyList<(int, bool)>>();
            foreach (var ch in text.Replace("\r\n", "\n", StringComparison.Ordinal))
            {
                if (ch == '\n')
                {
                    strokes.Add(Press(Enter));
                    continue;
                }

                var keys = KeysFor(ch, layout, out var dead, out var altGr);
                if (keys is not null)
                {
                    // A dead key waits for the next one; a space then gives its own character.
                    if (dead && !(linuxGuest && altGr))
                    {
                        keys.Add((Space, true));
                        keys.Add((Space, false));
                    }
                    strokes.Add(keys);
                    continue;
                }

                strokes.Add(Composed(ch, layout, linuxGuest)
                    // No parameter name: the message is shown to the user as it is, and .NET appends
                    // " (Parameter 'text')" to it otherwise.
                    ?? throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, CoreText.KeyboardCannotType, ch)));
            }
            return strokes;
        }

        /// <summary>
        /// Whether the layout is France's AZERTY (0000040C): the high word of a layout's handle names the layout itself,
        /// the low word the input language it is used for.
        /// </summary>
        private static bool IsFrench(IntPtr layout) => ((layout.ToInt64() >> 16) & 0xFFFF) == 0x040C;

        /// <summary>The keys of one character the layout has a key for (modifiers around it), or null.</summary>
        private static List<(int, bool)>? KeysFor(char ch, IntPtr layout, out bool dead, out bool altGr)
        {
            dead = false;
            altGr = false;
            var scan = VkKeyScanEx(ch, layout);
            if (scan == -1)
                return null;
            var virtualKey = (uint)(scan & 0xFF);
            var shiftState = (scan >> 8) & 0xFF;
            var code = MapVirtualKeyEx(virtualKey, 4, layout); // MAPVK_VK_TO_VSC_EX: 0xE0 in the high byte when extended
            if (code == 0)
                return null;
            var number = (int)(code & 0xFF) | ((code & 0xFF00) == 0xE000 ? 0x80 : 0);

            var modifiers = new List<int>();
            if ((shiftState & 1) != 0)
                modifiers.Add(LeftShift);
            // Ctrl+Alt in a layout's table is AltGr.
            altGr = (shiftState & 6) == 6;
            if (altGr)
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
            dead = IsDeadKey(virtualKey, code, shiftState, layout);
            return keys;
        }

        /// <summary>
        /// A letter with an accent no key carries (ê, ë, ñ on a French keyboard) as a person types it: the
        /// accent's dead key, then the letter. Null when the layout has no such dead key (É in French).
        /// </summary>
        private static List<(int, bool)>? Composed(char ch, IntPtr layout, bool linuxGuest)
        {
            var parts = ch.ToString().Normalize(System.Text.NormalizationForm.FormD);
            if (parts.Length != 2)
                return null;
            var accent = parts[1] switch
            {
                (char)0x0302 => '^',
                (char)0x0300 => '`',
                (char)0x0308 => (char)0x00A8, // ¨
                (char)0x0303 => '~',
                (char)0x0301 => (char)0x00B4, // ´
                _ => '\0'
            };
            if (accent == '\0')
                return null;
            var deadKey = KeysFor(accent, layout, out var dead, out var altGr);
            var letter = KeysFor(parts[0], layout, out var letterDead, out _);
            // In a Linux guest an accent behind AltGr is not a dead key: it would be typed before the letter, not on it.
            if (deadKey is null || !dead || (linuxGuest && altGr) || letter is null || letterDead)
                return null;
            deadKey.AddRange(letter);
            return deadKey;
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
