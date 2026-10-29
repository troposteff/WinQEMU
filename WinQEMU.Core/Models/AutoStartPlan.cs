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
            Order(library.Where(machine => machine.AutoStart));

        /// <summary>
        /// A folder started at once (LibraryTree.MachinesIn): every machine of it, whether it starts with WinQEMU or not, in
        /// the same order and with the same delays, counted from the click. Their delays are what orders a group of machines
        /// that depend on one another (a server first, its clients after), as VMware's teams do.
        /// </summary>
        public static IReadOnlyList<AutoStartEntry> ForGroup(IEnumerable<VmConfig> machines) => Order(machines);

        private static IReadOnlyList<AutoStartEntry> Order(IEnumerable<VmConfig> machines) =>
            machines
                .Select((machine, index) => (machine, index))
                .Where(m => !m.machine.IsTemplate)
                .OrderBy(m => m.machine.AutoStartDelaySeconds)
                .ThenBy(m => m.index)
                .Select(m => new AutoStartEntry(m.machine, TimeSpan.FromSeconds(m.machine.AutoStartDelaySeconds)))
                .ToList();
    }
}
