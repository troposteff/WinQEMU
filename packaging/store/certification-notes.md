# Notes for certification

## Restricted capabilities: runFullTrust

Partner Center, Submission, Submission options, Restricted capabilities. The field is short: paste this alone.

```text
WinQEMU is a WinUI 3 desktop app that manages QEMU virtual machines. It needs full trust to start and stop QEMU processes, talk to them over local named pipes and sockets, and embed their windows. It never runs elevated itself; admin actions are user-initiated through UAC. Source: github.com/troposteff/WinQEMU
```

## Notes for certification

Partner Center, Submission, Submission options, Notes for certification. Paste the block below.

---

WinQEMU is a graphical interface over QEMU, the open source machine emulator, for creating and running virtual machines on
Windows. It does not include QEMU: the user installs QEMU for Windows separately (free; `winget install
SoftwareFreedomConservancy.QEMU` or https://qemu.weilnetz.de/w64/), as the first line of the description says. Without
QEMU, WinQEMU starts and shows an assistant explaining how to install it; nothing else can be tested then.

How to test:
1. Install QEMU for Windows (winget command above), then start WinQEMU: the "Prepare this PC" assistant shows QEMU found.
2. Create a machine with the + button (any Linux ISO, for example Ubuntu), start it: it runs in the integrated console.
3. Settings (gear button): language, theme, "Check this PC…".

runFullTrust: WinQEMU is a WinUI 3 desktop application. It starts and stops QEMU processes, talks to them over local
named pipes and sockets, and embeds the window of each QEMU process in its own; none of this is possible in an app
container.

Administrator rights: WinQEMU never runs elevated itself. Three actions, each started by a click of the user and confirmed
by the Windows UAC prompt, run a Windows tool elevated:
- enabling the Windows Hypervisor Platform feature for hardware acceleration (dism.exe /Online /Enable-Feature
  /FeatureName:HypervisorPlatform /All /NoRestart);
- allowing access to a virtual machine from the local network (netsh advfirewall firewall add rule, private and domain
  profiles only);
- adding an adapter to the TAP-Windows driver for an internal virtual network (that driver's own tapinstall.exe, only if
  the user installed the driver, and only after checking that it is signed by OpenVPN Inc.).

Drivers: WinQEMU bundles no driver and installs none itself. Two optional features use third-party drivers that the user
installs from their publishers, guided by a link: internal virtual networks (OpenVPN's TAP-Windows) and external virtual
networks (Npcap). Everything else, virtual machines included, works without them.

Microphone: declared because QEMU, started by WinQEMU, runs within the package. A virtual machine gets the microphone only
if the user ticks "Microphone" in its hardware settings; it is off by default, and Windows asks for permission the first
time.

Startup task: off by default; turned on only from WinQEMU's settings ("Start WinQEMU when signing in to Windows").

Network: WinQEMU itself sends nothing over the internet and has no telemetry. Links (QEMU, drivers) open in the browser.

Source code (GPL-3.0): https://github.com/troposteff/WinQEMU
