using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using WinQEMU.Core.Interop;
using WinQEMU.Text;

namespace WinQEMU
{
    /// <summary>
    /// A QEMU that stops answering (Core.Interop.HungConsoleWatch): the user is asked whether to power the machine off,
    /// in a Windows task dialog on a thread of its own, since the shell itself is then most likely frozen with it. Not a
    /// XAML ContentDialog: it would be shown by the UI thread, the one stuck. A WinUI window on a thread of its own was
    /// considered and left: it would read the application's resources across threads, unverified when the main one hangs.
    /// </summary>
    public sealed partial class MainWindow
    {
        private readonly HungConsoleWatch _hangWatch = new();

        private void InitializeHangWatch()
        {
            ConsoleHost.HangWatch = _hangWatch;
            ConsoleHost.MachineOf = processId =>
                _viewModel.Manager.LiveSessions.FirstOrDefault(s => s.ProcessId == processId) is { } session
                    ? (session.Config.Name, session.AppendLog, session.ForcePowerOff)
                    : null;
            _hangWatch.Ask = HungDialog.Ask;
        }

        private static class HungDialog
        {
            private const int PowerOffButton = 100, WaitButton = 101;

            private const int TdfAllowDialogCancellation = 0x0008, TdfUseCommandLinks = 0x0010, TdfCallbackTimer = 0x0800;
            private const uint TdnCreated = 0, TdnTimer = 4;
            private const uint TdmClickButton = 0x0400 + 102;
            // MAKEINTRESOURCE(-1): the -1 is cut to a WORD. new IntPtr(-1) is read as a string pointer, and WinQEMU died
            // of an access violation as soon as the dialog painted its icon.
            private static readonly IntPtr TdWarningIcon = new(0xFFFF);
            private static readonly IntPtr HwndTopMost = new(-1);
            private const uint SwpNoSize = 0x1, SwpNoMove = 0x2;

            private delegate int Callback(IntPtr hwnd, uint notification, IntPtr wParam, IntPtr lParam, IntPtr data);

            // Packed as commctrl.h declares them (pshpack1.h).
            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
            private struct Button
            {
                public int Id;
                public IntPtr Text;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
            private struct Config
            {
                public uint Size;
                public IntPtr Parent;
                public IntPtr Instance;
                public int Flags;
                public int CommonButtons;
                public string WindowTitle;
                public IntPtr MainIcon;
                public string MainInstruction;
                public string Content;
                public uint ButtonCount;
                public IntPtr Buttons;
                public int DefaultButton;
                public uint RadioButtonCount;
                public IntPtr RadioButtons;
                public int DefaultRadioButton;
                public string? VerificationText;
                public string? ExpandedInformation;
                public string? ExpandedControlText;
                public string? CollapsedControlText;
                public IntPtr FooterIcon;
                public string? Footer;
                public Callback Callback;
                public IntPtr CallbackData;
                public uint Width;
            }

            [DllImport("comctl32.dll", CharSet = CharSet.Unicode)]
            private static extern int TaskDialogIndirect(ref Config config, out int button, out int radioButton, out bool verified);

            [DllImport("user32.dll")]
            private static extern IntPtr SendMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll")]
            private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

            [DllImport("user32.dll")]
            private static extern bool SetForegroundWindow(IntPtr hwnd);

            /// <summary>True to power the machine off. Closes itself, as Wait, once <paramref name="stillStuck"/> says no.</summary>
            public static bool Ask(string name, Func<bool> stillStuck)
            {
                var powerOff = Marshal.StringToHGlobalUni(ShellText.HungConsolePowerOff);
                var wait = Marshal.StringToHGlobalUni(ShellText.HungConsoleWait);
                var buttons = Marshal.AllocHGlobal(Marshal.SizeOf<Button>() * 2);
                try
                {
                    Marshal.StructureToPtr(new Button { Id = PowerOffButton, Text = powerOff }, buttons, false);
                    Marshal.StructureToPtr(new Button { Id = WaitButton, Text = wait }, buttons + Marshal.SizeOf<Button>(), false);

                    var lastCheck = 0;
                    // Kept in a local for the whole call: the dialog calls it until it returns.
                    Callback callback = (hwnd, notification, wParam, _, _) =>
                    {
                        if (notification == TdnCreated)
                        {
                            // No owner (owning would disable the shell's window: a call into the frozen thread), so it
                            // is put above everything itself, the shell included.
                            SetWindowPos(hwnd, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize);
                            SetForegroundWindow(hwnd);
                        }
                        else if (notification == TdnTimer && (int)wParam - lastCheck >= 1000)
                        {
                            lastCheck = (int)wParam;
                            if (!stillStuck())
                                SendMessageW(hwnd, TdmClickButton, new IntPtr(WaitButton), IntPtr.Zero);
                        }
                        return 0;
                    };

                    var config = new Config
                    {
                        Size = (uint)Marshal.SizeOf<Config>(),
                        // Escape and the close box mean Wait; Wait is also the default, so Enter pressed for something
                        // else never cuts a machine's power.
                        Flags = TdfAllowDialogCancellation | TdfUseCommandLinks | TdfCallbackTimer,
                        WindowTitle = "WinQEMU",
                        MainIcon = TdWarningIcon,
                        MainInstruction = string.Format(CultureInfo.CurrentCulture, ShellText.HungConsoleInstruction, name),
                        Content = string.Format(CultureInfo.CurrentCulture, ShellText.HungConsoleContent,
                            (int)HungConsoleWatch.Threshold.TotalSeconds),
                        ButtonCount = 2,
                        Buttons = buttons,
                        DefaultButton = WaitButton,
                        Callback = callback
                    };

                    var result = TaskDialogIndirect(ref config, out var chosen, out _, out _);
                    GC.KeepAlive(callback);
                    return result == 0 && chosen == PowerOffButton;
                }
                finally
                {
                    Marshal.FreeHGlobal(buttons);
                    Marshal.FreeHGlobal(powerOff);
                    Marshal.FreeHGlobal(wait);
                }
            }
        }
    }
}
