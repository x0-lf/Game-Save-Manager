using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace GameSaves.App.Converters
{
    /// <summary>
    /// Two-way bridge between a string view-model property and a group of
    /// radio buttons: a radio is checked when the bound string equals its
    /// <c>ConverterParameter</c>, and checking a radio writes the parameter
    /// back to the source. Unchecking returns
    /// <see cref="AvaloniaProperty.UnsetValue"/> so the source is left
    /// unchanged by the radio that lost selection.
    /// </summary>
    public sealed class StringEqualityConverter : IValueConverter
    {
        public static readonly StringEqualityConverter Instance = new();

        public object Convert(
            object? value,
            Type targetType,
            object? parameter,
            CultureInfo culture) =>
            string.Equals(value as string, parameter as string, StringComparison.Ordinal);

        public object ConvertBack(
            object? value,
            Type targetType,
            object? parameter,
            CultureInfo culture) =>
            value is true ? parameter! : AvaloniaProperty.UnsetValue;
    }
}
