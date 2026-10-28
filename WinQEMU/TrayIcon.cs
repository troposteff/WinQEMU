using System;
using System.IO;
using System.Runtime.InteropServices;
using WinQEMU.Text;

namespace WinQEMU
{
    /// <summary>
    /// WinQEMU's icon in the notification area while its window is hidden and machines run on: a click
    /// shows the window again, the right button offers that and quitting. WinUI has none of its own.
    /// </summary>
    /// <remarks>
    /// Its own message-only window takes the icon's messages. Explorer forgets every icon when it restarts
    /// and says so with "TaskbarCreated", upon which the icon is added again.
    /// </remarks>
    internal sealed class TrayIcon : IDisposable
    {
        private const string ClassName = "WinQEMU.TrayIcon";
        private const uint CallbackMessage = 0x8000 + 1; // WM_APP + 1
        private const uint WmCommand = 0x0111;
        private const uint WmLButtonUp = 0x0202;
        private const uint WmRButtonUp = 0x0205;
        private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
        private const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4, NifInfo = 0x10, NifShowTip = 0x80;
        private const int CommandOpen = 1, CommandQuit = 2;

        private static WndProc? s_proc;
        private static bool s_registered;
        private static TrayIcon? s_instance;
        private static uint s_taskbarCreated;

        private readonly IntPtr _window;
        private readonly IntPtr _icon;
        private bool _shown;
        private string _tip = "WinQEMU";

        /// <summary>The user asked to see the window again.</summary>
        public event Action? OpenRequested;

        /// <summary>The user asked to stop the machines and quit.</summary>
        public event Action? QuitRequested;

        public TrayIcon()
        {
            if (!s_registered)
            {
                s_proc = Dispatch;
                var windowClass = new WndClassEx
                {
                    Size = (uint)Marshal.SizeOf<WndClassEx>(),
                    WndProc = s_proc,
                    Instance = GetModuleHandle(null),
                    ClassName = ClassName
                };
                s_registered = RegisterClassEx(ref windowClass) != 0;
                s_taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            }

            // A top-level window never shown rather than a message-only one: only top-level windows hear
            // the broadcast "TaskbarCreated".
            _window = CreateWindowEx(0, ClassName, "WinQEMU tray", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "WinQEMU.ico");
            // LR_LOADFROMFILE | LR_DEFAULTSIZE, at the small icon size the notification area uses.
            _icon = LoadImage(IntPtr.Zero, iconPath, 1, GetSystemMetrics(49), GetSystemMetrics(50), 0x10 | 0x40);
            s_instance = this;
        }

        public bool IsShown => _shown;

        /// <summary>Puts the icon in the notification area, with a tooltip, and once a balloon if given.</summary>
        public void Show(string tip, string? balloonTitle = null, string? balloonText = null)
        {
            _tip = tip;
            var data = Data(NifMessage | NifIcon | NifTip | NifShowTip);
            if (balloonTitle is not null)
            {
                data.Flags |= NifInfo;
                data.InfoTitle = balloonTitle;
                data.Info = balloonText ?? string.Empty;
            }
            if (!_shown)
            {
                Shell_NotifyIcon(NimAdd, ref data);
                data.TimeoutOrVersion = 4; // NOTIFYICON_VERSION_4
                Shell_NotifyIcon(NimSetVersion, ref data);
                _shown = true;
            }
            else
            {
                Shell_NotifyIcon(NimModify, ref data);
            }
        }

        public void Hide()
        {
            if (!_shown)
                return;
            var data = Data(0);
            Shell_NotifyIcon(NimDelete, ref data);
            _shown = false;
        }

        public void Dispose()
        {
            Hide();
            if (_window != IntPtr.Zero)
                DestroyWindow(_window);
            if (_icon != IntPtr.Zero)
                DestroyIcon(_icon);
            if (ReferenceEquals(s_instance, this))
                s_instance = null;
        }

        private NotifyIconData Data(uint flags) => new()
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            Window = _window,
            Id = 1,
            Flags = flags,
            CallbackMessage = CallbackMessage,
            Icon = _icon,
            Tip = _tip.Length > 127 ? _tip[..127] : _tip,
            Info = string.Empty,
            InfoTitle = string.Empty
        };

        private static IntPtr Dispatch(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
        {
            var tray = s_instance;
            if (tray is not null && hWnd == tray._window)
            {
                if (message == CallbackMessage)
                {
                    // NOTIFYICON_VERSION_4: the mouse message is in the low word.
                    switch ((uint)(lParam.ToInt64() & 0xFFFF))
                    {
                        case WmLButtonUp:
                            tray.OpenRequested?.Invoke();
                            break;
                        case WmRButtonUp:
                            tray.ShowMenu();
                            break;
                    }
                    return IntPtr.Zero;
                }
                if (message == s_taskbarCreated && tray._shown)
                {
                    tray._shown = false;
                    tray.Show(tray._tip);
                    return IntPtr.Zero;
                }
            }
            return DefWindowProc(hWnd, message, wParam, lParam);
        }

        private void ShowMenu()
        {
            var menu = CreatePopupMenu();
            AppendMenu(menu, 0, (IntPtr)CommandOpen, DialogText.TrayOpen);
            AppendMenu(menu, 0x800, IntPtr.Zero, null); // MF_SEPARATOR
            AppendMenu(menu, 0, (IntPtr)CommandQuit, DialogText.TrayQuit);
            SetMenuDefaultItem(menu, CommandOpen, 0);
            GetCursorPos(out var point);
            // The menu closes when clicked away from only if its window is in the foreground.
            SetForegroundWindow(_window);
            // TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY: the choice comes back here.
            var command = TrackPopupMenu(menu, 0x0100 | 0x0002 | 0x0080, point.X, point.Y, 0, _window, IntPtr.Zero);
            DestroyMenu(menu);
            if (command == CommandOpen)
                OpenRequested?.Invoke();
            else if (command == CommandQuit)
                QuitRequested?.Invoke();
        }

        private delegate IntPtr WndProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WndClassEx
        {
            public uint Size;
            public uint Style;
            public WndProc WndProc;
            public int ClassExtra;
            public int WindowExtra;
            public IntPtr Instance;
            public IntPtr Icon;
            public IntPtr Cursor;
            public IntPtr Background;
            public string? MenuName;
            public string ClassName;
            public IntPtr SmallIcon;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NotifyIconData
        {
            public uint Size;
            public IntPtr Window;
            public uint Id;
            public uint Flags;
            public uint CallbackMessage;
            public IntPtr Icon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
            public uint State;
            public uint StateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
            public uint TimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
            public uint InfoFlags;
            public Guid Item;
            public IntPtr BalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            public int X;
            public int Y;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassEx(ref WndClassEx windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string name);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr icon);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenu(IntPtr menu, uint flags, IntPtr id, string? text);

        [DllImport("user32.dll")]
        private static extern bool SetMenuDefaultItem(IntPtr menu, int item, int byPosition);

        [DllImport("user32.dll")]
        private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr window, IntPtr rect);

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr menu);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? name);
    }
}
