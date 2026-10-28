namespace WinQEMU.Core.Models
{
    /// <summary>A machine to start as WinQEMU opens, and when.</summary>
    public sealed record AutoStartEntry(VmConfig Machine, TimeSpan After);

    /// <summary>
    /// Which machines start with WinQEMU, in what order: as Hyper-V's automatic start, each its own delay
    /// after the application opens, the shortest first; equal delays keep the library's order.
    /// </summary>
    public static class AutoStartPlan
    {
        /// <summary>An hour: a delay is to let one machine come up before the next, not to schedule.</summary>
        public const int MaxDelaySeconds = 3600;

        public static IReadOnlyList<AutoStartEntry> For(IEnumerable<VmConfig> library) =>
            library
                .Select((machine, index) => (machine, index))
                .Where(m => m.machine.AutoStart)
                .OrderBy(m => m.machine.AutoStartDelaySeconds)
                .ThenBy(m => m.index)
                .Select(m => new AutoStartEntry(m.machine, TimeSpan.FromSeconds(m.machine.AutoStartDelaySeconds)))
                .ToList();
    }
}
