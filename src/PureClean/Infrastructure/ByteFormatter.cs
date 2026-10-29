using System.Globalization;

namespace PureClean.Infrastructure;

public static class ByteFormatter
{
    private static readonly string[] Units = ["o", "Ko", "Mo", "Go", "To"];

    public static string Format(long bytes)
    {
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        var format = unit == 0 ? "0" : "0.0";
        return size.ToString(format, CultureInfo.CurrentCulture) + " " + Units[unit];
    }
}
