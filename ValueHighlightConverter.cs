using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace SequenceNavigator
{
    public sealed class ValueHighlightConverter : IMultiValueConverter
    {
        private static readonly Brush HighlightBrush = new SolidColorBrush(Color.FromRgb(0x55, 0xB7, 0x7E));
        private static readonly Brush ClearBrush = Brushes.Transparent;

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2 || values[1] is not bool enabled || !enabled)
            {
                return ClearBrush;
            }

            object value = values[0];
            if (value is string text)
            {
                if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return HighlightBrush;
                }

                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                {
                    return number > 0 ? HighlightBrush : ClearBrush;
                }
            }

            if (value is bool flag)
            {
                return flag ? HighlightBrush : ClearBrush;
            }

            if (value is IConvertible)
            {
                try
                {
                    double number = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
                    return number > 0 ? HighlightBrush : ClearBrush;
                }
                catch
                {
                    return ClearBrush;
                }
            }

            return ClearBrush;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}

