namespace WinQEMU.Core.Input
{
    /// <summary>
    /// Removes the phantom Left Ctrl that Windows sends with every AltGr press, before keystrokes are
    /// forwarded to the guest.
    /// </summary>
    /// <remarks>
    /// On layouts with AltGr, Windows reports AltGr as Left Ctrl down immediately followed by Right Alt
    /// down — the Ctrl is synthetic, but nothing in the key event says so. Forwarded as is, the guest
    /// sees Ctrl+AltGr+key, which Linux treats as a shortcut rather than as the AltGr character, so
    /// | [ ] @ # { } never appear.
    ///
    /// A Left Ctrl press is therefore held back until the next key event. If that event is Right Alt
    /// going down, the Ctrl was AltGr's phantom: it is dropped, and so is its release later on.
    /// Anything else — or the caller's timeout, through <see cref="Flush"/> — proves it was a real Ctrl,
    /// which is then sent ahead of the event that revealed it. A real Ctrl is delayed by the timeout
    /// at most, far below anything a person notices.
    /// </remarks>
    public sealed class AltGrFilter
    {
        /// <summary>AT set-1 scancode of Left Ctrl.</summary>
        public const int LeftCtrl = 0x1D;

        /// <summary>AT set-1 scancode of Right Alt (AltGr), with 0x80 marking the extended key.</summary>
        public const int RightAlt = 0x38 | 0x80;

        private bool _ctrlPending;
        private bool _phantomCtrlDown;

        /// <summary>True while a Left Ctrl press is held back; the caller should arm its timeout.</summary>
        public bool HasPending => _ctrlPending;

        /// <summary>Takes one key edge and returns the edges to forward now, in order.</summary>
        public IReadOnlyList<(int Number, bool Down)> Process(int number, bool down)
        {
            if (number == LeftCtrl)
            {
                if (down)
                {
                    var flushed = Flush();
                    _ctrlPending = true;
                    return flushed;
                }

                if (_phantomCtrlDown)
                {
                    _phantomCtrlDown = false;
                    return Array.Empty<(int, bool)>();
                }

                if (_ctrlPending)
                {
                    // Pressed and released with nothing in between: a real, if pointless, Ctrl tap.
                    _ctrlPending = false;
                    return new[] { (LeftCtrl, true), (LeftCtrl, false) };
                }

                return new[] { (number, down) };
            }

            if (_ctrlPending && number == RightAlt && down)
            {
                _ctrlPending = false;
                _phantomCtrlDown = true;
                return new[] { (number, down) };
            }

            if (_ctrlPending)
            {
                _ctrlPending = false;
                return new[] { (LeftCtrl, true), (number, down) };
            }

            return new[] { (number, down) };
        }

        /// <summary>
        /// Called when the timeout expires with a Ctrl still held back: no AltGr followed, so it was real.
        /// </summary>
        public IReadOnlyList<(int Number, bool Down)> Flush()
        {
            if (!_ctrlPending)
                return Array.Empty<(int, bool)>();

            _ctrlPending = false;
            return new[] { (LeftCtrl, true) };
        }

        /// <summary>Forgets any held or swallowed Ctrl, for when the console loses the keyboard.</summary>
        public void Reset()
        {
            _ctrlPending = false;
            _phantomCtrlDown = false;
        }
    }
}
