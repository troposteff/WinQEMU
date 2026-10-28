using System.Globalization;

namespace WinQEMU.Core.Text
{
    /// <summary>Byte counts as people read them, in the interface's language ("2.4 GB", "2,4 Go").</summary>
    public static class Sizes
    {
        public static string Format(double bytes)
        {
            string[] units = { CommonText.UnitByte, CommonText.UnitKilobyte, CommonText.UnitMegabyte, CommonText.UnitGigabyte, CommonText.UnitTerabyte };
            var unit = 0;
            while (bytes >= 1024 && unit < units.Length - 1)
            {
                bytes /= 1024;
                unit++;
            }
            return $"{bytes.ToString(unit == 0 || bytes >= 100 ? "0" : "0.#", CultureInfo.CurrentCulture)} {units[unit]}";
        }
    }
}
