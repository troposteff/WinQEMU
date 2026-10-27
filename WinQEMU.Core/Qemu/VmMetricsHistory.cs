namespace WinQEMU.Core.Qemu
{
    /// <summary>
    /// The last two minutes of a machine's resource use, one sample a second, for the shell's charts:
    /// processor, memory, and disk and network throughput worked out from QEMU's and the agent's
    /// cumulative counters.
    /// </summary>
    /// <remarks>
    /// Values are oldest first; <see cref="double.NaN"/> marks a moment with nothing known (before the
    /// first sample, or a network without its agent). A counter that goes backwards — QEMU started again
    /// after a guest reboot, the guest's card reset — gives a rate of 0, not a negative spike.
    /// </remarks>
    public sealed class VmMetricsHistory
    {
        public const int Capacity = 120;

        private readonly double[] _cpu = Filled();
        private readonly double[] _memory = Filled();
        private readonly double[] _diskRead = Filled();
        private readonly double[] _diskWrite = Filled();
        private readonly double[] _networkReceived = Filled();
        private readonly double[] _networkSent = Filled();
        private int _count;

        private DateTimeOffset? _lastDiskAt;
        private long _lastRead, _lastWritten;
        private DateTimeOffset? _lastNetworkAt;
        private long _lastReceived, _lastSent;
        private double _receivedRate = double.NaN, _sentRate = double.NaN;

        private static double[] Filled()
        {
            var values = new double[Capacity];
            Array.Fill(values, double.NaN);
            return values;
        }

        /// <summary>Share of the machine's processors, 0-100.</summary>
        public IReadOnlyList<double> Cpu => _cpu;

        /// <summary>Memory QEMU holds for the guest, in bytes.</summary>
        public IReadOnlyList<double> MemoryBytes => _memory;

        /// <summary>Bytes per second.</summary>
        public IReadOnlyList<double> DiskRead => _diskRead;

        public IReadOnlyList<double> DiskWrite => _diskWrite;

        public IReadOnlyList<double> NetworkReceived => _networkReceived;

        public IReadOnlyList<double> NetworkSent => _networkSent;

        /// <summary>How many samples are in, up to <see cref="Capacity"/>.</summary>
        public int Count => _count;

        /// <summary>Whether the network has ever been measured: without an agent it never is.</summary>
        public bool HasNetwork => !double.IsNaN(_receivedRate);

        /// <param name="network">The agent's counters when they were read this time, else null: the last rate is kept.</param>
        public void Add(DateTimeOffset at, VmMetrics metrics, (long Received, long Sent)? network)
        {
            double read = double.NaN, write = double.NaN;
            if (_lastDiskAt is { } lastDisk && (at - lastDisk).TotalSeconds > 0)
            {
                read = Rate(metrics.DiskReadBytes, _lastRead, at - lastDisk);
                write = Rate(metrics.DiskWrittenBytes, _lastWritten, at - lastDisk);
            }
            _lastDiskAt = at;
            _lastRead = metrics.DiskReadBytes;
            _lastWritten = metrics.DiskWrittenBytes;

            if (network is { } counters)
            {
                if (_lastNetworkAt is { } lastNetwork && (at - lastNetwork).TotalSeconds > 0)
                {
                    _receivedRate = Rate(counters.Received, _lastReceived, at - lastNetwork);
                    _sentRate = Rate(counters.Sent, _lastSent, at - lastNetwork);
                }
                _lastNetworkAt = at;
                _lastReceived = counters.Received;
                _lastSent = counters.Sent;
            }

            Push(_cpu, metrics.GuestCpuPercent);
            Push(_memory, metrics.ResidentMemoryBytes);
            Push(_diskRead, read);
            Push(_diskWrite, write);
            Push(_networkReceived, _receivedRate);
            Push(_networkSent, _sentRate);
            _count = Math.Min(Capacity, _count + 1);
        }

        private static double Rate(long now, long before, TimeSpan elapsed) =>
            now < before ? 0 : (now - before) / elapsed.TotalSeconds;

        private static void Push(double[] values, double value)
        {
            Array.Copy(values, 1, values, 0, values.Length - 1);
            values[^1] = value;
        }
    }
}
