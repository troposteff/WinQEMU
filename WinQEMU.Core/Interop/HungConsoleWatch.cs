using System.Collections.Concurrent;

namespace WinQEMU.Core.Interop
{
    /// <summary>
    /// Watches the console window of each QEMU, from a thread of its own, and says when one stops answering.
    /// </summary>
    /// <remarks>
    /// A QEMU whose main thread is stuck takes WinQEMU down with it: its console is a child of the shell's window, so
    /// the two threads share their input processing, and the shell's next call into the console (a resize, a region)
    /// waits on a thread that never answers. Seen with vmmouse on the user's Ryzen (Windows then reported WinQEMU, not
    /// QEMU, as hung: MoAppHangXProc), and nothing in the shell could react, its own thread being the one stuck. Hence
    /// this thread, which needs nothing from the UI's: it asks Windows whether QEMU's window still processes messages
    /// and, past <see cref="Threshold"/>, calls <see cref="Ask"/>, which may only use Win32 (a dialog of its own,
    /// which shows while the shell is frozen). The power-off given to <see cref="Track"/> kills QEMU (VmSession.ForcePowerOff), which releases the shell: what it was waiting on is gone.
    /// QMP cannot do it instead: commands run on QEMU's main loop, the very thread that is stuck.
    /// </remarks>
    public sealed class HungConsoleWatch
    {
        /// <summary>How long a console must stay unanswering before the user is asked: a busy guest boot is not a hang.</summary>
        public static readonly TimeSpan Threshold = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Asked again this long after the user chose to wait, if it is still stuck. The question says so
        /// (ShellText.HungConsoleText): without the delay, a user who answered No thought it would never come back.
        /// </summary>
        public static readonly TimeSpan AskAgainAfter = TimeSpan.FromMinutes(1);

        private sealed class Entry
        {
            public required IntPtr Handle { get; init; }
            public required string Name { get; init; }
            public Action<string>? Log { get; init; }
            public required Action PowerOff { get; init; }
            public DateTime? HungSince { get; set; }
            public DateTime? AskedAt { get; set; }
            public volatile bool Asking;
        }

        private readonly ConcurrentDictionary<int, Entry> _consoles = new();
        private readonly Thread _thread;

        public HungConsoleWatch()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "WinQEMU hung console watch" };
            _thread.Start();
        }

        /// <summary>
        /// Called on a thread of its own with the machine's name once its console has not answered for
        /// <see cref="Threshold"/>, and a probe saying whether it is still stuck (cheap, never blocking: for closing the
        /// question by itself once QEMU is back); true powers it off. Never touches the UI thread, likely the one stuck.
        /// </summary>
        public Func<string, Func<bool>, bool>? Ask { get; set; }

        /// <summary>
        /// Watches this QEMU's console. Forgotten by itself once the window is gone. <paramref name="log"/> is called on
        /// the watch thread: it must not wait on the UI.
        /// </summary>
        public void Track(int processId, IntPtr handle, string name, Action<string>? log, Action powerOff) =>
            _consoles[processId] = new Entry { Handle = handle, Name = name, Log = log, PowerOff = powerOff };

        private void Run()
        {
            while (true)
            {
                foreach (var (processId, entry) in _consoles)
                {
                    if (!NativeMethods.IsWindow(entry.Handle))
                    {
                        _consoles.TryRemove(processId, out _);
                        continue;
                    }
                    Check(entry);
                }
                Thread.Sleep(1000);
            }
        }

        private void Check(Entry entry)
        {
            if (Answers(entry.Handle, 2000))
            {
                if (entry.HungSince is not null)
                    entry.Log?.Invoke("Console: QEMU's window answers again.");
                entry.HungSince = null;
                entry.AskedAt = null;
                return;
            }

            var now = DateTime.UtcNow;
            if (entry.HungSince is null)
            {
                entry.HungSince = now;
                entry.Log?.Invoke("Console: QEMU's window has stopped answering messages.");
            }
            if (entry.Asking || now - entry.HungSince < Threshold
                || (entry.AskedAt is { } asked && now - asked < AskAgainAfter))
                return;

            entry.Asking = true;
            entry.AskedAt = now;
            // A thread per question: the watch goes on for the other machines meanwhile.
            new Thread(() =>
            {
                try
                {
                    // Checked again after the answer: it may have come back while the box was open.
                    if (Ask?.Invoke(entry.Name, () => StillStuck(entry.Handle)) == true && !Answers(entry.Handle, 500))
                        entry.PowerOff();
                }
                finally
                {
                    entry.Asking = false;
                }
            }) { IsBackground = true, Name = "WinQEMU hung console question" }.Start();
        }

        /// <summary>
        /// WM_NULL with SMTO_ABORTIFHUNG: returns at once for a thread Windows already considers hung (5 s without
        /// reading its messages), otherwise waits up to the timeout. Nothing here blocks on QEMU for longer.
        /// </summary>
        /// <summary>IsHungAppWindow: what Windows itself thinks (5 s without reading messages), and it does not wait.</summary>
        private static bool StillStuck(IntPtr handle) =>
            NativeMethods.IsWindow(handle) && NativeMethods.IsHungAppWindow(handle);

        private static bool Answers(IntPtr handle, uint timeoutMs) =>
            NativeMethods.SendMessageTimeout(handle, NativeMethods.WmNull, IntPtr.Zero, IntPtr.Zero,
                NativeMethods.SmtoAbortIfHung | NativeMethods.SmtoBlock, timeoutMs, out _) != IntPtr.Zero
            || !NativeMethods.IsWindow(handle);
    }
}
