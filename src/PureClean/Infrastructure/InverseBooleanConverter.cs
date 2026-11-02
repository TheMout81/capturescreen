using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PureClean.Infrastructure;

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;
}

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Coche un RadioButton quand la valeur liée est égale au paramètre (ex. un membre d'enum),
/// et renvoie le paramètre quand on le coche.
/// </summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not true || parameter is null)
            return Binding.DoNothing;

        var text = parameter.ToString()!;
        var target = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return target.IsEnum
            ? Enum.Parse(target, text)
            : System.Convert.ChangeType(text, target, CultureInfo.InvariantCulture);
    }
}

/// <summary>Vrai si la valeur est une chaîne non vide.</summary>
public sealed class IsNotEmptyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string { Length: > 0 };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
