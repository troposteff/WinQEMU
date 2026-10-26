using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinQEMU.Core.Input;
using WinQEMU.Core.Interop;

namespace WinQEMU.Controls
{
    /// <summary>
    /// Hosts QEMU's SDL console inside the XAML tree, keeping the native window aligned with this
    /// element's bounds. The Win32 work lives in <see cref="QemuConsoleWindow"/>; this class only
    /// translates XAML layout into pixel bounds.
    /// </summary>
    /// <remarks>
    /// The embedded window is a real child HWND, so it composites <em>above</em> all XAML content in
    /// the window rather than participating in the visual tree. Anything that must appear over the
    /// console — dialogs, flyouts, teaching tips — has to hide it first via <see cref="Suspend"/>.
    /// This is the standard airspace constraint of mixing HWNDs with XAML, not a bug.
    /// </remarks>
    /// <summary>How the guest image is fitted into the space the console has.</summary>
    public enum ConsoleScaleMode
    {
        /// <summary>
        /// The whole panel goes to QEMU, which keeps the guest's proportions itself (black bars inside its
        /// window) and passes the size on to the guest, which may adopt it.
        /// </summary>
        Fit,

        /// <summary>One guest pixel per host pixel; larger guests are cropped by the panel.</summary>
        Actual
    }

    public sealed class VmConsoleHost : Grid
    {
        private const string IdlePlaceholder = "Démarrez la machine virtuelle pour afficher sa console.";

        private readonly TextBlock _placeholder;

        private IntPtr _shellWindow = IntPtr.Zero;
        private IntPtr _parkingWindow = IntPtr.Zero;
        private QemuConsoleWindow? _console;
        private int _consoleProcessId;

        /// <summary>Consoles of VMs still running but not shown, by QEMU process id.</summary>
        private readonly Dictionary<int, QemuConsoleWindow> _parked = new();
        private bool _suspended;
        private bool _isLoaded;

        private int _lastX = int.MinValue;
        private int _lastY = int.MinValue;
        private int _lastWidth = int.MinValue;
        private int _lastHeight = int.MinValue;

        private CancellationTokenSource? _attachCancellation;
        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _focusTimer;
        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _guestSizeTimer;
        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _altGrTimer;
        private readonly AltGrFilter _altGr = new();

        public VmConsoleHost()
        {
            // Dark grey rather than black: a guest that renders a black screen is then visibly
            // distinct from a console that never attached at all.
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1E, 0x1E, 0x1E));

            _placeholder = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(24),
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                Text = IdlePlaceholder
            };
            Children.Add(_placeholder);

            // Deliberately not subscribed to LayoutUpdated. It fires on every layout pass, and the
            // TransformToVisual call needed to compute our position can itself force another pass —
            // which spins the UI thread at 100% CPU. Repositioning is driven by explicit signals
            // instead, plus SynchronizeBounds for cases the shell knows about.
            SizeChanged += (_, _) => UpdateNativeBounds();

            // Focusable so XAML routes key events here. Preview handlers, because focus navigation
            // would otherwise swallow Tab and the arrow keys before the guest ever sees them.
            IsTabStop = true;
            UseSystemFocusVisuals = false;
            PreviewKeyDown += OnConsoleKeyDown;
            PreviewKeyUp += OnConsoleKeyUp;
            LostFocus += (_, _) => ReleaseHeldKeys();
            LosingFocus += OnConsoleLosingFocus;

            // Tab navigation cycles inside this element, and the console is the only focusable thing in
            // it, so Tab has nowhere to go. OnConsoleLosingFocus covers what this does not.
            TabFocusNavigation = KeyboardNavigationMode.Cycle;

            _focusTimer = DispatcherQueue.CreateTimer();
            _focusTimer.Interval = TimeSpan.FromMilliseconds(200);
            _focusTimer.IsRepeating = false;
            _focusTimer.Tick += (_, _) => ApplyKeyboardCapture();

            // Windows sends AltGr's phantom Left Ctrl and the Right Alt back to back, so a held Ctrl
            // that is still unexplained after this long was a real one.
            _altGrTimer = DispatcherQueue.CreateTimer();
            _altGrTimer.Interval = TimeSpan.FromMilliseconds(40);
            _altGrTimer.IsRepeating = false;
            _altGrTimer.Tick += (_, _) => Forward(_altGr.Flush());

            // A guest mode change has no notification: QEMU simply resizes its own window. Once that
            // window is a child we position ourselves, the only way to notice is to look. One second is
            // slow enough to cost nothing and quick enough that a resolution change is not left crooked.
            _guestSizeTimer = DispatcherQueue.CreateTimer();
            _guestSizeTimer.Interval = TimeSpan.FromSeconds(1);
            _guestSizeTimer.IsRepeating = true;
            _guestSizeTimer.Tick += (_, _) => CheckGuestSize();

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        public bool IsAttached => _console is { IsEmbedded: true };

        /// <summary>
        /// Whether this host is actually showing. Loaded is not enough on its own: the shell switches
        /// views by toggling visibility, so the element stays loaded while completely hidden.
        /// </summary>
        /// <summary>
        /// True when this element and every one of its ancestors is visible.
        /// </summary>
        /// <remarks>
        /// Checking only this element's own Visibility was not enough: the summary view hides the
        /// console by collapsing its container, which leaves this element "Visible". Closing any dialog
        /// from the summary then called Resume, which saw the console as on screen and showed the native
        /// window over the summary.
        /// </remarks>
        private bool IsOnScreen
        {
            get
            {
                if (!_isLoaded)
                    return false;

                for (DependencyObject? node = this; node is not null; node = VisualTreeHelper.GetParent(node))
                {
                    if (node is UIElement { Visibility: not Visibility.Visible })
                        return false;
                }

                return true;
            }
        }

        /// <summary>Supplies the shell's top-level window handle, which becomes the console's parent.</summary>
        public void Initialize(IntPtr shellWindowHandle)
        {
            _shellWindow = shellWindowHandle;

            // Created here because this runs on the UI thread, whose message loop the window needs.
            _parkingWindow = QemuConsoleWindow.CreateParkingWindow();
            if (_parkingWindow == IntPtr.Zero)
                Log?.Invoke("Console: could not create the parking window; consoles left by switching VMs return to the desktop.");
        }

        /// <summary>
        /// Sink for embedding diagnostics. Without it an embedding failure is invisible: the user
        /// just sees a blank console and a separate QEMU window, with nothing explaining why.
        /// </summary>
        public Action<string>? Log { get; set; }

        public string PlaceholderText
        {
            get => _placeholder.Text;
            set => _placeholder.Text = value;
        }

        /// <summary>
        /// Locates the console window of the given QEMU process and embeds it. Returns false when no
        /// window appeared in time or reparenting failed, leaving an explanatory placeholder.
        /// </summary>
        public async Task<bool> AttachAsync(int processId)
        {
            Detach();

            if (_shellWindow == IntPtr.Zero)
                throw new InvalidOperationException($"{nameof(Initialize)} must be called before attaching.");

            _attachCancellation = new CancellationTokenSource();
            var token = _attachCancellation.Token;

            PlaceholderText = "Connexion à la console…";

            foreach (var dead in _parked.Where(entry => !entry.Value.IsAlive).Select(entry => entry.Key).ToList())
                _parked.Remove(dead);

            if (_parked.Remove(processId, out var parked))
            {
                // Seen before and parked when the user switched away: it is still a child window, so
                // it only needs moving back, not finding again among the desktop's windows.
                if (await Task.Run(() => parked.Unpark(_shellWindow)))
                    return CompleteAttach(parked, processId);

                Log?.Invoke($"Console: could not take window 0x{parked.Handle.ToInt64():X} back from parking; searching again.");
            }

            QemuConsoleWindow? console;
            try
            {
                console = await QemuConsoleWindow.LocateAsync(processId, TimeSpan.FromSeconds(15), token, Log);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            if (token.IsCancellationRequested)
                return false;

            if (console is null)
            {
                PlaceholderText = "La fenêtre de console de QEMU est introuvable.";
                return false;
            }

            // Off the UI thread on purpose. SetParent on a window owned by another process sends
            // messages synchronously to that process's message loop; doing it here would block the
            // shell until QEMU answers, which it may not do promptly while booting a guest.
            // Must happen before the console becomes a sibling of the XAML surface, otherwise that
            // surface paints straight over it.
            WinUiHostWindow.EnableSiblingClipping(_shellWindow, Log);

            var embedded = await Task.Run(() => console.Embed(_shellWindow));
            if (!embedded)
            {
                Log?.Invoke($"Console: SetParent failed for window 0x{console.Handle.ToInt64():X}.");
                PlaceholderText = "Impossible d'intégrer la console. Elle reste dans une fenêtre distincte.";
                return false;
            }

            return CompleteAttach(console, processId);
        }

        /// <summary>Common tail of attaching, once the console is a child of the shell window.</summary>
        private bool CompleteAttach(QemuConsoleWindow console, int processId)
        {
            _console = console;
            _consoleProcessId = processId;
            ResetCachedBounds();
            _guestSizeTimer.Start();
            _ = RefreshGuestSizeAsync();
            GuestSizeChanged?.Invoke(this, EventArgs.Empty);

            // A VM can start while another tab is showing. The window is now a child of the shell,
            // so it must stay hidden until this host is on screen, or it would appear over whatever
            // the user is currently looking at.
            _suspended = false;
            if (IsOnScreen)
            {
                // Queued so any pending layout has completed and ActualWidth/Height are final.
                // Positioning with stale bounds reveals the console at the wrong size and then
                // visibly resizes it.
                DispatcherQueue.TryEnqueue(() =>
                {
                    UpdateNativeBounds();
                    // Starting a VM with the console tab already open should give the guest the
                    // keyboard straight away, without needing a tab switch to trigger it.
                    FocusGuest();
                });
            }
            else
            {
                Suspend();
            }

            return true;
        }

        /// <summary>
        /// Hides the console without releasing it. Call before showing any XAML content that must
        /// appear on top, since a child HWND always wins against the XAML compositor.
        /// </summary>
        public void Suspend()
        {
            if (_console is null || _suspended)
                return;

            _suspended = true;
            _placeholder.Visibility = Visibility.Visible;
            ReleaseHeldKeys();
            _console.Hide();
        }

        public void Resume()
        {
            // Never un-hide while off screen: a console suspended for a dialog must stay hidden if
            // its tab is no longer the visible one.
            if (_console is null || !_suspended || !IsOnScreen)
                return;

            _suspended = false;
            ResetCachedBounds();
            UpdateNativeBounds();
            FocusGuest();
        }

        /// <summary>Releases the console back to its own top-level window, if the process still lives.</summary>
        public void Detach()
        {
            _attachCancellation?.Cancel();
            _attachCancellation?.Dispose();
            _attachCancellation = null;

            var console = _console;
            var processId = _consoleProcessId;
            _console = null;
            _consoleProcessId = 0;
            _suspended = false;
            _pressedKeys.Clear();
            ResetCachedBounds();
            _guestSizeTimer.Stop();
            _guestSize = (0, 0);
            GuestSizeChanged?.Invoke(this, EventArgs.Empty);

            PlaceholderText = IdlePlaceholder;
            _placeholder.Visibility = Visibility.Visible;

            if (console is not null && console.IsAlive)
            {
                // Parked rather than released to the desktop, where SDL would show it again on the
                // next guest mode change (see QemuConsoleWindow.Park). Both reach into QEMU's message
                // loop, so they run off-thread with a bounded wait: a wedged guest must not be able
                // to hang the shell.
                var parkingWindow = _parkingWindow;
                var parkTask = Task.Run(() => parkingWindow != IntPtr.Zero && console.Park(parkingWindow));
                if (!parkTask.Wait(TimeSpan.FromSeconds(2)))
                    PlaceholderText = "La console n'a pas pu être libérée dans le délai imparti.";
                else if (parkTask.Result)
                    _parked[processId] = console;
                else
                    Task.Run(() => console.Release()).Wait(TimeSpan.FromSeconds(2));
            }
        }

        /// <summary>
        /// Re-aligns the native window with this element. Call it after anything that moves the host
        /// without changing its size — a tab becoming visible, a notification bar appearing above it.
        /// </summary>
        public void SynchronizeBounds()
        {
            ResetCachedBounds();
            UpdateNativeBounds();
        }

        /// <summary>
        /// Sink for keystrokes headed to the guest, wired to the session's QMP channel.
        /// Scancodes are AT set-1 with 0x80 set for extended keys.
        /// </summary>
        public Func<IReadOnlyList<(int Number, bool Down)>, Task>? KeyForwarder { get; set; }

        private readonly HashSet<int> _pressedKeys = new();

        /// <summary>
        /// Takes the XAML focus so keystrokes route to this element, and from there to the guest.
        /// </summary>
        /// <remarks>
        /// Deliberately does not give the Win32 focus to the console window. That was the previous
        /// approach and it cannot work — an embedded SDL window never reaches the input-focus state
        /// QEMU wants — and worse, moving the Win32 focus out of WinUI's content bridge would stop XAML
        /// delivering key events here, which is exactly how the keys now get forwarded.
        /// The mouse is unaffected either way: mouse messages go to the window under the pointer.
        /// </remarks>
        public void FocusGuest()
        {
            if (!IsAttached || _suspended)
                return;

            // Deferred, not immediate. TabView moves focus to the freshly selected tab header after
            // raising SelectionChanged, so grabbing focus synchronously just loses it again a moment
            // later — measured: the focused element ended up being the tab item, and keystrokes went
            // to the shell instead of the guest.
            _focusTimer.Stop();
            _focusTimer.Start();
        }

        private void ApplyKeyboardCapture()
        {
            if (!IsAttached || _suspended || !IsOnScreen)
                return;

            Focus(FocusState.Programmatic);
        }

        /// <summary>
        /// Raised on F11. The shell owns what full screen means; the host only reports the key.
        /// </summary>
        public event EventHandler? FullScreenRequested;

        private void OnConsoleKeyDown(object sender, KeyRoutedEventArgs e)
        {
            // Kept for the shell rather than forwarded: while the guest holds the keyboard every other
            // key goes to it, so without one reserved key there would be no way back out of full screen.
            if (e.Key == Windows.System.VirtualKey.F11)
            {
                e.Handled = true;
                FullScreenRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            var number = ToScancode(e);
            if (number == 0)
            {
                Log?.Invoke($"Console: {e.Key} has no scancode and no mapping; not forwarded.");
                return;
            }

            // Handled unconditionally: while the console has focus every keystroke belongs to the
            // guest, and letting Escape or Tab reach the shell would act on the wrong machine.
            e.Handled = true;

            // Windows repeats a held key; the guest tracks its own repeat, so only edges are sent.
            if (!_pressedKeys.Add(number))
                return;

            ForwardFiltered(number, true);
        }

        /// <summary>
        /// Keeps keyboard navigation from taking the focus away from the guest.
        /// </summary>
        /// <remarks>
        /// Marking Tab handled in PreviewKeyDown forwards it to the guest but does not stop it: WinUI's
        /// focus manager runs tab and arrow navigation on its own, so the key reached the guest and the
        /// focus moved to the next control of the shell as well. Cancelling the focus change is the only
        /// way to stop that. Only keyboard-driven changes are refused; clicking elsewhere still leaves
        /// the console, which is how the user gets the keyboard back for the shell.
        /// </remarks>
        private void OnConsoleLosingFocus(UIElement sender, LosingFocusEventArgs args)
        {
            if (!IsAttached || _suspended)
                return;

            // Both are checked: which one WinUI fills in for a Tab press is exactly what has been in
            // doubt, and either one alone means the keyboard, not the pointer or the shell, moved focus.
            var byKeyboard = args.InputDevice == FocusInputDeviceKind.Keyboard || args.FocusState == FocusState.Keyboard;
            var cancelled = byKeyboard && args.TryCancel();

            if (byKeyboard && !cancelled)
            {
                Log?.Invoke(
                    $"Console: keyboard moved the focus to {args.NewFocusedElement?.GetType().Name ?? "nothing"} " +
                    $"(direction {args.Direction}) and it could not be kept in the console.");
            }
        }

        private void OnConsoleKeyUp(object sender, KeyRoutedEventArgs e)
        {
            var number = ToScancode(e);
            if (number == 0)
                return;

            e.Handled = true;
            if (_pressedKeys.Remove(number))
                ForwardFiltered(number, false);
        }

        /// <summary>Sends a key edge through the AltGr filter, arming its timeout while a Ctrl is held back.</summary>
        private void ForwardFiltered(int number, bool down)
        {
            _altGrTimer.Stop();
            Forward(_altGr.Process(number, down));
            if (_altGr.HasPending)
                _altGrTimer.Start();
        }

        /// <summary>
        /// Releases every key the guest still believes is held. Without this, leaving the console with
        /// Ctrl or Alt down leaves the guest stuck with a phantom modifier.
        /// </summary>
        private void ReleaseHeldKeys()
        {
            if (_pressedKeys.Count == 0)
                return;

            // A Ctrl still held back never reached the guest, and a swallowed phantom one never did
            // either; releasing keys the guest does not think are down is harmless.
            _altGrTimer.Stop();
            _altGr.Reset();

            var releases = _pressedKeys.Select(number => (number, false)).ToArray();
            _pressedKeys.Clear();
            Forward(releases);
        }

        private void Forward(IReadOnlyList<(int Number, bool Down)> keys)
        {
            if (keys.Count == 0)
                return;

            var forwarder = KeyForwarder;
            if (forwarder is null)
                return;

            // Fire and forget: a dropped keystroke must never block the UI thread, and the session
            // reports its own failures through the log.
            _ = forwarder(keys);
        }

        /// <summary>
        /// AT set-1 scancode for a key event, with 0x80 set for extended keys — the encoding QEMU's
        /// numeric key events expect. Zero when neither Windows nor the virtual key gives one.
        /// </summary>
        /// <remarks>
        /// Some keys arrive with no scancode at all: Tab does, because WinUI's focus navigation handles
        /// it before reporting it, and with a zero scancode it never reached the guest. The virtual key
        /// is always there, and MapVirtualKey turns it back into the scancode the keyboard would send.
        /// </remarks>
        private static int ToScancode(KeyRoutedEventArgs e)
        {
            var status = e.KeyStatus;
            if (status.ScanCode != 0)
                return (int)status.ScanCode | (status.IsExtendedKey ? 0x80 : 0);

            var mapped = NativeMethods.MapVirtualKey((uint)e.Key, NativeMethods.MapvkVkToVscEx);
            if (mapped == 0)
                return 0;

            // The high byte is 0xE0 (or 0xE1) for extended keys, which QEMU's encoding marks with 0x80.
            var extended = (mapped & 0xFF00) is 0xE000 or 0xE100;
            return (int)(mapped & 0x7F) | (extended ? 0x80 : 0);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = true;

            if (XamlRoot is not null)
                XamlRoot.Changed += OnXamlRootChanged;

            Resume();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (XamlRoot is not null)
                XamlRoot.Changed -= OnXamlRootChanged;

            // Leaving the tree (switching tabs) hides the console rather than releasing it — the VM
            // keeps running and we want it back when the user returns.
            Suspend();
            _isLoaded = false;
        }

        // A DPI change alters the pixel size of the same layout rect, so the cache has to be dropped.
        private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
        {
            ResetCachedBounds();
            UpdateNativeBounds();
        }

        private void ResetCachedBounds()
        {
            _lastX = _lastY = _lastWidth = _lastHeight = int.MinValue;
        }

        /// <summary>
        /// Aligns the native window with this element. Child window coordinates are relative to the
        /// parent's client area, which is exactly the region the XAML content root occupies, so the
        /// element's position in the visual tree maps directly once scaled for DPI.
        /// </summary>
        private ConsoleScaleMode _scaleMode = ConsoleScaleMode.Fit;
        /// <summary>
        /// How the guest image is fitted. Defaults to preserving its proportions: stretching was the
        /// only behaviour before, and it visibly distorted anything that was not the panel's own shape.
        /// </summary>
        public ConsoleScaleMode ScaleMode
        {
            get => _scaleMode;
            set
            {
                if (_scaleMode == value)
                    return;

                _scaleMode = value;
                // The bounds are unchanged as far as layout is concerned, so the usual no-op guard would
                // skip the repositioning this needs.
                InvalidateLastBounds();
                UpdateNativeBounds();
            }
        }

        private double _topOverlayHeight;
        /// <summary>
        /// Height, in DIPs, of a XAML strip along the top of this element that the console must leave
        /// visible: the native window would otherwise draw over it. The strip is cut out of the window's
        /// visible region; the guest image keeps its size and place, so nothing reflows under the pointer.
        /// </summary>
        public double TopOverlayHeight
        {
            get => _topOverlayHeight;
            set
            {
                if (_topOverlayHeight == value)
                    return;

                _topOverlayHeight = value;
                // Bounds unchanged as far as layout is concerned: force the region to be recomputed.
                InvalidateLastBounds();
                UpdateNativeBounds();
            }
        }

        private (int Width, int Height) _guestSize;
        /// <summary>The guest's own resolution, or (0,0) while it is not known.</summary>
        public (int Width, int Height) GuestSize => _guestSize;

        /// <summary>
        /// Asks QEMU for the guest's framebuffer size. Supplied by the shell, which owns the session.
        /// </summary>
        /// <remarks>
        /// The embedded window cannot answer this. It is sized in host pixels — a 150% display makes a
        /// 720x400 guest a 1080x600 window — and once this host has reparented and resized it, its size
        /// is whatever we last asked for. An earlier attempt to read the guest resolution from it
        /// reported 656x519 for a guest that was in no such mode.
        /// </remarks>
        public Func<Task<(int Width, int Height)>>? GuestSizeProvider { get; set; }

        private async Task RefreshGuestSizeAsync()
        {
            if (GuestSizeProvider is null || _console is null)
                return;

            try
            {
                var size = await GuestSizeProvider();
                if (size.Width <= 0 || size.Height <= 0 || size == _guestSize)
                    return;

                _guestSize = size;

                // The panel has not moved, so the usual no-op guard would skip the new letterbox.
                InvalidateLastBounds();
                UpdateNativeBounds();
                GuestSizeChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Console: could not read the guest resolution ({ex.Message}).");
            }
        }

        /// <summary>Raised when the guest changes video mode, so the shell can show the new resolution.</summary>
        public event EventHandler? GuestSizeChanged;

        private void InvalidateLastBounds()
        {
            _lastX = int.MinValue;
            _lastY = int.MinValue;
            _lastWidth = int.MinValue;
            _lastHeight = int.MinValue;
        }

        /// <summary>
        /// Places the guest image inside the panel according to the scale mode, in physical pixels.
        /// </summary>
        /// <remarks>
        /// Only "Taille réelle" needs the guest's resolution. While it is not known the panel is filled,
        /// as in Fit, rather than leaving the console blank.
        /// </remarks>
        private (int X, int Y, int Width, int Height) FitGuest(int panelX, int panelY, int panelWidth, int panelHeight)
        {
            var guest = GuestSize;
            // QEMU's SDL display letterboxes on its own (seen once its window was DPI aware: a 16:10 guest
            // in a wider window got bars at the sides, not a stretched image), so fitting needs no guest
            // size and cannot go wrong when that size is stale. The window's size also becomes the
            // guest's preferred mode, which a virtio-gpu guest follows: a letterbox computed here from an
            // outdated 640x480 kept such a guest boxed in at 4:3.
            if (_scaleMode == ConsoleScaleMode.Fit || guest.Width <= 0 || guest.Height <= 0)
                return (panelX, panelY, panelWidth, panelHeight);

            var (width, height) = guest;

            // Centred, so a smaller image sits in the middle and a cropped one shows its middle.
            return (panelX + (panelWidth - width) / 2, panelY + (panelHeight - height) / 2, width, height);
        }

        private int _ticksSinceGuestSizeRead;

        /// <summary>
        /// Keeps the guest resolution current. QEMU resizing its own window is one sign of a mode change,
        /// but not the only one: a guest that adopts the size of the window we gave it changes mode with
        /// no such sign, and the resolution read at boot (640x480) stayed in place for good. So it is also
        /// read again every few seconds, and shortly after each resize (see UpdateNativeBounds).
        /// </summary>
        private void CheckGuestSize()
        {
            if (_console is null || _suspended)
                return;

            // A screendump writes the whole framebuffer to a file, hence every few seconds, not every tick.
            if (_console.RefreshNativeSize(_lastWidth, _lastHeight) || ++_ticksSinceGuestSizeRead >= 5)
            {
                _ticksSinceGuestSizeRead = 0;
                _ = RefreshGuestSizeAsync();
            }
        }

        private Task _regionUpdates = Task.CompletedTask;

        /// <summary>
        /// Crops the console to the panel when the guest image is larger than it, as "Taille réelle" can
        /// make it, and removes the crop otherwise.
        /// </summary>
        /// <remarks>
        /// Applied on every move rather than only when cropping, so a region left over from an earlier
        /// size or scale mode — or from before the console was parked — can never survive. Queued one
        /// after another off the UI thread: setting a region waits on QEMU's message loop, and two
        /// updates applied out of order would leave the older crop in place.
        /// </remarks>
        private void ClipToPanel(QemuConsoleWindow console, int x, int y, int width, int height,
            int panelX, int panelY, int panelWidth, int panelHeight)
        {
            // The visible part, in the console window's own coordinates.
            var left = Math.Max(x, panelX) - x;
            var top = Math.Max(y, panelY) - y;
            var right = Math.Min(x + width, panelX + panelWidth) - x;
            var bottom = Math.Min(y + height, panelY + panelHeight) - y;

            (int, int, int, int)? visible = left <= 0 && top <= 0 && right >= width && bottom >= height
                ? null
                : (left, top, right, bottom);

            _regionUpdates = _regionUpdates.ContinueWith(
                _ => console.SetVisibleRegion(visible), TaskScheduler.Default);
        }

        private void UpdateNativeBounds()
        {
            if (_console is null || _suspended || !IsOnScreen || XamlRoot is null)
                return;

            if (ActualWidth <= 0 || ActualHeight <= 0)
                return;

            var scale = XamlRoot.RasterizationScale;
            Windows.Foundation.Point origin;
            try
            {
                origin = TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
            }
            catch (Exception)
            {
                // Not connected to a visual tree yet.
                return;
            }

            // The space available, in physical pixels.
            var panelX = (int)Math.Round(origin.X * scale);
            var panelY = (int)Math.Round(origin.Y * scale);
            var panelWidth = Math.Max(1, (int)Math.Round(ActualWidth * scale));
            var panelHeight = Math.Max(1, (int)Math.Round(ActualHeight * scale));

            var (x, y, width, height) = FitGuest(panelX, panelY, panelWidth, panelHeight);

            // LayoutUpdated fires on every layout pass; skipping no-op moves keeps QEMU from
            // rescaling its framebuffer continuously.
            if (x == _lastX && y == _lastY && width == _lastWidth && height == _lastHeight)
                return;

            _lastX = x;
            _lastY = y;
            _lastWidth = width;
            _lastHeight = height;

            _console.SetBounds(x, y, width, height);
            _console.RequestRedraw();
            // The guest may adopt the new window size as its resolution: read it again in a moment.
            _ticksSinceGuestSizeRead = Math.Max(_ticksSinceGuestSizeRead, 3);
            var overlay = Math.Min(panelHeight - 1, (int)Math.Round(_topOverlayHeight * scale));
            ClipToPanel(_console, x, y, width, height, panelX, panelY + overlay, panelWidth, panelHeight - overlay);

            // The guest now covers this element, so the waiting message would only ever be seen
            // through a gap. Collapsing it also makes it obvious when the console is genuinely blank.
            _placeholder.Visibility = Visibility.Collapsed;
        }
    }
}
