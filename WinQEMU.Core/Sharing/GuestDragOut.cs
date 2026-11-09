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
            $dir = "$env:ProgramData\WinQEMU"
            New-Item $dir -ItemType Directory -Force | Out-Null
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
            """.Replace("{EXE}", ExecutableName)
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

        /// <summary>
        /// The files being dragged in the guest right now, or null when no drag has reached the edge of its screen.
        /// </summary>
        public static async Task<IReadOnlyList<string>?> ReadAsync(GuestAgentClient agent, CancellationToken cancellationToken = default)
        {
            var timeout = TimeSpan.FromSeconds(3);
            var handle = await agent.ExecuteAsync("guest-file-open", new JsonObject { ["path"] = StatePath, ["mode"] = "r" },
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
