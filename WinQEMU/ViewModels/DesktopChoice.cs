using WinQEMU.Core.Services;
using WinQEMU.Text;

namespace WinQEMU.ViewModels
{
    /// <summary>
    /// A desktop environment of the creation wizard's list, for an unattended Debian, Fedora or openSUSE: product names as
    /// they are, "None" in the interface's language.
    /// </summary>
    public sealed record DesktopChoice(LinuxDesktop Value)
    {
        public string Label => LinuxUnattendedSetup.DesktopName(Value) ?? DialogText.NewVmDesktopNone;
    }
}
