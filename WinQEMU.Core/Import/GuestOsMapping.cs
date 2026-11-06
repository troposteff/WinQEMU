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
                79 or 80 or 106 or 107 => "rhel",
                82 or 83 or 84 or 85 => "opensuse",
                42 or 78 => "freebsd",
                36 or 99 or 100 or 101 => "linux",
                _ => FromLinuxName(text)
            };
        }

        private static string FromLinuxName(string id)
        {
            bool Has(params string[] names) => names.Any(n => id.Contains(n, StringComparison.Ordinal));

            // Derivatives first: "Linux Mint 22 (Ubuntu based)" is Mint, not Ubuntu.
            if (Has("pfsense"))
                return "pfsense";
            if (Has("opnsense"))
                return "opnsense";
            if (Has("freebsd"))
                return "freebsd";
            if (Has("mint"))
                return "linux-mint";
            if (Has("pop!_os", "pop-os", "popos"))
                return "pop-os";
            if (Has("zorin"))
                return "zorin";
            if (Has("kali"))
                return "kali";
            if (Has("manjaro"))
                return "manjaro";
            if (Has("ubuntu"))
                return "ubuntu";
            if (Has("debian"))
                return "debian";
            if (Has("fedora"))
                return "fedora";
            if (Has("rhel", "redhat", "red hat", "centos", "rocky", "alma", "oracle"))
                return "rhel";
            if (Has("suse", "sles"))
                return "opensuse";
            // Not "arch" alone: it would match "architecture" in a descriptor's text.
            if (Has("archlinux", "arch linux"))
                return "arch";
            if (Has("nixos"))
                return "nixos";
            if (Has("alpine"))
                return "alpine";
            if (Has("linux", "gentoo", "mandriva", "turbolinux", "other26", "other3x", "other4x", "other5x", "other6x"))
                return "linux";

            return "other";
        }
    }
}
