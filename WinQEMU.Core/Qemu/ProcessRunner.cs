using System.Diagnostics;
using System.Text;

namespace WinQEMU.Core.Qemu
{
    public readonly record struct ProcessResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public bool Succeeded => ExitCode == 0;

        /// <summary>Stderr when present, otherwise stdout — whichever actually carries the diagnostic.</summary>
        public string Diagnostics =>
            string.IsNullOrWhiteSpace(StandardError) ? StandardOutput.Trim() : StandardError.Trim();
    }

    /// <summary>
    /// Runs the short-lived QEMU helper tools (qemu-img, --version probes) and captures their output.
    /// Long-running VM processes are owned by <see cref="VmSession"/> instead.
    /// </summary>
    public static class ProcessRunner
    {
        /// <param name="onOutputLine">
        /// Called with each stdout line as it arrives, on a thread-pool thread. Lines also split on a bare
        /// carriage return, which is how qemu-img redraws its progress in place.
        /// </param>
        public static async Task<ProcessResult> RunAsync(
            string executablePath,
            IEnumerable<string> arguments,
            CancellationToken cancellationToken = default,
            Action<string>? onOutputLine = null)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory
            };

            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = new Process { StartInfo = startInfo };

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                    return;
                stdout.AppendLine(e.Data);
                onOutputLine?.Invoke(e.Data);
            };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

            if (!process.Start())
                throw new InvalidOperationException($"Unable to start '{executablePath}'.");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    // Kill only requests termination. Waiting lets the caller clean up the files the
                    // tool was writing, which stay locked until the process is really gone.
                    process.WaitForExit(5000);
                }
            }
            catch
            {
                // The process already died on its own; nothing to clean up.
            }
        }
    }
}
