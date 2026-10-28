using System.Text;

namespace WinQEMU.Core.Interop
{
    /// <summary>
    /// QEMU's console window, and the Win32 dance needed to embed it in another window.
    /// </summary>
    /// <remarks>
    /// QEMU exposes no handle for its display, so the window is discovered by matching the process
    /// id across top-level windows. Once embedded it is a real child HWND: it composites above any
    /// XAML or WPF content in the host window rather than inside it.
    /// </remarks>
    public sealed class QemuConsoleWindow
    {
        /// <summary>Window class SDL2 registers on Windows.</summary>
        public const string SdlWindowClass = "SDL_app";

        private long _originalStyle;
        private long _originalExStyle;

        private QemuConsoleWindow(IntPtr handle, string className, string title)
        {
            Handle = handle;
            ClassName = className;
            Title = title;
        }

        public IntPtr Handle { get; }

        public string ClassName { get; }

        public string Title { get; }

        public bool IsEmbedded { get; private set; }

        /// <summary>True when the window still exists, i.e. QEMU has not exited.</summary>
        public bool IsAlive => NativeMethods.IsWindow(Handle);

        /// <summary>Whether this really is an SDL window, as opposed to a fallback match.</summary>
        public bool IsSdlWindow => string.Equals(ClassName, SdlWindowClass, StringComparison.Ordinal);

        /// <summary>
        /// Polls until the process owns a usable top-level window. QEMU creates it during startup,
        /// a moment after the process itself exists, so this cannot be done synchronously.
        /// </summary>
        public static async Task<QemuConsoleWindow?> LocateAsync(
            int processId,
            TimeSpan timeout,
            CancellationToken cancellationToken = default,
            Action<string>? trace = null)
        {
            var deadline = DateTime.UtcNow + timeout;
            var attempts = 0;

            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attempts++;

                var window = Locate(processId, out _);
                if (window is not null)
                    return window;

                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            trace?.Invoke(
                $"Console: no usable window found for process {processId} after " +
                $"{attempts} attempt(s) over {timeout.TotalSeconds:0}s.");
            return null;
        }

        /// <summary>
        /// Picks the graphical console among the process's windows.
        /// </summary>
        /// <remarks>
        /// QEMU creates one SDL window per console — the graphic output plus the text consoles
        /// (monitor, serial) — so "the first visible SDL window" is not deterministic. QEMU titles
        /// them "QEMU (name-N)" where N is the console index and 0 is the graphical one.
        /// </remarks>
        public static QemuConsoleWindow? Locate(int processId, out IReadOnlyList<string> candidateDescriptions)
        {
            var candidates = new List<(IntPtr Handle, string Class, string Title, int Area, bool Visible)>();

            NativeMethods.EnumWindows((handle, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(handle, out var owner);
                if (owner != (uint)processId)
                    return true;

                // Skip zero-sized helper windows (IME and friends); the console has a real client area.
                if (!NativeMethods.GetClientRect(handle, out var client) || client.Width <= 0 || client.Height <= 0)
                    return true;

                candidates.Add((handle, ReadClassName(handle), ReadWindowText(handle),
                    client.Width * client.Height, NativeMethods.IsWindowVisible(handle)));
                return true;
            }, IntPtr.Zero);

            candidateDescriptions = candidates
                .Select(c => $"{c.Class} '{c.Title}' {c.Area}px visible={c.Visible}")
                .ToList();

            // Visibility is deliberately not a filter for SDL windows: QEMU is launched with its
            // windows hidden so the console never appears outside the shell, so the one we want is
            // normally invisible at this point.
            var sdl = candidates.Where(c => string.Equals(c.Class, SdlWindowClass, StringComparison.Ordinal)).ToList();

            // Console 0 is the graphical output. Fall back to the largest SDL window, then to any
            // visible window so a different display backend still gets embedded rather than failing.
            var chosen = sdl.FirstOrDefault(c => c.Title.EndsWith("-0)", StringComparison.Ordinal));
            if (chosen.Handle == IntPtr.Zero)
                chosen = sdl.OrderByDescending(c => c.Area).FirstOrDefault();
            if (chosen.Handle == IntPtr.Zero)
                chosen = candidates.Where(c => c.Visible).OrderByDescending(c => c.Area).FirstOrDefault();

            return chosen.Handle == IntPtr.Zero
                ? null
                : new QemuConsoleWindow(chosen.Handle, chosen.Class, chosen.Title);
        }

        /// <summary>
        /// Turns the window into a child of <paramref name="parent"/>, stripping the frame that only
        /// makes sense for a top-level window.
        /// </summary>
        /// <returns>False when reparenting failed, in which case the original styles are restored.</returns>
        public bool Embed(IntPtr parent)
        {
            if (IsEmbedded)
                return true;

            // Read before any reparenting or resizing: at this point the window is still exactly the
            // size QEMU gave it, which is the guest framebuffer.
            NativeSize = ClientSize;

            _originalStyle = NativeMethods.GetWindowStyle(Handle, NativeMethods.GwlStyle);
            _originalExStyle = NativeMethods.GetWindowStyle(Handle, NativeMethods.GwlExStyle);

            var style = _originalStyle;
            style &= ~(NativeMethods.WsPopup
                       | NativeMethods.WsCaption
                       | NativeMethods.WsThickFrame
                       | NativeMethods.WsMinimizeBox
                       | NativeMethods.WsMaximizeBox
                       | NativeMethods.WsSysMenu
                       | NativeMethods.WsBorder
                       | NativeMethods.WsDlgFrame);
            // WS_VISIBLE is deliberately not set here. Reparenting reinterprets the window's old
            // top-level coordinates against the new parent's client area, so showing it at this point
            // paints a black rectangle at the wrong place and size until the first reposition lands.
            // The first SetBounds carries SWP_SHOWWINDOW and reveals it already correctly placed.
            style |= NativeMethods.WsChild;

            var exStyle = _originalExStyle;
            exStyle &= ~(NativeMethods.WsExDlgModalFrame
                         | NativeMethods.WsExClientEdge
                         | NativeMethods.WsExWindowEdge
                         | NativeMethods.WsExStaticEdge
                         | NativeMethods.WsExAppWindow
                         // SDL takes dropped files (WS_EX_ACCEPTFILES), which QEMU ignores: the pointer would
                         // promise a copy that never happens. Files are taken by the shell (DropCatcher).
                         | NativeMethods.WsExAcceptFiles);

            NativeMethods.SetWindowStyle(Handle, NativeMethods.GwlStyle, style);
            NativeMethods.SetWindowStyle(Handle, NativeMethods.GwlExStyle, exStyle);

            if (NativeMethods.SetParent(Handle, parent) == IntPtr.Zero)
            {
                // Leave the user with a usable standalone window rather than a styleless orphan.
                NativeMethods.SetWindowStyle(Handle, NativeMethods.GwlStyle, _originalStyle);
                NativeMethods.SetWindowStyle(Handle, NativeMethods.GwlExStyle, _originalExStyle);
                return false;
            }

            IsEmbedded = true;
            return true;
        }

        /// <summary>
        /// Creates the hidden window that parks consoles of VMs not currently shown. Call on a thread
        /// with a message loop (the UI thread); the window is never shown.
        /// </summary>
        public static IntPtr CreateParkingWindow() =>
            NativeMethods.CreateWindowEx(
                (uint)(NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate),
                "STATIC", "WinQEMU console parking", unchecked((uint)NativeMethods.WsPopup),
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        /// <summary>
        /// Moves an embedded console under the hidden parking window, while its VM is not the one shown.
        /// </summary>
        /// <remarks>
        /// Handing the window back to the desktop (<see cref="Release"/>) is not enough to keep it out of
        /// sight: it hides it, but SDL shows its window again by itself whenever the guest changes video
        /// mode — a guest blanking its screen after a few idle minutes is enough — and a framed
        /// "QEMU (name-0)" window then pops up over the user's desktop. A child of a hidden window
        /// cannot appear on screen whatever SDL does to it. The window stays a child, so
        /// <see cref="Unpark"/> brings it back without having to find it again.
        /// </remarks>
        public bool Park(IntPtr parkingWindow)
        {
            if (!IsEmbedded || !IsAlive)
                return false;

            Hide();
            return NativeMethods.SetParent(Handle, parkingWindow) != IntPtr.Zero;
        }

        /// <summary>Brings a parked console back under <paramref name="parent"/>, still hidden.</summary>
        public bool Unpark(IntPtr parent)
        {
            if (!IsEmbedded || !IsAlive)
                return false;

            // NativeSize is kept as it was: the client area now holds the size the shell last fitted it
            // to, not the guest framebuffer.
            return NativeMethods.SetParent(Handle, parent) != IntPtr.Zero;
        }

        /// <summary>Returns the window to the desktop with its original frame, if it still exists.</summary>
        public void Release()
        {
            if (!IsEmbedded)
                return;

            IsEmbedded = false;

            if (!IsAlive)
                return;

            NativeMethods.SetParent(Handle, IntPtr.Zero);

            // WS_VISIBLE is masked out rather than restored verbatim. QEMU creates its windows hidden,
            // but a guest that actually initialises a video mode makes SDL show the window before we
            // embed it — so the captured style can carry WS_VISIBLE, and restoring it pops a framed
            // QEMU window onto the desktop. Releasing always leaves the console hidden; it is found
            // again, hidden or not, whenever its VM is selected in the shell.
            NativeMethods.SetWindowStyle(Handle, NativeMethods.GwlStyle, _originalStyle & ~NativeMethods.WsVisible);
            NativeMethods.SetWindowStyle(Handle, NativeMethods.GwlExStyle, _originalExStyle);

            NativeMethods.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SwpNoZOrder | NativeMethods.SwpNoMove | NativeMethods.SwpNoSize
                | NativeMethods.SwpFrameChanged | NativeMethods.SwpNoActivate
                | NativeMethods.SwpHideWindow | NativeMethods.SwpAsyncWindowPos);
        }

        /// <summary>
        /// Positions the window inside its parent, in physical pixels relative to the parent's
        /// client area. QEMU's SDL backend scales the guest framebuffer to whatever size it is given.
        /// </summary>
        /// <summary>
        /// The window's current client size, which for an SDL console is the guest framebuffer QEMU is
        /// presenting: SDL shows it one pixel per pixel until someone resizes the window.
        /// </summary>
        public (int Width, int Height) ClientSize
        {
            get
            {
                if (!IsAlive || !NativeMethods.GetClientRect(Handle, out var rect))
                    return (0, 0);

                return (rect.Right - rect.Left, rect.Bottom - rect.Top);
            }
        }

        /// <summary>
        /// The guest resolution as it was when the window was embedded, and afterwards whenever QEMU
        /// resizes the window itself -- which is how a guest mode change announces itself once the
        /// window is a child we otherwise control.
        /// </summary>
        public (int Width, int Height) NativeSize { get; private set; }

        /// <summary>
        /// Re-reads the guest resolution when QEMU, not us, changed the window size.
        /// </summary>
        /// <returns>True when the size had changed, i.e. the guest switched mode.</returns>
        public bool RefreshNativeSize(int lastRequestedWidth, int lastRequestedHeight)
        {
            var current = ClientSize;
            if (current.Width <= 0 || current.Height <= 0)
                return false;

            // Anything other than what we last asked for came from QEMU.
            if (current.Width == lastRequestedWidth && current.Height == lastRequestedHeight)
                return false;

            if (current == NativeSize)
                return false;

            NativeSize = current;
            return true;
        }

        /// <param name="below">
        /// A window of the shell to stay just under (the pointer overlay), or zero for the top.
        /// </param>
        public void SetBounds(int x, int y, int width, int height, IntPtr below = default)
        {
            if (!IsAlive)
                return;

            // HWND_TOP, and deliberately no SWP_NOZORDER. Once reparented, the console is a sibling
            // of WinUI's content bridge; left where it lands it sits *below* the bridge and the XAML
            // tree paints straight over it, so the window is correctly parented, sized and visible
            // yet shows nothing at all.
            NativeMethods.SetWindowPos(
                Handle, below == IntPtr.Zero ? NativeMethods.HwndTop : below, x, y, Math.Max(1, width), Math.Max(1, height),
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow | NativeMethods.SwpAsyncWindowPos);
        }

        /// <summary>
        /// Restricts what is shown of the window to a rectangle in its own coordinates, or shows all of
        /// it when <paramref name="visible"/> is null.
        /// </summary>
        /// <remarks>
        /// A child HWND is never clipped by the XAML around it, so a guest image larger than the console
        /// panel — "Taille réelle" on a small window — spilled over the shell's toolbar. A window region
        /// limits both painting and mouse hit-testing to the part inside the panel. It reaches into
        /// QEMU's message loop, so callers should not make it from the UI thread.
        /// <paramref name="hole"/> is cut out of it: the full-screen bar, over the guest.
        /// </remarks>
        public void SetVisibleRegion((int Left, int Top, int Right, int Bottom)? visible,
            (int Left, int Top, int Right, int Bottom)? hole = null, int windowWidth = 0, int windowHeight = 0,
            int holeRadius = 0)
        {
            if (!IsAlive)
                return;

            if (visible is null && hole is null)
            {
                NativeMethods.SetWindowRgn(Handle, IntPtr.Zero, true);
                return;
            }

            var region = NativeMethods.CreateRegion(visible, hole, windowWidth, windowHeight, holeRadius);
            if (region == IntPtr.Zero)
                return;

            // On success the system owns the region; it is only ours to free if the call failed.
            if (NativeMethods.SetWindowRgn(Handle, region, true) == 0)
                NativeMethods.DeleteObject(region);
        }

        /// <summary>
        /// Hides the console. Uses SetWindowPos rather than ShowWindow because ShowWindow always
        /// sends synchronously to the owning thread, which deadlocks the caller when that thread is
        /// QEMU's busy main loop.
        /// </summary>
        /// <summary>
        /// Asks the console to repaint. Reparenting and async repositioning leave SDL with a client
        /// area it has not redrawn, which shows as a transparent hole rather than the guest screen.
        /// Only invalidates — never RDW_UPDATENOW, which would paint synchronously on QEMU's thread
        /// and block us.
        /// </summary>
        public void RequestRedraw()
        {
            if (!IsAlive)
                return;

            NativeMethods.RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero,
                NativeMethods.RdwInvalidate | NativeMethods.RdwErase | NativeMethods.RdwAllChildren);
        }

        public void Hide()
        {
            if (!IsAlive)
                return;

            NativeMethods.SetWindowPos(
                Handle, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SwpHideWindow | NativeMethods.SwpNoMove | NativeMethods.SwpNoSize
                | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate | NativeMethods.SwpAsyncWindowPos);
        }

        /// <summary>
        /// Makes the console able to receive keyboard input, and focuses it.
        /// </summary>
        /// <remarks>
        /// Keyboard focus belongs to an input queue, and the console's queue is QEMU's, not ours. The
        /// foreground thread — ours — receives the keystrokes, so on its own the guest gets nothing and
        /// a plain SetFocus across processes silently fails. Merging the two queues is what makes the
        /// console focusable at all, and it also makes SDL's own SetFocus calls work when the user
        /// clicks back into the console.
        ///
        /// The queues stay merged only while the console is on screen: a merged queue means input
        /// processing is serialised with QEMU's message loop, so a wedged guest could stall our UI.
        /// </remarks>
        public void Focus()
        {
            if (!IsAlive)
                return;

            // AttachThreadInput was tried here and removed. It does what it promises — the console
            // becomes the focus window of the merged queue, confirmed with GetGUIThreadInfo — yet the
            // guest still receives no keystrokes: as a child window SDL never enters the input-focus
            // state QEMU requires before it will forward a key. Verified by contrast: the very same
            // synthetic keystrokes drive the GRUB menu when QEMU owns a top-level window.
            //
            // So the merge bought nothing while serialising our input processing with QEMU's message
            // loop, which is a way to freeze the shell behind a wedged guest. Only the focus call is
            // kept; the mouse works regardless, because mouse messages are delivered by position.
            NativeMethods.SetFocus(Handle);
        }

        /// <summary>True when the console currently holds the keyboard focus.</summary>
        public bool HasKeyboardFocus => IsAlive && NativeMethods.GetFocus() == Handle;

        /// <summary>Current parent, or zero when the window is top-level. Used to verify embedding.</summary>
        public IntPtr GetParentHandle() => NativeMethods.GetParent(Handle);

        private static string ReadClassName(IntPtr handle)
        {
            var buffer = new StringBuilder(256);
            var length = NativeMethods.GetClassName(handle, buffer, buffer.Capacity);
            return length > 0 ? buffer.ToString(0, length) : string.Empty;
        }

        private static string ReadWindowText(IntPtr handle)
        {
            var buffer = new StringBuilder(512);
            var length = NativeMethods.GetWindowText(handle, buffer, buffer.Capacity);
            return length > 0 ? buffer.ToString(0, length) : string.Empty;
        }
    }
}
