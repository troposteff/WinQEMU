using WinQEMU.Text;

namespace WinQEMU.ViewModels
{
    /// <summary>
    /// A family of the creation wizard's first list (Core.Models.GuestOsCatalog.Families). The key is what the code tests
    /// ("Windows", "Linux"…), the label what the list shows: product names as they are, "Other" in the interface's language.
    /// </summary>
    public sealed record GuestOsFamily(string Key)
    {
        public string Label => Key == "Other" ? DialogText.NewVmFamilyOther : Key;
    }
}
