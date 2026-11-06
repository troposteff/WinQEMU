using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace WinQEMU.Core.Services
{
    /// <summary>
    /// Unattended installation of a Linux, the way <see cref="UbuntuUnattendedSetup"/> does Ubuntu's: the first start
    /// boots the ISO's own installer kernel (QEMU's -kernel, files read out of the ISO at creation) with the line that
    /// makes it go ahead alone, and the answers ride where that installer looks for them.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Debian (debian-installer): preseed.cfg added to the initrd, a second gzip'd cpio after the ISO's own,
    /// which the kernel unpacks over it; d-i reads /preseed.cfg by itself, before its first question.</item>
    /// <item>Fedora (Anaconda): kickstart ks.cfg on a stick labelled OEMDRV, named by inst.ks. Only an installation
    /// disc (Everything, Server: images/install.img): a live one installs by copying itself, without kickstart.</item>
    /// <item>openSUSE (YaST, linuxrc): AutoYaST's autoinst.xml on the same OEMDRV stick, named by autoyast=.</item>
    /// </list>
    /// The kernel line is kept in <see cref="CommandLineFile"/>: Fedora's names the disc's volume label, read from the
    /// ISO's own boot menu. Each installer reboots into the installed system, which starts from its disk.
    /// </remarks>
    public static class LinuxUnattendedSetup
    {
        public const string CommandLineFile = "cmdline";
        public const string OemDriveFolder = "oemdrv";
        public const string OemDriveLabel = "OEMDRV";

        private const int MaxKernel = 256 * 1024 * 1024;
        private const int MaxInitrd = 1024 * 1024 * 1024;

        /// <summary>The systems this installs unattended, by profile key.</summary>
        public static bool Supports(string guestOs) => guestOs is "ubuntu" or "debian" or "fedora" or "opensuse";

        /// <summary>Ubuntu's desktop ISO installs a desktop and its server ISO none; the others are asked.</summary>
        public static bool OffersDesktopChoice(string guestOs) => guestOs is "debian" or "fedora" or "opensuse";

        /// <summary>Whether the machine's installer reads its answers from a stick labelled OEMDRV.</summary>
        public static bool UsesOemDrive(string guestOs) => guestOs is "fedora" or "opensuse";

        private static (string Kernel, string Initrd)? BootFiles(string guestOs) => guestOs switch
        {
            "ubuntu" => ("casper/vmlinuz", "casper/initrd"),
            "debian" => ("install.amd/vmlinuz", "install.amd/initrd.gz"),
            "fedora" => ("images/pxeboot/vmlinuz", "images/pxeboot/initrd.img"),
            "opensuse" => ("boot/x86_64/loader/linux", "boot/x86_64/loader/initrd"),
            _ => null
        };

        /// <summary>Whether this ISO is an installation disc of that system this can drive.</summary>
        public static bool CanInstall(string guestOs, string isoPath)
        {
            if (guestOs == "ubuntu")
                return UbuntuUnattendedSetup.CanInstall(isoPath);
            if (BootFiles(guestOs) is not { } files || !File.Exists(isoPath) || !IsoFileReader.Contains(isoPath, files.Kernel))
                return false;
            return guestOs switch
            {
                "fedora" => IsoFileReader.Contains(isoPath, "images/install.img") && FedoraStage2(isoPath) is not null,
                // SUSE Linux Enterprise asks for a registration first: openSUSE only.
                // media.1/media rather than .treeinfo, which the ISO 9660 primary tree cannot name (a leading dot).
                "opensuse" => IsoFileReader.Read(isoPath, "media.1/media") is { } media
                    && Encoding.UTF8.GetString(media).StartsWith("openSUSE", StringComparison.Ordinal),
                _ => true
            };
        }

        /// <summary>
        /// Writes the kernel, initrd, kernel line and answers into the setup folder. False when the ISO lacks the
        /// installer this expected.
        /// </summary>
        public static bool WriteTo(string folder, UnattendedLinux settings, string guestOs, string isoPath)
        {
            if (guestOs == "ubuntu")
                return UbuntuUnattendedSetup.WriteTo(folder, settings, isoPath);
            if (BootFiles(guestOs) is not { } files
                || IsoFileReader.Read(isoPath, files.Kernel, MaxKernel) is not { } kernel
                || IsoFileReader.Read(isoPath, files.Initrd, MaxInitrd) is not { } initrd)
                return false;

            string commandLine;
            switch (guestOs)
            {
                case "debian":
                    initrd = WithFile(initrd, "preseed.cfg", DebianPreseed(settings));
                    // The text installer's own line ("--- quiet" for the installed system), at critical priority:
                    // whatever the preseed leaves out takes its default instead of a question.
                    commandLine = "priority=critical --- quiet";
                    break;
                case "fedora":
                    if (FedoraStage2(isoPath) is not { } stage2)
                        return false;
                    WriteOemDrive(folder, "ks.cfg", FedoraKickstart(settings));
                    commandLine = $"{stage2} inst.ks=hd:LABEL={OemDriveLabel}:/ks.cfg quiet";
                    break;
                case "opensuse":
                    WriteOemDrive(folder, "autoinst.xml", AutoYast(settings, OpenSuseProduct(isoPath)));
                    commandLine = $"splash=silent autoyast=label://{OemDriveLabel}/autoinst.xml";
                    break;
                default:
                    return false;
            }

            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, UbuntuUnattendedSetup.KernelFile), kernel);
            File.WriteAllBytes(Path.Combine(folder, UbuntuUnattendedSetup.InitrdFile), initrd);
            File.WriteAllText(Path.Combine(folder, CommandLineFile), commandLine, new UTF8Encoding(false));
            return true;
        }

        /// <summary>The kernel line written at creation, or Ubuntu's for a machine created before it existed.</summary>
        public static string CommandLine(string folder)
        {
            var path = Path.Combine(folder, CommandLineFile);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : UbuntuUnattendedSetup.KernelArguments;
        }

        /// <summary>
        /// "inst.stage2=hd:LABEL=Fedora-E-dvd-x86_64-44", from the ISO's own boot menu: the volume label Anaconda
        /// finds its installer image by.
        /// </summary>
        public static string? FedoraStage2(string isoPath)
        {
            foreach (var menu in new[] { "EFI/BOOT/grub.cfg", "boot/grub2/grub.cfg" })
            {
                if (IsoFileReader.Read(isoPath, menu) is { } bytes
                    && Regex.Match(Encoding.UTF8.GetString(bytes), @"inst\.stage2=\S+") is { Success: true } stage2)
                    return stage2.Value;
            }
            return null;
        }

        // ---- Debian ----

        public static string DebianPreseed(UnattendedLinux s)
        {
            var tasks = s.Desktop ? "standard, desktop, gnome-desktop" : "standard, ssh-server";
            return $"""
                # Written by WinQEMU for an unattended installation.
                d-i debian-installer/locale string {s.Locale.Replace(".UTF-8", string.Empty, StringComparison.Ordinal)}.UTF-8
                d-i keyboard-configuration/xkb-keymap select {s.KeyboardLayout}
                d-i netcfg/choose_interface select auto
                d-i netcfg/get_hostname string {s.Hostname}
                d-i netcfg/hostname string {s.Hostname}
                d-i netcfg/get_domain string
                d-i mirror/country string manual
                d-i mirror/http/hostname string deb.debian.org
                d-i mirror/http/directory string /debian
                d-i mirror/http/proxy string
                d-i passwd/root-login boolean false
                d-i passwd/user-fullname string {s.UserName}
                d-i passwd/username string {s.UserName}
                d-i passwd/user-password-crypted password {s.PasswordHash}
                d-i clock-setup/utc boolean true
                d-i time/zone string {s.TimeZone}
                d-i clock-setup/ntp boolean true
                d-i partman-auto/method string regular
                d-i partman-auto/choose_recipe select atomic
                d-i partman-partitioning/choose_label select gpt
                d-i partman-partitioning/confirm_write_new_label boolean true
                d-i partman/choose_partition select finish
                d-i partman/confirm boolean true
                d-i partman/confirm_nooverwrite boolean true
                d-i partman-efi/non_efi_system boolean true
                d-i apt-setup/cdrom/set-first boolean false
                d-i apt-setup/cdrom/set-next boolean false
                d-i apt-setup/cdrom/set-failed boolean false
                d-i apt-setup/disable-cdrom-entries boolean true
                d-i apt-setup/non-free-firmware boolean true
                tasksel tasksel/first multiselect {tasks}
                d-i pkgsel/include string qemu-guest-agent spice-vdagent
                d-i pkgsel/upgrade select none
                popularity-contest popularity-contest/participate boolean false
                d-i grub-installer/only_debian boolean true
                d-i grub-installer/bootdev string default
                d-i grub-installer/force-efi-extra-removable boolean true
                d-i finish-install/reboot_in_progress note

                """;
        }

        /// <summary>
        /// The initrd with one more file at its root: a gzip'd newc cpio appended to it, which the kernel unpacks after
        /// the ISO's own archive. How Debian documents adding a preseed file without rebuilding the initrd.
        /// </summary>
        public static byte[] WithFile(byte[] initrd, string name, string content)
        {
            using var output = new MemoryStream();
            output.Write(initrd);
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                var data = new UTF8Encoding(false).GetBytes(content.ReplaceLineEndings("\n"));
                WriteCpioEntry(gzip, name, data, mode: 0x81A4); // regular file, 0644
                WriteCpioEntry(gzip, "TRAILER!!!", Array.Empty<byte>(), mode: 0);
            }
            return output.ToArray();
        }

        private static void WriteCpioEntry(Stream stream, string name, byte[] data, int mode)
        {
            var nameBytes = Encoding.ASCII.GetBytes(name + "\0");
            var header = new StringBuilder("070701");
            foreach (var field in new[] { 0, mode, 0, 0, 1, 0, data.Length, 0, 0, 0, 0, nameBytes.Length, 0 })
                header.Append(field.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
            var headerBytes = Encoding.ASCII.GetBytes(header.ToString());
            stream.Write(headerBytes);
            stream.Write(nameBytes);
            Pad(stream, headerBytes.Length + nameBytes.Length);
            stream.Write(data);
            Pad(stream, data.Length);
        }

        private static void Pad(Stream stream, int written)
        {
            for (var i = written; i % 4 != 0; i++)
                stream.WriteByte(0);
        }

        // ---- Fedora ----

        public static string FedoraKickstart(UnattendedLinux s)
        {
            var environment = s.Desktop ? "@^workstation-product-environment" : "@^server-product-environment";
            return $"""
                # Written by WinQEMU for an unattended installation.
                text
                lang {s.Locale}
                keyboard --xlayouts='{s.KeyboardLayout}'
                timezone {s.TimeZone} --utc
                network --bootproto=dhcp --activate --hostname={s.Hostname}
                zerombr
                clearpart --all --initlabel
                autopart
                bootloader
                rootpw --lock
                user --name={s.UserName} --gecos="{s.UserName}" --groups=wheel --iscrypted --password={s.PasswordHash}
                firstboot --disable
                reboot

                %packages
                {environment}
                qemu-guest-agent
                spice-vdagent
                %end

                """;
        }

        // ---- openSUSE ----

        /// <param name="product">
        /// The base product to install: one medium offers several (Tumbleweed's: openSUSE, MicroOS, Aeon, Kalpa), and
        /// without it AutoYaST stops on "None or wrong base product has been defined".
        /// </param>
        /// <remarks>
        /// A drive without a device takes the first disk, which is the OEMDRV stick (sda, before vda): YaST stopped on
        /// "Impossible de suggérer un partitionnement". Disks under 4 GiB are skipped, whatever bus the system disk is on.
        /// kdump is off: on by default, it reserved guest memory and failed at every start ("Failed to start Load kdump").
        /// </remarks>
        public static string AutoYast(UnattendedLinux s, string product = "openSUSE")
        {
            var patterns = s.Desktop
                ? new[] { "base", "minimal_base", "enhanced_base", "x11", "kde", "kde_plasma", "fonts", "yast2_basis", "sw_management" }
                : new[] { "base", "minimal_base", "enhanced_base", "yast2_basis", "sw_management" };
            string X(string value) => SecurityElement.Escape(value);
            return $"""
                <?xml version="1.0"?>
                <!DOCTYPE profile>
                <!-- Written by WinQEMU for an unattended installation. -->
                <profile xmlns="http://www.suse.com/1.0/yast2ns" xmlns:config="http://www.suse.com/1.0/configns">
                  <general>
                    <mode>
                      <confirm config:type="boolean">false</confirm>
                    </mode>
                  </general>
                  <language>
                    <language>{X(s.Locale.Replace(".UTF-8", string.Empty, StringComparison.Ordinal))}</language>
                  </language>
                  <keyboard>
                    <keymap>{X(YastKeymap(s.KeyboardLayout))}</keymap>
                  </keyboard>
                  <timezone>
                    <hwclock>UTC</hwclock>
                    <timezone>{X(s.TimeZone)}</timezone>
                  </timezone>
                  <networking>
                    <keep_install_network config:type="boolean">true</keep_install_network>
                    <dns>
                      <hostname>{X(s.Hostname)}</hostname>
                    </dns>
                  </networking>
                  <kdump>
                    <add_crash_kernel config:type="boolean">false</add_crash_kernel>
                  </kdump>
                  <partitioning config:type="list">
                    <drive>
                      <initialize config:type="boolean">true</initialize>
                      <use>all</use>
                      <skip_list config:type="list">
                        <listentry>
                          <skip_key>size_k</skip_key>
                          <skip_value>4194304</skip_value>
                          <skip_if_less_than config:type="boolean">true</skip_if_less_than>
                        </listentry>
                      </skip_list>
                    </drive>
                  </partitioning>
                  <software>
                    <products config:type="list">
                      <product>{X(product)}</product>
                    </products>
                    <install_recommended config:type="boolean">true</install_recommended>
                    <patterns config:type="list">
                {string.Concat(patterns.Select(p => $"      <pattern>{p}</pattern>\n"))}    </patterns>
                    <packages config:type="list">
                      <package>qemu-guest-agent</package>
                      <package>spice-vdagent</package>
                      <package>openssh</package>
                    </packages>
                  </software>
                  <services-manager>
                    <default_target>{(s.Desktop ? "graphical" : "multi-user")}</default_target>
                    <services>
                      <enable config:type="list">
                        <service>sshd</service>
                      </enable>
                    </services>
                  </services-manager>
                  <users config:type="list">
                    <user>
                      <username>root</username>
                      <user_password>{X(s.PasswordHash)}</user_password>
                      <encrypted config:type="boolean">true</encrypted>
                    </user>
                    <user>
                      <username>{X(s.UserName)}</username>
                      <fullname>{X(s.UserName)}</fullname>
                      <user_password>{X(s.PasswordHash)}</user_password>
                      <encrypted config:type="boolean">true</encrypted>
                    </user>
                  </users>
                </profile>

                """;
        }

        /// <summary>
        /// The medium's own product, from media.1/products ("/ openSUSE 20261102-0": Tumbleweed's; Leap names "Leap"),
        /// else "openSUSE".
        /// </summary>
        public static string OpenSuseProduct(string isoPath)
        {
            if (IsoFileReader.Read(isoPath, "media.1/products") is { } bytes)
            {
                var fields = Encoding.UTF8.GetString(bytes).Split('\n')[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length >= 2)
                    return fields[1];
            }
            return "openSUSE";
        }

        /// <summary>YaST's keymap names, from the xkb layout this PC's keyboard maps to.</summary>
        private static string YastKeymap(string xkb) => xkb switch
        {
            "fr" => "french",
            "be" => "belgian",
            "ch" => "swiss-french",
            "ca" => "french-ca",
            "de" => "german",
            "gb" => "english-uk",
            "es" => "spanish",
            "it" => "italian",
            "pt" => "portugese",
            "br" => "portugese-br",
            "nl" => "dutch",
            _ => "english-us"
        };

        private static void WriteOemDrive(string folder, string fileName, string content)
        {
            var drive = Path.Combine(folder, OemDriveFolder);
            Directory.CreateDirectory(drive);
            File.WriteAllText(Path.Combine(drive, fileName), content.ReplaceLineEndings("\n"), new UTF8Encoding(false));
        }
    }
}
