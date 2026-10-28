using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
            var held = GetAsyncKeyState(0x01) < 0 || GetAsyncKeyState(0x02) < 0;
            if (!held)
            {
                _pressStartedOnConsole = null;
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
            if (_pressStartedOnConsole == true || !over || catcher.IsVisible)
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
