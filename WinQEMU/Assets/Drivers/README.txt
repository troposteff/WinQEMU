TAP-Windows driver installer, bundled with WinQEMU
=================================================

File     : tap-windows-setup.exe (published as tap-windows-9.24.7-I601-Win10.exe)
Version  : 9.24.7 (latest release of the standalone installer)
Source   : https://build.openvpn.net/downloads/releases/tap-windows-9.24.7-I601-Win10.exe
SHA-256  : 1C44E77AB148DDFB174DE8041100EFC38C4F23500183FAFF88F9BADAC5782E3C
Signed by: OpenVPN Inc. (Authenticode, DigiCert timestamp)

TAP-Windows is OpenVPN's virtual Ethernet adapter driver, licensed under the GNU
General Public License, version 2. Its source code is available at
https://github.com/OpenVPN/tap-windows6

WinQEMU runs this installer, with the user's consent (a UAC prompt), to give this
PC a card on an internal virtual network. It checks the installer's signature
before running it, and adds further adapters with the installer's tapinstall.exe.
