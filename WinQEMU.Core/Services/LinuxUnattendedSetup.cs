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

        /// <summary>The desktops offered, in the wizard's order; None (a console with SSH) last.</summary>
        public static IReadOnlyList<LinuxDesktop> Desktops { get; } =
        [
            LinuxDesktop.Gnome, LinuxDesktop.Kde, LinuxDesktop.Xfce, LinuxDesktop.Cinnamon, LinuxDesktop.Mate,
            LinuxDesktop.Lxqt, LinuxDesktop.None
        ];

        /// <summary>The distribution's own: what its installer picks when nobody chooses.</summary>
        public static LinuxDesktop DefaultDesktop(string guestOs) => guestOs == "opensuse" ? LinuxDesktop.Kde : LinuxDesktop.Gnome;

        /// <summary>The environment's own name, the same in every language; null for None.</summary>
        public static string? DesktopName(LinuxDesktop desktop) => desktop switch
        {
            LinuxDesktop.Gnome => "GNOME",
            LinuxDesktop.Kde => "KDE Plasma",
            LinuxDesktop.Xfce => "Xfce",
            LinuxDesktop.Cinnamon => "Cinnamon",
            LinuxDesktop.Mate => "MATE",
            LinuxDesktop.Lxqt => "LXQt",
            _ => null
        };

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
                    commandLine = FedoraCommandLine(stage2);
                    break;
                case "opensuse":
                    WriteOemDrive(folder, "autoinst.xml", AutoYast(settings, OpenSuseProduct(isoPath), OpenSuseOnlineRepository(isoPath)));
                    // kexec=3: Tumbleweed's repository moves on, and an ISO a few days old no longer has the kernel of
                    // the packages; linuxrc then stopped on "To use the selected repository a matching boot image is
                    // needed. Download it now and restart?". 3 loads that kernel without asking, and only when needed.
                    // ifcfg=*=dhcp: from a DVD, linuxrc has no need of the network and leaves it down; YaST then stopped
                    // on "Réseau non fonctionnel" while saving its settings (86 %), and the online repository the DVD
                    // gets (OpenSuseOnlineRepository) could not be reached. The NET medium brings it up anyway.
                    commandLine = $"splash=silent kexec=3 ifcfg=*=dhcp autoyast=label://{OemDriveLabel}/autoinst.xml";
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
            // tasksel's tasks: "desktop" (X, the common desktop packages) and one per environment.
            var tasks = s.Desktop switch
            {
                LinuxDesktop.None => "standard, ssh-server",
                LinuxDesktop.Gnome => "standard, desktop, gnome-desktop",
                LinuxDesktop.Kde => "standard, desktop, kde-desktop",
                LinuxDesktop.Xfce => "standard, desktop, xfce-desktop",
                LinuxDesktop.Cinnamon => "standard, desktop, cinnamon-desktop",
                LinuxDesktop.Mate => "standard, desktop, mate-desktop",
                LinuxDesktop.Lxqt => "standard, desktop, lxqt-desktop",
                _ => throw new ArgumentOutOfRangeException(nameof(s))
            };
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

        /// <summary>The installer's kernel line: its image (<see cref="FedoraStage2"/>) and the kickstart.</summary>
        public static string FedoraCommandLine(string stage2) =>
            $"{stage2} inst.ks=hd:LABEL={OemDriveLabel}:/ks.cfg quiet";

        /// <summary>
        /// Run by Anaconda before it sets up its sources: waits until the network is up and Fedora's mirror server answers by
        /// name, twice, three seconds apart (a minute at most).
        /// </summary>
        /// <remarks>
        /// From the netinst ISO (Fedora 45), Anaconda set up its sources about a second after NetworkManager had brought the
        /// card up, and the very first lookup failed ("Could not resolve host: mirrors.fedoraproject.org", packaging.log,
        /// 3 ms after systemd-resolved's "Using degraded feature set UDP instead of UDP+EDNS0 for DNS server 10.0.2.3", 0.4 s
        /// before NetworkManager handed it the DNS servers): it stopped on "Erreur lors de la configuration des dépôts".
        /// Name resolution worked a moment later, and from a shell every time. The network brought up from the initramfs
        /// (ip=dhcp) changed nothing: NetworkManager re-applies it as Anaconda starts.
        /// </remarks>
        internal const string FedoraWaitForNetwork = """
            %pre --log=/tmp/winqemu-pre.log
            nm-online -s -q -t 60
            ok=0
            for i in $(seq 60); do
              if curl -sSI --max-time 5 -o /dev/null https://mirrors.fedoraproject.org; then ok=$((ok + 1)); else ok=0; fi
              echo "$(date +%T.%N) try $i ok $ok"
              [ "$ok" -ge 2 ] && break
              sleep 3
            done
            %end
            """;

        public static string FedoraKickstart(UnattendedLinux s)
        {
            // Environments of the comps file (GNOME is Workstation's).
            var environment = s.Desktop switch
            {
                LinuxDesktop.None => "@^server-product-environment",
                LinuxDesktop.Gnome => "@^workstation-product-environment",
                LinuxDesktop.Kde => "@^kde-desktop-environment",
                LinuxDesktop.Xfce => "@^xfce-desktop-environment",
                LinuxDesktop.Cinnamon => "@^cinnamon-desktop-environment",
                LinuxDesktop.Mate => "@^mate-desktop-environment",
                LinuxDesktop.Lxqt => "@^lxqt-desktop-environment",
                _ => throw new ArgumentOutOfRangeException(nameof(s))
            };
            // A text-mode installation leaves the installed system on a text login unless told otherwise: Fedora
            // Workstation was installed whole and still started on "autotest-fedora login:".
            var display = s.HasDesktop ? "xconfig --startxonboot" : "skipx";
            // KDE's first-boot assistant (mandatory in its comps groups), which "firstboot --disable" does not reach: Fedora
            // KDE started on "Bienvenue sur Plasma Desktop, Commencer la configuration" with the account already made.
            var excluded = s.Desktop == LinuxDesktop.Kde ? "\n-plasma-setup" : string.Empty;
            // No --activate: NetworkManager has the card up already, and re-activating it cut it off just as Anaconda set up
            // its sources ("Could not resolve host: mirrors.fedoraproject.org" one second after the re-activation, two
            // before NetworkManager's "dns-change"). The line still names the installed system. See FedoraWaitForNetwork.
            return $"""
                # Written by WinQEMU for an unattended installation.
                text
                lang {s.Locale}
                keyboard --xlayouts='{s.KeyboardLayout}'
                timezone {s.TimeZone} --utc
                network --bootproto=dhcp --hostname={s.Hostname}
                zerombr
                clearpart --all --initlabel
                autopart
                bootloader
                rootpw --lock
                user --name={s.UserName} --gecos="{s.UserName}" --groups=wheel --iscrypted --password={s.PasswordHash}
                firstboot --disable
                {display}
                reboot

                %packages
                {environment}
                qemu-guest-agent
                spice-vdagent{excluded}
                %end

                {FedoraWaitForNetwork}

                %post
                {UnconfinedAgent}
                %end

                """;
        }

        /// <summary>
        /// Lets what the guest agent runs reach the user's files: Fedora and openSUSE confine qemu-ga in SELinux's
        /// virt_qemu_ga_t, and a command it ran could neither list /home ("Permission non accordée") nor load 9pnet_fd,
        /// so shared folders, file drops and pulls failed. Only the agent's domain is made permissive (denials are still
        /// logged), not SELinux. The host drives the agent anyway: it is the machine's own hypervisor.
        /// </summary>
        internal const string UnconfinedAgent = """
            if command -v semodule >/dev/null; then
              printf '(typepermissive virt_qemu_ga_t)\n' > /tmp/winqemu_qemu_ga.cil
              semodule -i /tmp/winqemu_qemu_ga.cil
              rm -f /tmp/winqemu_qemu_ga.cil
            fi
            """;

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
        /// <param name="onlineRepository">
        /// A repository added to the installation, for a medium that does not carry every package asked for (see
        /// <see cref="OpenSuseOnlineRepository"/>); null for none.
        /// </param>
        public static string AutoYast(UnattendedLinux s, string product = "openSUSE", string? onlineRepository = null)
        {
            string[] desktop = s.Desktop switch
            {
                LinuxDesktop.None => [],
                LinuxDesktop.Gnome => ["x11", "gnome", "fonts"],
                LinuxDesktop.Kde => ["x11", "kde", "kde_plasma", "fonts"],
                LinuxDesktop.Xfce => ["x11", "xfce", "fonts"],
                LinuxDesktop.Cinnamon => ["x11", "cinnamon", "fonts"],
                LinuxDesktop.Mate => ["x11", "mate", "fonts"],
                LinuxDesktop.Lxqt => ["x11", "lxqt", "fonts"],
                _ => throw new ArgumentOutOfRangeException(nameof(s))
            };
            string[] patterns = ["base", "minimal_base", "enhanced_base", .. desktop, "yast2_basis", "sw_management"];
            string X(string value) => SecurityElement.Escape(value);
            var addOn = onlineRepository is null ? string.Empty : $"""
                  <add-on>
                    <add_on_products config:type="list">
                      <listentry>
                        <media_url>{X(onlineRepository)}</media_url>
                        <product_dir>/</product_dir>
                        <alias>repo-oss</alias>
                        <name>Main Repository (OSS)</name>
                      </listentry>
                    </add_on_products>
                  </add-on>

                """;
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
                {addOn}
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
                    <default_target>{(s.HasDesktop ? "graphical" : "multi-user")}</default_target>
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
                  <scripts>
                    <chroot-scripts config:type="list">
                      <script>
                        <chrooted config:type="boolean">true</chrooted>
                        <filename>winqemu-agent.sh</filename>
                        <source><![CDATA[{UnconfinedAgent}
                {OpenSuseAgentCommands}]]></source>
                      </script>
                    </chroot-scripts>
                  </scripts>
                {(s.HasDesktop ? X11Keyboard(s.KeyboardLayout) : string.Empty)}</profile>

                """;
        }

        /// <summary>
        /// openSUSE starts qemu-ga with "--block-rpcs=guest-exec,guest-exec-status", which WinQEMU needs for shared
        /// folders, file drops and pulls ("Command guest-exec has been disabled"). The same command line without it.
        /// </summary>
        internal const string OpenSuseAgentCommands = """
            mkdir -p /etc/systemd/system/qemu-guest-agent.service.d
            printf '[Service]\nExecStart=\nExecStart=/usr/bin/qemu-ga -p /dev/virtio-ports/org.qemu.guest_agent.0\n' > /etc/systemd/system/qemu-guest-agent.service.d/winqemu.conf
            """;

        /// <summary>
        /// The graphical keyboard layout, which &lt;keymap&gt; does not set: openSUSE with Xfce and LXQt showed "us" on the login
        /// screen (LightDM, SDDM) with "french" as the keymap. The file localectl writes, read by X, the login screens and
        /// Wayland compositors (through systemd-localed).
        /// </summary>
        private static string X11Keyboard(string xkb) => $"""
              <files config:type="list">
                <file>
                  <file_path>/etc/X11/xorg.conf.d/00-keyboard.conf</file_path>
                  <file_contents><![CDATA[Section "InputClass"
                    Identifier "system-keyboard"
                    MatchIsKeyboard "on"
                    Option "XkbLayout" "{xkb}"
                EndSection
                ]]></file_contents>
                  <file_owner>root.root</file_owner>
                  <file_permissions>644</file_permissions>
                </file>
              </files>

            """;

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

        /// <summary>Tumbleweed's main online repository.</summary>
        public const string TumbleweedOnlineRepository = "https://download.opensuse.org/tumbleweed/repo/oss/";

        /// <summary>
        /// The online repository to add to the installation: Tumbleweed's for its DVD (media.1/media names it
        /// "openSUSE-Tumbleweed-DVD-…"), none otherwise.
        /// </summary>
        /// <remarks>
        /// The DVD carries a selection of packages only, spice-vdagent not among them: AutoYaST stopped on "Ces paquets
        /// n'ont pas pu être trouvés dans les dépôts de logiciels : spice-vdagent" (Tumbleweed DVD of 30/10/2026). The
        /// NET medium installs from that repository already. Leap's DVD is left as it is: not tried.
        /// </remarks>
        public static string? OpenSuseOnlineRepository(string isoPath) =>
            IsoFileReader.Read(isoPath, "media.1/media") is { } bytes && IsTumbleweedDvd(Encoding.UTF8.GetString(bytes))
                ? TumbleweedOnlineRepository
                : null;

        /// <summary>Whether media.1/media is that of Tumbleweed's DVD.</summary>
        public static bool IsTumbleweedDvd(string media) =>
            media.Contains("openSUSE-Tumbleweed-DVD-", StringComparison.OrdinalIgnoreCase);

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
