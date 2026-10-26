namespace WinQEMU.Core.Qemu
{
    /// <summary>
    /// A point-in-time sample of VM resource usage.
    /// </summary>
    /// <param name="HostCpuPercent">
    /// Share of the host's total CPU capacity consumed by the QEMU process. This is host-side cost,
    /// not guest-side utilisation — QEMU cannot report what the guest scheduler is doing. A busy
    /// guest and an idle-but-polling guest can look similar here.
    /// </param>
    /// <param name="ResidentMemoryBytes">
    /// QEMU's working set. Grows towards the configured guest memory as the guest touches pages, so
    /// it reads as "how much of the allocation is actually backed" rather than guest memory pressure.
    /// </param>
    /// <param name="DiskReadBytes">Cumulative bytes read across all block devices since boot.</param>
    /// <param name="DiskWrittenBytes">Cumulative bytes written across all block devices since boot.</param>
    public readonly record struct VmMetrics(
        double HostCpuPercent,
        long ResidentMemoryBytes,
        long DiskReadBytes,
        long DiskWrittenBytes);
}
