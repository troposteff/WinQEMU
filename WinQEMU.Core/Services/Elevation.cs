using System.ComponentModel;
using System.Diagnostics;

namespace WinQEMU.Core.Services
{
    /// <summary>Runs a program as administrator: one UAC prompt, the user's consent for that change alone.</summary>
    public static class Elevation
    {
        /// <summary>
        /// The program's exit code. OperationCanceledException when the prompt was declined. Elevation is a
        /// shell verb, so the program's output cannot be captured: callers check the outcome themselves.
        /// </summary>
        public static async Task<int> RunAsync(string fileName, string arguments, CancellationToken cancellationToken = default)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process? process;
            try
            {
                process = Process.Start(startInfo);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)   // ERROR_CANCELLED: UAC declined
            {
                throw new OperationCanceledException("L'autorisation administrateur a été refusée.", ex);
            }

            if (process is null)
                return -1;
            using (process)
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                return process.ExitCode;
            }
        }
    }
}
