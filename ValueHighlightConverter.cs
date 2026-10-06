using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace SequenceNavigator
{
    /// <summary>
    /// Decides whether a card shows as active (the HMI's green fill). With no parameter it
    /// returns the card background; with the parameter "TagForeground" it returns the tag
    /// name's colour, which must darken on green to stay readable (2.31:1 grey on green,
    /// 6.25:1 dark on green).
    /// </summary>
    public sealed class ValueHighlightConverter : IMultiValueConverter
    {
        private static readonly Brush HighlightBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x55, 0xB7, 0x7E)));
        private static readonly Brush ClearBrush = Brushes.Transparent;
        private static readonly Brush TagOnHighlight = Freeze(new SolidColorBrush(Color.FromRgb(0x1F, 0x24, 0x30)));
        private static readonly Brush TagOnPlain = Freeze(new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)));

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            bool highlighted = IsHighlighted(values);
            if (parameter is string mode && mode == "TagForeground")
            {
                return highlighted ? TagOnHighlight : TagOnPlain;
            }
            return highlighted ? HighlightBrush : ClearBrush;
        }

        private static bool IsHighlighted(object[] values)
        {
            if (values.Length < 2 || values[1] is not bool enabled || !enabled)
            {
                return false;
            }

            object value = values[0];
            if (value is string text)
            {
                if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                {
                    return number > 0;
                }
            }

            if (value is bool flag)
            {
                return flag;
            }

            if (value is IConvertible)
            {
                try
                {
                    return System.Convert.ToDouble(value, CultureInfo.InvariantCulture) > 0;
                }
                catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
                {
                    return false;
                }
            }

            return false;
        }

        private static Brush Freeze(Brush brush)
        {
            brush.Freeze();
            return brush;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
