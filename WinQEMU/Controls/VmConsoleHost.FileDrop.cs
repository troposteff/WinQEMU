using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WinQEMU.Core.Input;
using WinQEMU.Core.Interop;

namespace WinQEMU.Controls
{
    /// <summary>Files dragged from Explorer onto the console (see <see cref="DropCatcher"/>).</summary>
    public sealed partial class VmConsoleHost
    {
        private DropCatcher? _dropCatcher;
        private ConsoleDropTarget? _dropTarget;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _dragWatch;

        /// <summary>Whether the button held down was pressed over the console (a drag inside the guest), or null.</summary>
        private bool? _pressStartedOnConsole;

        /// <summary>Whether <see cref="GuestDragLeft"/> was raised for the button held now.</summary>
        private bool _guestDragLeftRaised;

        /// <summary>The button held now, as QMP names it, and whether the pointer was outside the console when last seen.</summary>
        private string? _pressButton;
        private bool _pressEndedOutside;

        /// <summary>
        /// A button pressed in the guest is held outside the console: perhaps files dragged out of it. Says which button
        /// ("left", "right"). Once per press.
        /// </summary>
        public event Action<string>? GuestDragLeft;

        /// <summary>
        /// Puts the guest's pointer on the edge of its screen nearest to where the host's pointer is, and moves it along
        /// the edge a pixel and back: the guest's drag then enters the window its tools lay there (Sharing.GuestDragOut).
        /// The pointer overlay already pins it while it holds the mouse; QEMU's own window may not, so it is sent anyway.
        /// </summary>
        public async Task PinGuestPointerToEdgeAsync()
        {
            if (_console is not { IsEmbedded: true } console || !NativeMethods.GetCursorPos(out var cursor)
                || !GetWindowRect(console.Handle, out var bounds))
                return;
            int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
            (int Width, int Height) guest = GuestSize is { Width: > 0, Height: > 0 } known ? known : (width, height);
            if (width <= 0 || height <= 0)
                return;
            var (x, y) = PointerEvent.ToAbsolute(cursor.X - bounds.Left, cursor.Y - bounds.Top, width, height, guest.Width, guest.Height);
            // A guest pixel along the edge, in QEMU's units, and back: OLE in the guest acts on moves.
            var stepX = x is 0 or PointerEvent.AbsoluteMax ? 0 : PointerEvent.AbsoluteMax / guest.Width + 1;
            var stepY = stepX == 0 ? PointerEvent.AbsoluteMax / guest.Height + 1 : 0;
            QueuePointer(PointerEvent.Move(x, y));
            foreach (var (dx, dy) in new[] { (stepX, stepY), (0, 0) })
            {
                await Task.Delay(120);
                QueuePointer(PointerEvent.Move(Math.Clamp(x + dx, 0, PointerEvent.AbsoluteMax), Math.Clamp(y + dy, 0, PointerEvent.AbsoluteMax)));
            }
        }

        /// <summary>
        /// Lets go of the guest's button after a drag carried on outside it: the host's drag took the mouse, so QEMU
        /// never saw the release, and the guest's drag would otherwise stay under way.
        /// </summary>
        public void ReleaseGuestButton(string button) => QueuePointer(PointerEvent.Press(button, false));

        /// <summary>
        /// Takes files dropped on the console from now on. Returns what became of it, for the journal.
        /// </summary>
        /// <param name="canDrop">Whether a drop is taken now (a running machine, no copy under way).</param>
        public string EnableFileDrop(Func<bool> canDrop, Action<IReadOnlyList<string>> dropped)
        {
            var catcher = new DropCatcher(_shellWindow);
            if (!catcher.IsCreated)
                return "Console: could not create the drop window; files can only be sent with Envoyer.";
            _dropTarget = ConsoleDropTarget.Register(catcher.Handle, canDrop, dropped, out var problem);
            if (_dropTarget is null)
            {
                catcher.Dispose();
                return $"Console: {problem}; files can only be sent with Envoyer.";
            }

            _dropCatcher = catcher;
            _dropTarget.Log = message => Log?.Invoke(message);
            _dropTarget.Ended += catcher.Hide;

            // A tenth of a second: OLE looks for the window under the pointer at every move, so the catcher only
            // has to be there before the drop, and a drag takes longer than that to cross into the console.
            _dragWatch = DispatcherQueue.CreateTimer();
            _dragWatch.Interval = TimeSpan.FromMilliseconds(100);
            _dragWatch.IsRepeating = true;
            _dragWatch.Tick += (_, _) => WatchForDrag();
            _dragWatch.Start();
            return "Console: files dragged over the console are taken by WinQEMU.";
        }

        /// <summary>
        /// Lays the catcher over the console while a button pressed elsewhere is held over it: a drag from
        /// Explorer (or any other window). A press on the console itself is the guest's, never intercepted.
        /// </summary>
        private void WatchForDrag()
        {
            var catcher = _dropCatcher!;
            // Physical buttons, either of them: Explorer also drags with the right one, and they may be swapped.
            var left = GetAsyncKeyState(0x01) < 0;
            var held = left || GetAsyncKeyState(0x02) < 0;
            if (!held)
            {
                // A press made in the guest and released outside the console: QEMU's own window, which has the mouse
                // when the image is not scaled, never hears of that release (seen: a selection rectangle left drawn
                // in a Windows guest, its button still down). Sent here; a second release does no harm.
                if (_pressStartedOnConsole == true && _pressButton is { } button && _pressEndedOutside)
                    ReleaseGuestButton(button);
                _pressStartedOnConsole = null;
                _pressButton = null;
                _guestDragLeftRaised = false;
                catcher.Hide();
                return;
            }

            if (_console is not { IsEmbedded: true } console || _suspended || !NativeMethods.IsWindowVisible(console.Handle)
                || !NativeMethods.GetCursorPos(out var cursor) || !GetWindowRect(console.Handle, out var bounds))
            {
                catcher.Hide();
                return;
            }

            var over = cursor.X >= bounds.Left && cursor.X < bounds.Right && cursor.Y >= bounds.Top && cursor.Y < bounds.Bottom;
            _pressStartedOnConsole ??= over;
            _pressButton ??= left ? "left" : "right";
            _pressEndedOutside = !over;
            if (_pressStartedOnConsole == true)
            {
                if (!over && !_guestDragLeftRaised)
                {
                    _guestDragLeftRaised = true;
                    GuestDragLeft?.Invoke(left ? "left" : "right");
                }
                return;
            }
            if (!over || catcher.IsVisible)
                return;

            var topLeft = new NativeMethods.Point { X = bounds.Left, Y = bounds.Top };
            ScreenToClient(_shellWindow, ref topLeft);
            catcher.Show(topLeft.X, topLeft.Y, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Bounds
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out Bounds rect);

        [DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr hWnd, ref NativeMethods.Point point);
    }
}
