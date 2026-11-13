using System.Net;

namespace WinQEMU.Core.Qemu
{
    /// <summary>
    /// The loopback address of WinQEMU's own channels to QEMU (QMP, guest agent, shared folders, file drop, clipboard VNC)
    /// and of its virtual switches: on this PC only, and out of reach of the guests.
    /// </summary>
    /// <remarks>
    /// QEMU's NAT turns a guest's connection to 10.0.2.2 into one to 127.0.0.1 of this PC, whatever the port (the WebDAV
    /// shares rely on it). With the agent's port on 127.0.0.1, any process of any guest, an unprivileged user's included,
    /// reached it there: verified on Ubuntu, uid 1000 ran <c>guest-exec</c> as root through its own agent, and could have
    /// driven another machine's agent, QMP or switch just as well. The NAT translates to 127.0.0.1 alone; the rest of
    /// 127.0.0.0/8 is loopback on Windows too, without any setting, and no guest can name it.
    /// </remarks>
    public static class PrivateLoopback
    {
        /// <summary>As QEMU's options and connections write it.</summary>
        public const string Host = "127.87.81.1";

        public static readonly IPAddress Address = IPAddress.Parse(Host);
    }
}
