using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using WinQEMU.Core.Qemu;

namespace WinQEMU.Core.Sharing
{
    /// <summary>
    /// Files dragged out of a Windows guest's console onto this PC, as VMware's drag and drop does: the guest half.
    /// </summary>
    /// <remarks>
    /// A drag inside the guest belongs to the guest's own Explorer; QEMU only carries the mouse. What is being dragged
    /// can only be learnt in the guest, by a window that the drag passes over. So a small program runs in the user's
    /// session (as VMware's tools do): while a mouse button is held and the guest's pointer sits on the edge of the
    /// screen, which is where it stays once the host's pointer has left the console, it lays an almost invisible
    /// window under the pointer. Explorer's drag then enters that window and hands it the file list, which the program
    /// writes to <see cref="StatePath"/> for the shell to read through the agent (guest-file-read: no process started).
    /// When the button is released the state goes back to idle. A drop on the window does nothing: the files stay.
    ///
    /// Checked on a Windows 11 guest with a drag simulated over QMP: the list arrives within a tenth of a second of
    /// the pointer reaching the edge, and the file is still there after the drop.
    ///
    /// Started like the resolution helper (<see cref="Services.WindowsGuestDisplay"/>): compiled by the agent with
    /// Windows' own .NET Framework, run by a task of the Users group, at each logon and once at setup. WinForms, part
    /// of the .NET Framework, carries the OLE drop target. The executable's name holds a hash of its source, so a
    /// newer WinQEMU compiles its own and stops the old one.
    /// </remarks>
    public static class GuestDragOut
    {
        public const string TaskName = "WinQEMU-DragOut";

        /// <summary>Where the program writes what is being dragged; every user may write it (set up by <see cref="SetupScript"/>).</summary>
        public const string StatePath = @"C:\ProgramData\WinQEMU\dragout.txt";

        /// <summary>The program, run in the user's session. C# 5, the .NET Framework compiler's.</summary>
        public const string HelperSource = """
            using System;
            using System.Drawing;
            using System.IO;
            using System.Runtime.InteropServices;
            using System.Text;
            using System.Threading;
            using System.Windows.Forms;

            class WinQemuDragOutWindow : Form
            {
                protected override bool ShowWithoutActivation { get { return true; } }
                protected override CreateParams CreateParams
                {
                    // WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE: never in the taskbar, never takes the focus.
                    get { var p = base.CreateParams; p.ExStyle |= 0x8 | 0x80 | 0x08000000; return p; }
                }
            }

            static class WinQemuDragOut
            {
                [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
                [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
                [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);

                static readonly string State = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"WinQEMU\dragout.txt");
                static string written;

                static void Write(string text)
                {
                    if (text == written)
                        return;
                    try
                    {
                        File.WriteAllText(State, text, new UTF8Encoding(false));
                        written = text;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }

                [STAThread]
                static int Main()
                {
                    bool first;
                    using (new Mutex(true, @"Local\WinQEMU-DragOut", out first))
                    {
                        if (!first)
                            return 0;

                        var window = new WinQemuDragOutWindow
                        {
                            FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, TopMost = true, AllowDrop = true,
                            StartPosition = FormStartPosition.Manual, Size = new Size(16, 16), Opacity = 0.01,
                            BackColor = Color.Black
                        };
                        window.DragEnter += (s, e) =>
                        {
                            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                            if (files == null || files.Length == 0)
                            {
                                e.Effect = DragDropEffects.None;
                                return;
                            }
                            e.Effect = DragDropEffects.Copy;
                            var text = new StringBuilder("winqemu-dragout 1\ndrag\n");
                            foreach (var file in files)
                                text.Append("file ").Append(file).Append('\n');
                            Write(text.Append("end\n").ToString());
                        };
                        window.DragOver += (s, e) =>
                        {
                            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
                        };

                        var timer = new System.Windows.Forms.Timer { Interval = 50 };
                        timer.Tick += (s, e) =>
                        {
                            Point point;
                            var held = GetAsyncKeyState(0x01) < 0 || GetAsyncKeyState(0x02) < 0;
                            var screen = SystemInformation.VirtualScreen;
                            var atEdge = GetCursorPos(out point) && (point.X <= screen.Left || point.Y <= screen.Top
                                || point.X >= screen.Right - 1 || point.Y >= screen.Bottom - 1);
                            if (held && atEdge)
                            {
                                if (!window.Visible)
                                {
                                    window.Location = new Point(point.X - 8, point.Y - 8);
                                    window.Show();
                                }
                                else if (!window.Bounds.Contains(point))
                                {
                                    // The pointer slid along the edge (the host's moved on outside the console): the
                                    // window follows, and a move on the spot makes OLE look under the pointer again.
                                    // Left behind, the drag ended on the desktop, which moved the dragged icon there.
                                    window.Location = new Point(point.X - 8, point.Y - 8);
                                    SetCursorPos(point.X, point.Y);
                                }
                            }
                            else
                            {
                                if (window.Visible)
                                    window.Hide();
                                if (!held)
                                    Write("winqemu-dragout 1\nidle\nend\n");
                            }
                        };
                        timer.Start();
                        Application.Run();
                    }
                    return 0;
                }
            }
            """;

        /// <summary>The executable's name: changes with its source, so an older one is never kept by mistake.</summary>
        public static string ExecutableName
        {
            get
            {
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(HelperSource));
                return $"WinQemuDragOut-{Convert.ToHexString(hash, 0, 4).ToLowerInvariant()}.exe";
            }
        }

        /// <summary>
        /// Run as SYSTEM by the agent: compiles the program unless already there, lets every user write the state
        /// file, stops older versions, registers the logon task and starts it in the open session.
        /// </summary>
        public static string SetupScript => """
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            {PROTECT}
            $dir = "$env:ProgramData\WinQEMU"
            Protect-WinQemuFolder $dir
            $exe = Join-Path $dir '{EXE}'
            if (-not (Test-Path $exe)) {
              $source = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{SOURCE}'))
              Add-Type -TypeDefinition $source -ReferencedAssemblies System.Windows.Forms, System.Drawing -OutputAssembly $exe -OutputType WindowsApplication
            }
            $state = Join-Path $dir 'dragout.txt'
            if (-not (Test-Path $state)) { Set-Content $state "winqemu-dragout 1`nidle`nend" -NoNewline }
            icacls $state /grant '*S-1-5-32-545:M' | Out-Null
            Get-Process -Name 'WinQemuDragOut-*' -ErrorAction SilentlyContinue | Where-Object { $_.Path -ne $exe } | Stop-Process -Force -ErrorAction SilentlyContinue
            Get-ChildItem $dir -Filter 'WinQemuDragOut-*.exe' | Where-Object { $_.FullName -ne $exe } | Remove-Item -ErrorAction SilentlyContinue
            $principal = New-ScheduledTaskPrincipal -GroupId 'S-1-5-32-545' -RunLevel Limited
            $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
            Register-ScheduledTask -TaskName 'WinQEMU-DragOut' -Action (New-ScheduledTaskAction -Execute $exe) -Trigger (New-ScheduledTaskTrigger -AtLogOn) -Principal $principal -Settings $settings -Force | Out-Null
            if (-not (Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($exe)) -ErrorAction SilentlyContinue)) { Start-ScheduledTask -TaskName 'WinQEMU-DragOut' }
            """.Replace("{PROTECT}", Services.GuestWorkFolder.ProtectFunction)
            .Replace("{EXE}", ExecutableName)
            .Replace("{SOURCE}", Convert.ToBase64String(Encoding.UTF8.GetBytes(HelperSource)));

        /// <summary>Compiles and starts the program. The guest may still be booting: see <see cref="Services.GuestShareMounter"/>.</summary>
        public static async Task SetupAsync(GuestAgentClient agent, CancellationToken cancellationToken = default)
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(SetupScript));
            var result = await agent.RunAsync("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded },
                null, TimeSpan.FromSeconds(120), cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new GuestAgentException($"Dragging out of the guest could not be set up: {result.Error.Trim()}");
        }

        // ---- Linux guests ----

        /// <summary>
        /// Where the Linux program writes what is being dragged. Writable by everyone: the program runs as whoever logs
        /// in, maybe long after the setup, and the list only names files for the host to copy to its own user, who reads
        /// any file of the guest through the agent anyway. A guest user writing it would receive nothing.
        /// </summary>
        public const string LinuxStatePath = "/var/lib/winqemu/dragout.txt";

        /// <summary>
        /// Which edge of the screen the program lays its window along, written by the host through the agent: left,
        /// right, top, bottom, or none.
        /// </summary>
        public const string LinuxEdgePath = "/var/lib/winqemu/dragout-edge";

        /// <summary>Where the Linux program is installed.</summary>
        public const string LinuxHelperPath = "/usr/local/lib/winqemu/winqemu-dragout.py";

        /// <summary>
        /// The program for a Linux guest's desktop: python3 over libX11 (ctypes), nothing to install. Under Wayland too,
        /// through XWayland.
        /// </summary>
        /// <remarks>
        /// A Wayland client can neither place its window on the screen nor see the pointer. An X11 window can, through
        /// XWayland: an override-redirect window is laid where asked, and Mutter (GNOME) passes a Wayland drag, from
        /// Files, on to it over XDND (checked on Ubuntu 26.04: XdndEnter, XdndPosition, then the text/uri-list at the
        /// drop). KWin (Plasma) does the same for the X11 windows it manages only: there the window is a dock that says
        /// where it wants to be (checked on openSUSE Tumbleweed, Plasma 6). VMware's and VirtualBox's tools do this on
        /// X11 only.
        ///
        /// The program cannot see where the pointer is either, so the host says which edge the drag left the console by
        /// (<see cref="LinuxEdgePath"/>), and the window is a strip along that whole edge, there only while the host
        /// asks: the guest's pointer slides along the edge as the host's moves outside the console, and stays over it.
        /// The file list is asked for at the first XdndPosition, before any drop, and written to
        /// <see cref="LinuxStatePath"/> as the Windows program does; a drop on the strip does nothing (copy, the files
        /// stay), and lays it down.
        /// </remarks>
        public static readonly string LinuxHelperSource = """
            # winqemu-dragout: files dragged to an edge of the screen, for WinQEMU (see GuestDragOut in its source).
            import ctypes, fcntl, os, sys, time, urllib.parse
            from ctypes import c_int, c_uint, c_long, c_ulong, c_char_p, c_void_p, POINTER, byref, Structure, Union

            STATE = '/var/lib/winqemu/dragout.txt'
            EDGE = '/var/lib/winqemu/dragout-edge'
            IDLE = 'winqemu-dragout 1\nidle\nend\n'
            THICKNESS = 4

            class XSetWindowAttributes(Structure):
                _fields_ = [('background_pixmap', c_ulong), ('background_pixel', c_ulong), ('border_pixmap', c_ulong),
                            ('border_pixel', c_ulong), ('bit_gravity', c_int), ('win_gravity', c_int), ('backing_store', c_int),
                            ('backing_planes', c_ulong), ('backing_pixel', c_ulong), ('save_under', c_int), ('event_mask', c_long),
                            ('do_not_propagate_mask', c_long), ('override_redirect', c_int), ('colormap', c_ulong), ('cursor', c_ulong)]

            class XClientMessageEvent(Structure):
                _fields_ = [('type', c_int), ('serial', c_ulong), ('send_event', c_int), ('display', c_void_p), ('window', c_ulong),
                            ('message_type', c_ulong), ('format', c_int), ('l', c_long * 5)]

            class XSelectionEvent(Structure):
                _fields_ = [('type', c_int), ('serial', c_ulong), ('send_event', c_int), ('display', c_void_p), ('requestor', c_ulong),
                            ('selection', c_ulong), ('target', c_ulong), ('property', c_ulong), ('time', c_ulong)]

            class XEvent(Union):
                _fields_ = [('type', c_int), ('xclient', XClientMessageEvent), ('xselection', XSelectionEvent), ('pad', c_long * 24)]

            def write_state(text):
                try:
                    with open(STATE, 'w', encoding='utf-8') as f:
                        f.write(text)
                except OSError:
                    pass

            def read_edge():
                try:
                    with open(EDGE, encoding='utf-8') as f:
                        return f.read().strip()
                except OSError:
                    return 'none'

            def paths(uri_list):
                found = []
                for line in uri_list.replace('\r\n', '\n').split('\n'):
                    if not line or line.startswith('#'):
                        continue
                    uri = urllib.parse.urlparse(line)
                    path = urllib.parse.unquote(uri.path)
                    if uri.scheme == 'file' and path and '\n' not in path:
                        found.append(path)
                return found

            def main():
                # One per session: the logon start and the host's setup may both run it.
                lock = open(os.path.join(os.environ.get('XDG_RUNTIME_DIR') or '/tmp', 'winqemu-dragout.lock'), 'w')
                try:
                    fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
                except OSError:
                    return 0

                x = ctypes.cdll.LoadLibrary('libX11.so.6')
                x.XOpenDisplay.restype = c_void_p; x.XOpenDisplay.argtypes = [c_char_p]
                x.XDefaultRootWindow.restype = c_ulong; x.XDefaultRootWindow.argtypes = [c_void_p]
                x.XInternAtom.restype = c_ulong; x.XInternAtom.argtypes = [c_void_p, c_char_p, c_int]
                x.XCreateWindow.restype = c_ulong
                x.XCreateWindow.argtypes = [c_void_p, c_ulong, c_int, c_int, c_uint, c_uint, c_uint, c_int, c_uint, c_void_p, c_ulong,
                                            POINTER(XSetWindowAttributes)]
                x.XChangeProperty.argtypes = [c_void_p, c_ulong, c_ulong, c_ulong, c_int, c_int, c_void_p, c_int]
                x.XGetGeometry.argtypes = [c_void_p, c_ulong, POINTER(c_ulong), POINTER(c_int), POINTER(c_int), POINTER(c_uint),
                                           POINTER(c_uint), POINTER(c_uint), POINTER(c_uint)]
                x.XMoveResizeWindow.argtypes = [c_void_p, c_ulong, c_int, c_int, c_uint, c_uint]
                x.XMapRaised.argtypes = [c_void_p, c_ulong]
                x.XUnmapWindow.argtypes = [c_void_p, c_ulong]
                x.XFlush.argtypes = [c_void_p]
                x.XPending.argtypes = [c_void_p]
                x.XNextEvent.argtypes = [c_void_p, POINTER(XEvent)]
                x.XSendEvent.argtypes = [c_void_p, c_ulong, c_int, c_long, POINTER(XEvent)]
                x.XConvertSelection.argtypes = [c_void_p, c_ulong, c_ulong, c_ulong, c_ulong, c_ulong]
                x.XGetWindowProperty.argtypes = [c_void_p, c_ulong, c_ulong, c_long, c_long, c_int, c_ulong, POINTER(c_ulong),
                                                 POINTER(c_int), POINTER(c_ulong), POINTER(c_ulong), POINTER(c_void_p)]
                x.XFree.argtypes = [c_void_p]

                # A window gone meanwhile (the window manager's check window) would end the program under Xlib's default handler.
                handler = ctypes.CFUNCTYPE(c_int, c_void_p, c_void_p)(lambda display, error: 0)
                x.XSetErrorHandler(handler)
                d = x.XOpenDisplay(None)
                if not d:
                    return 1
                atom = lambda name: x.XInternAtom(d, name.encode(), 0)
                aware, enter, position, status, drop, finished = (atom('XdndAware'), atom('XdndEnter'), atom('XdndPosition'),
                                                                  atom('XdndStatus'), atom('XdndDrop'), atom('XdndFinished'))
                selection, copy, uri_list, prop = atom('XdndSelection'), atom('XdndActionCopy'), atom('text/uri-list'), atom('WINQEMU_DRAG')
                root = x.XDefaultRootWindow(d)

                def property_of(window, name, kind, length):
                    kind_out, size_format, count, after, data = c_ulong(), c_int(), c_ulong(), c_ulong(), c_void_p()
                    if x.XGetWindowProperty(d, window, atom(name), 0, length, 0, kind, byref(kind_out), byref(size_format),
                                            byref(count), byref(after), byref(data)) != 0 or not data:
                        return None
                    value = ctypes.cast(data, POINTER(c_ulong))[0] if kind == 33 else ctypes.string_at(data, count.value)
                    x.XFree(data)
                    return value

                # KWin passes a Wayland drag to the X11 windows it manages only: an override-redirect one never got XdndEnter
                # (Plasma 6, openSUSE Tumbleweed). Managed, the window goes where the window manager puts it, unless it says
                # where it wants to be (WM_NORMAL_HINTS): a dock, above everything, out of the taskbar. Mutter (GNOME) passes
                # the drag to an override-redirect window, which no window manager moves: kept there.
                check = property_of(root, '_NET_SUPPORTING_WM_CHECK', 33, 1)  # XA_WINDOW
                name = property_of(check, '_NET_WM_NAME', atom('UTF8_STRING'), 64) if check else None
                managed = bool(name) and b'KWin' in name

                attributes = XSetWindowAttributes()
                attributes.override_redirect = 0 if managed else 1
                attributes.background_pixel = 0
                # InputOutput: an InputOnly window has no Wayland surface, so the compositor would never find it under the pointer.
                window = x.XCreateWindow(d, root, 0, 0, 1, 1, 0, 0, 1, None, 0x200 | 0x2, byref(attributes))  # CWOverrideRedirect | CWBackPixel
                version = c_ulong(5)
                x.XChangeProperty(d, window, aware, 4, 32, 0, byref(version), 1)  # XA_ATOM
                opacity = c_ulong(0x02000000)  # about 1 %, where the compositor honours it
                x.XChangeProperty(d, window, atom('_NET_WM_WINDOW_OPACITY'), 6, 32, 0, byref(opacity), 1)  # XA_CARDINAL
                if managed:
                    dock = c_ulong(atom('_NET_WM_WINDOW_TYPE_DOCK'))
                    x.XChangeProperty(d, window, atom('_NET_WM_WINDOW_TYPE'), 4, 32, 0, byref(dock), 1)
                    states = (c_ulong * 3)(atom('_NET_WM_STATE_ABOVE'), atom('_NET_WM_STATE_SKIP_TASKBAR'),
                                           atom('_NET_WM_STATE_SKIP_PAGER'))
                    x.XChangeProperty(d, window, atom('_NET_WM_STATE'), 4, 32, 0, states, 3)

                def send(target, kind, data):
                    event = XEvent()
                    event.xclient.type = 33; event.xclient.window = target; event.xclient.message_type = kind; event.xclient.format = 32
                    for i, value in enumerate(data):
                        event.xclient.l[i] = value
                    x.XSendEvent(d, target, 0, 0, byref(event))

                def lay(edge):
                    r, gx, gy, w, h, b, depth = c_ulong(), c_int(), c_int(), c_uint(), c_uint(), c_uint(), c_uint()
                    x.XGetGeometry(d, root, byref(r), byref(gx), byref(gy), byref(w), byref(h), byref(b), byref(depth))
                    w, h, t = w.value, h.value, THICKNESS
                    box = {'left': (0, 0, t, h), 'right': (w - t, 0, t, h), 'top': (0, 0, w, t), 'bottom': (0, h - t, w, t)}[edge]
                    if managed:
                        # USPosition | USSize | PPosition | PSize | PMinSize | PMaxSize: here, this size, no other.
                        bx, by, bw, bh = box
                        hints = (c_ulong * 18)(63, bx, by, bw, bh, bw, bh, bw, bh, 0, 0, 0, 0, 0, 0, 0, 0, 1)
                        x.XChangeProperty(d, window, 40, 41, 32, 0, hints, 18)  # WM_NORMAL_HINTS, WM_SIZE_HINTS
                    x.XMoveResizeWindow(d, window, *box)
                    x.XMapRaised(d, window)

                write_state(IDLE)
                shown, laid, source, asked = 'none', False, 0, False
                event = XEvent()
                while True:
                    edge = read_edge()
                    if edge != shown:
                        shown = edge
                        if edge in ('left', 'right', 'top', 'bottom'):
                            # A new drag: the list of the one before must not be read as this one's. It stayed when
                            # the host could not write "none" after a drag (the agent busy), and the next press
                            # that left the console then copied the previous drag's files.
                            write_state(IDLE)
                            lay(edge)
                            laid, asked = True, False
                        else:
                            x.XUnmapWindow(d, window)
                            laid = False
                            write_state(IDLE)
                    while x.XPending(d):
                        x.XNextEvent(d, byref(event))
                        if event.type == 33:  # ClientMessage
                            message = event.xclient
                            if message.message_type == enter:
                                source, asked = message.l[0], False
                            elif message.message_type == position:
                                send(message.l[0], status, [window, 1 | 2, 0, 0, copy])  # accepted, keep sending positions
                                if not asked:
                                    x.XConvertSelection(d, selection, uri_list, prop, window, message.l[3])
                                    asked = True
                            elif message.message_type == drop:
                                # Nothing to do with it here: the files stay, the host copies them.
                                send(message.l[0], finished, [window, 1, copy])
                                x.XUnmapWindow(d, window)
                                laid = False
                        elif event.type == 31 and event.xselection.property:  # SelectionNotify
                            kind, size_format, count, after, data = c_ulong(), c_int(), c_ulong(), c_ulong(), c_void_p()
                            x.XGetWindowProperty(d, window, prop, 0, 1 << 22, 1, 0, byref(kind), byref(size_format),
                                                 byref(count), byref(after), byref(data))
                            text = ctypes.string_at(data, count.value).decode('utf-8', 'replace') if data else ''
                            if data:
                                x.XFree(data)
                            found = paths(text)
                            # Not once the strip is gone: an answer that comes after the host gave up ("none") would
                            # leave a list for the next drag to find.
                            if found and shown in ('left', 'right', 'top', 'bottom'):
                                write_state('winqemu-dragout 1\ndrag\n' + ''.join('file ' + p + '\n' for p in found) + 'end\n')
                    x.XFlush(d)
                    time.sleep(0.05)

            if __name__ == '__main__':
                sys.exit(main())
            """.ReplaceLineEndings("\n");

        /// <summary>
        /// Run as root by the agent: installs the program, starts it at each logon (XDG autostart), and in the graphical
        /// sessions already open, with the X display and its authority read from the session's systemd environment.
        /// Nobody logged in yet is fine: the logon starts it (the guest's agent answers well before that, and a first
        /// version waited for a session, gave up after five minutes, and never took a drag from a later logon).
        /// Exit 3: python3 or libX11 missing.
        /// </summary>
        public static string LinuxSetupScript => """
            set -u
            # openSUSE's agent runs with UMask=0077: the program's folder and the autostart entry came out root-only, and
            # the session could neither start nor read the program ("Permission denied").
            umask 022
            command -v python3 >/dev/null 2>&1 || { echo "python3 is missing" >&2; exit 3; }
            python3 -c "import ctypes; ctypes.CDLL('libX11.so.6')" 2>/dev/null || { echo "libX11 is missing" >&2; exit 3; }
            dir=/var/lib/winqemu
            mkdir -p /usr/local/lib/winqemu "$dir"
            # Explicit too: an earlier run may have left them root-only.
            chmod 755 /usr/local/lib/winqemu "$dir"
            printf '%s' '{SOURCE}' | base64 -d > {HELPER}.new && mv {HELPER}.new {HELPER}
            chmod 644 {HELPER}
            printf 'none\n' > {EDGE}
            chmod 644 {EDGE}
            # Written by the program, run as whoever logs in (see LinuxStatePath).
            touch {STATE}
            chown root:root {STATE}
            chmod 666 {STATE}
            mkdir -p /etc/xdg/autostart
            printf '[Desktop Entry]\nType=Application\nName=WinQEMU\nExec=python3 {HELPER}\nNoDisplay=true\n' > /etc/xdg/autostart/winqemu-dragout.desktop
            chmod 644 /etc/xdg/autostart/winqemu-dragout.desktop
            for session in $(loginctl list-sessions --no-legend | awk '{print $1}'); do
              [ "$(loginctl show-session "$session" -p Class --value)" = user ] || continue
              case "$(loginctl show-session "$session" -p Type --value)" in wayland|x11) ;; *) continue ;; esac
              user=$(loginctl show-session "$session" -p Name --value)
              uid=$(id -u "$user") || continue
              environment=$(runuser -u "$user" -- env XDG_RUNTIME_DIR=/run/user/$uid DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/$uid/bus \
                systemctl --user show-environment 2>/dev/null)
              display=$(printf '%s\n' "$environment" | sed -n 's/^DISPLAY=//p')
              authority=$(printf '%s\n' "$environment" | sed -n 's/^XAUTHORITY=//p')
              [ -n "$display" ] || continue
              # Started here as "python3 …", by the session's autostart as "/usr/bin/python3 …": both are matched, but
              # not this script's own command line, which names the program too.
              pkill -u "$user" -f '^[^ ]*python3 {HELPER_PATTERN}$' 2>/dev/null
              sleep 0.3
              # As a unit of the user's own systemd, which has the session's DISPLAY and XAUTHORITY: started by this
              # script alone, the program is a child of the agent and lives in its control group, and a restart of the
              # agent (a package upgrade) killed it; no drag was taken until the next logon. Where that cannot be done,
              # as before; without an empty XAUTHORITY, which keeps X from looking for ~/.Xauthority.
              runuser -u "$user" -- env XDG_RUNTIME_DIR=/run/user/$uid DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/$uid/bus \
                systemd-run --user --quiet --collect python3 {HELPER} >/dev/null 2>&1 </dev/null && continue
              if [ -n "$authority" ]; then
                runuser -u "$user" -- env DISPLAY="$display" XAUTHORITY="$authority" XDG_RUNTIME_DIR=/run/user/$uid \
                  setsid -f python3 {HELPER} >/dev/null 2>&1 </dev/null
              else
                runuser -u "$user" -- env DISPLAY="$display" XDG_RUNTIME_DIR=/run/user/$uid \
                  setsid -f python3 {HELPER} >/dev/null 2>&1 </dev/null
              fi
            done
            exit 0
            """.ReplaceLineEndings("\n")
            .Replace("{SOURCE}", Convert.ToBase64String(Encoding.UTF8.GetBytes(LinuxHelperSource)), StringComparison.Ordinal)
            .Replace("{HELPER_PATTERN}", LinuxHelperPath.Replace(".", "[.]", StringComparison.Ordinal), StringComparison.Ordinal)
            .Replace("{HELPER}", LinuxHelperPath, StringComparison.Ordinal)
            .Replace("{EDGE}", LinuxEdgePath, StringComparison.Ordinal)
            .Replace("{STATE}", LinuxStatePath, StringComparison.Ordinal);

        /// <summary>Installs and starts the Linux program; throws with the reason when it cannot (see <see cref="LinuxSetupScript"/>).</summary>
        public static async Task SetupLinuxAsync(GuestAgentClient agent, CancellationToken cancellationToken = default)
        {
            var result = await agent.RunAsync("/bin/sh", new[] { "-c", LinuxSetupScript }, null, TimeSpan.FromSeconds(60), cancellationToken)
                .ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new GuestAgentException($"Dragging out of the guest could not be set up: {result.Error.Trim()}");
        }

        /// <summary>
        /// Lays the Linux program's strip along <paramref name="edge"/> (left, right, top, bottom), or takes it away (none).
        /// </summary>
        public static async Task ShowLinuxEdgeAsync(GuestAgentClient agent, string edge, CancellationToken cancellationToken = default)
        {
            var timeout = TimeSpan.FromSeconds(3);
            var handle = await agent.ExecuteAsync("guest-file-open", new JsonObject { ["path"] = LinuxEdgePath, ["mode"] = "w" },
                timeout, cancellationToken).ConfigureAwait(false);
            try
            {
                await agent.ExecuteAsync("guest-file-write", new JsonObject
                {
                    ["handle"] = handle!.GetValue<long>(),
                    ["buf-b64"] = Convert.ToBase64String(Encoding.ASCII.GetBytes(edge + "\n"))
                }, timeout, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await agent.ExecuteAsync("guest-file-close", new JsonObject { ["handle"] = handle!.GetValue<long>() }, timeout,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// The files being dragged in the guest right now, or null when no drag has reached the edge of its screen.
        /// </summary>
        public static async Task<IReadOnlyList<string>?> ReadAsync(GuestAgentClient agent, bool windowsGuest = true,
            CancellationToken cancellationToken = default)
        {
            var timeout = TimeSpan.FromSeconds(3);
            var handle = await agent.ExecuteAsync("guest-file-open",
                new JsonObject { ["path"] = windowsGuest ? StatePath : LinuxStatePath, ["mode"] = "r" },
                timeout, cancellationToken).ConfigureAwait(false);
            var bytes = new List<byte>();
            try
            {
                while (true)
                {
                    var chunk = await agent.ExecuteAsync("guest-file-read",
                        new JsonObject { ["handle"] = handle!.GetValue<long>(), ["count"] = 65536 }, timeout, cancellationToken)
                        .ConfigureAwait(false);
                    if (chunk?["buf-b64"]?.GetValue<string>() is { Length: > 0 } data)
                        bytes.AddRange(Convert.FromBase64String(data));
                    if (chunk?["eof"]?.GetValue<bool>() != false)
                        break;
                }
            }
            finally
            {
                await agent.ExecuteAsync("guest-file-close", new JsonObject { ["handle"] = handle!.GetValue<long>() }, timeout,
                    CancellationToken.None).ConfigureAwait(false);
            }

            return Parse(Encoding.UTF8.GetString(bytes.ToArray()));
        }

        /// <summary>
        /// Reads the state file: the files of a drag, or null when idle. Null too when the file is caught half written
        /// (no "end"): it is written in place, the next read will see it whole.
        /// </summary>
        public static IReadOnlyList<string>? Parse(string state)
        {
            var lines = state.ReplaceLineEndings("\n").Split('\n');
            if (lines.Length < 3 || lines[0] != "winqemu-dragout 1" || lines[1] != "drag" || !lines.Contains("end"))
                return null;
            var files = lines.Skip(2).TakeWhile(line => line != "end")
                .Where(line => line.StartsWith("file ", StringComparison.Ordinal))
                .Select(line => line[5..])
                .ToList();
            return files.Count > 0 ? files : null;
        }
    }
}
