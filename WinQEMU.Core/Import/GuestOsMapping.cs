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

        /// <summary>
        /// An OVF descriptor's guest: VMware's vmw:osType ("windows9_64Guest", the vSphere spelling, which
        /// <see cref="FromVmware"/> reads as well), else VirtualBox's vbox:OSType, else the standard CIM number
        /// and its description ("Microsoft Windows 10 (64-bit)").
        /// </summary>
        public static string FromOvf(string? vmwareOsType, string? virtualBoxOsType, long? cimOsType, string description)
        {
            if (!string.IsNullOrWhiteSpace(vmwareOsType) && FromVmware(vmwareOsType) is var vmware && vmware != "other")
                return vmware;
            if (!string.IsNullOrWhiteSpace(virtualBoxOsType) && FromVirtualBox(virtualBoxOsType) is var virtualBox && virtualBox != "other")
                return virtualBox;

            var text = description.ToLowerInvariant();
            if (text.Contains("windows", StringComparison.Ordinal))
            {
                if (text.Contains("server", StringComparison.Ordinal))
                    return "windows-server";
                if (text.Contains("11", StringComparison.Ordinal))
                    return "windows-11";
                return text.Contains("10", StringComparison.Ordinal) ? "windows-10" : "windows-7";
            }

            return cimOsType switch
            {
                93 or 94 => "ubuntu",
                95 or 96 => "debian",
                79 or 80 or 106 or 107 => "fedora",
                36 or 99 or 100 or 101 => "linux",
                _ => FromLinuxName(text)
            };
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
