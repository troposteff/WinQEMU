using WinQEMU.Core.Models;

namespace WinQEMU.Core.Qemu
{
    /// <summary>
    /// The machine types and CPU models one QEMU binary actually offers.
    /// </summary>
    /// <remarks>
    /// Asked of QEMU rather than hardcoded: both lists change with the version — machine types carry the
    /// release they froze at (pc-q35-9.2), and CPU models grow with each new microarchitecture. A list
    /// baked into the shell would go stale and, worse, would offer names this binary rejects.
    /// </remarks>
    public sealed class QemuCapabilities
    {
        public required GuestArchitecture Architecture { get; init; }

        /// <summary>Machine types in the order QEMU lists them, which groups them by family.</summary>
        public required IReadOnlyList<string> MachineTypes { get; init; }

        public required IReadOnlyList<string> CpuModels { get; init; }

        public static async Task<QemuCapabilities> ProbeAsync(
            QemuInstallation installation,
            GuestArchitecture architecture,
            CancellationToken cancellationToken = default)
        {
            var binary = installation.GetSystemBinary(architecture);

            var machines = await RunAsync(binary, "-machine", cancellationToken).ConfigureAwait(false);
            var cpus = await RunAsync(binary, "-cpu", cancellationToken).ConfigureAwait(false);

            return new QemuCapabilities
            {
                Architecture = architecture,
                MachineTypes = ParseMachineTypes(machines),
                CpuModels = ParseCpuModels(cpus)
            };
        }

        private static async Task<string> RunAsync(string binary, string flag, CancellationToken cancellationToken)
        {
            var result = await ProcessRunner
                .RunAsync(binary, new[] { flag, "help" }, cancellationToken)
                .ConfigureAwait(false);

            // Both helps exit non-zero on some builds while still printing the list, so the output is
            // what matters, not the exit code.
            return string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardError : result.StandardOutput;
        }

        /// <summary>
        /// Extracts the machine names from <c>-machine help</c>, whose lines are a name, some spacing and
        /// a description: <c>q35    Standard PC (Q35 + ICH9, 2009)</c>.
        /// </summary>
        public static IReadOnlyList<string> ParseMachineTypes(string helpOutput)
        {
            var names = new List<string>();

            foreach (var line in SplitLines(helpOutput))
            {
                // "Supported machines are:" and anything else without leading content is not an entry.
                if (line.EndsWith(':') || line.StartsWith("Supported machines", StringComparison.OrdinalIgnoreCase))
                    continue;

                var name = FirstToken(line);
                if (name.Length > 0)
                    names.Add(name);
            }

            return names;
        }

        /// <summary>
        /// Extracts the model names from <c>-cpu help</c>, which lists indented names followed by an
        /// optional note, then a second section of CPUID flags that must not be mistaken for models.
        /// </summary>
        public static IReadOnlyList<string> ParseCpuModels(string helpOutput)
        {
            var names = new List<string>();

            foreach (var line in SplitLines(helpOutput))
            {
                // Everything past this heading is feature flags, not models.
                if (line.StartsWith("Recognized CPUID flags", StringComparison.OrdinalIgnoreCase))
                    break;

                if (line.EndsWith(':') || line.StartsWith("Available CPUs", StringComparison.OrdinalIgnoreCase))
                    continue;

                var name = FirstToken(line);
                if (name.Length > 0)
                    names.Add(name);
            }

            return names;
        }

        private static IEnumerable<string> SplitLines(string text) =>
            text.Replace("\r\n", "\n").Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0);

        private static string FirstToken(string line)
        {
            var end = line.IndexOfAny(new[] { ' ', '\t' });
            return end < 0 ? line : line[..end];
        }
    }
}
