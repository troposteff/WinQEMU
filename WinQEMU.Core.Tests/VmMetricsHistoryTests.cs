using WinQEMU.Core.Qemu;

namespace WinQEMU.Core.Tests;

/// <summary>The history behind the resource charts: throughput from cumulative counters, two minutes kept.</summary>
public class VmMetricsHistoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private static VmMetrics Sample(long read = 0, long written = 0, double cpu = 0, long memory = 0) =>
        new(0, memory, read, written, cpu);

    [Fact]
    public void Disk_throughput_is_the_counters_difference_per_second()
    {
        var history = new VmMetricsHistory();
        history.Add(T0, Sample(read: 1000, written: 0), null);
        history.Add(T0.AddSeconds(2), Sample(read: 5000, written: 8000), null);

        Assert.True(double.IsNaN(history.DiskRead[^2]));   // nothing to compare the first sample with
        Assert.Equal(2000, history.DiskRead[^1]);
        Assert.Equal(4000, history.DiskWrite[^1]);
    }

    [Fact]
    public void A_counter_that_goes_back_gives_zero_not_a_negative_spike()
    {
        // QEMU started again after a guest reboot: its counters start from zero.
        var history = new VmMetricsHistory();
        history.Add(T0, Sample(read: 900_000), null);
        history.Add(T0.AddSeconds(1), Sample(read: 1_000), null);

        Assert.Equal(0, history.DiskRead[^1]);
    }

    [Fact]
    public void Network_keeps_its_last_rate_between_two_readings_of_the_agent()
    {
        var history = new VmMetricsHistory();
        history.Add(T0, Sample(), (100, 50));
        Assert.False(history.HasNetwork);
        history.Add(T0.AddSeconds(1), Sample(), null);
        history.Add(T0.AddSeconds(3), Sample(), (3100, 650));
        history.Add(T0.AddSeconds(4), Sample(), null);

        Assert.True(history.HasNetwork);
        Assert.Equal(1000, history.NetworkReceived[^2]);
        Assert.Equal(200, history.NetworkSent[^2]);
        Assert.Equal(1000, history.NetworkReceived[^1]);
    }

    [Fact]
    public void Two_minutes_are_kept_oldest_first()
    {
        var history = new VmMetricsHistory();
        for (var i = 0; i < VmMetricsHistory.Capacity + 30; i++)
            history.Add(T0.AddSeconds(i), Sample(cpu: i), null);

        Assert.Equal(VmMetricsHistory.Capacity, history.Count);
        Assert.Equal(30, history.Cpu[0]);
        Assert.Equal(VmMetricsHistory.Capacity + 29, history.Cpu[^1]);
    }

    [Fact]
    public void Nothing_is_known_before_the_first_sample()
    {
        var history = new VmMetricsHistory();
        history.Add(T0, Sample(cpu: 12, memory: 4096), null);

        Assert.True(double.IsNaN(history.Cpu[0]));
        Assert.Equal(12, history.Cpu[^1]);
        Assert.Equal(4096, history.MemoryBytes[^1]);
    }
}
