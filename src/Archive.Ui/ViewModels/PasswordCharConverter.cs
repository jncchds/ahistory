using System.Globalization;
using Avalonia.Data.Converters;

namespace Archive.Ui.ViewModels;

/// <summary>
/// Hides what is typed, but only when it is a password.
/// </summary>
/// <remarks>
/// The same box takes a verification code and a two-step password, one after the other. A code is
/// worth showing — it is read off another screen and mistyped constantly — and a password is not,
/// because it stays legible in a window somebody else may be looking at.
/// </remarks>
public sealed class PasswordCharConverter : IValueConverter
{
    public static PasswordCharConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? '•' : '\0';

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("One way only: nothing sets which box is secret from the view.");
}
