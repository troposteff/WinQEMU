namespace WinQEMU.Core.Import
{
    /// <summary>
    /// Maps the guest identifiers of other hypervisors onto <see cref="Models.GuestOsCatalog"/> keys. Only
    /// the family matters much: it decides the clock and the notes about drivers, and the profile's
    /// defaults fill in what the source configuration leaves out.
    /// </summary>
    public static class GuestOsMapping
    {
        /// <summary>VMware's guestOS, e.g. "windows9-64" (Windows 10), "windows11-64", "ubuntu-64".</summary>
        public static string FromVmware(string guestOs)
        {
            var id = guestOs.Trim().ToLowerInvariant();
            if (id.StartsWith("windows11", StringComparison.Ordinal) || id.StartsWith("windows12", StringComparison.Ordinal))
                return "windows-11";
            // VMware never renamed the identifier: Windows 10 is "windows9".
            if (id.StartsWith("windows9", StringComparison.Ordinal))
                return "windows-10";
            if (id.StartsWith("windows", StringComparison.Ordinal) && id.Contains("srv", StringComparison.Ordinal))
                return "windows-server";
            if (id.StartsWith("windows", StringComparison.Ordinal) || id.StartsWith("win", StringComparison.Ordinal))
                return "windows-7";

            return FromLinuxName(id);
        }

        /// <summary>VirtualBox's OSType, e.g. "Windows10_64", "Ubuntu_64", "Ubuntu24_LTS_64".</summary>
        public static string FromVirtualBox(string osType)
        {
            var id = osType.Trim().ToLowerInvariant();
            if (id.StartsWith("windows11", StringComparison.Ordinal))
                return "windows-11";
            if (id.StartsWith("windows10", StringComparison.Ordinal))
                return "windows-10";
            if (id.StartsWith("windows20", StringComparison.Ordinal))
                return "windows-server";
            if (id.StartsWith("windows", StringComparison.Ordinal))
                return "windows-7";

            return FromLinuxName(id);
        }

        private static string FromLinuxName(string id)
        {
            if (id.Contains("ubuntu", StringComparison.Ordinal))
                return "ubuntu";
            if (id.Contains("debian", StringComparison.Ordinal))
                return "debian";
            if (new[] { "fedora", "rhel", "redhat", "centos", "rocky", "alma", "oracle" }.Any(n => id.Contains(n, StringComparison.Ordinal)))
                return "fedora";
            if (new[] { "linux", "suse", "arch", "mint", "gentoo", "mandriva", "turbolinux", "other26", "other3x", "other4x", "other5x", "other6x" }
                .Any(n => id.Contains(n, StringComparison.Ordinal)))
                return "linux";

            return "other";
        }
    }
}
