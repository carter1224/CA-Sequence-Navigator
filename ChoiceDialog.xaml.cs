using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace SequenceNavigator
{
    public enum ChoiceResult
    {
        Primary,
        Secondary,
        Close,
    }

    public enum ChoiceTone
    {
        Question,
        Warning,
        Danger,
    }

    /// <summary>
    /// The only pop-up left for decisions with consequences. Unlike MessageBox, every
    /// button is named for what it does ("Save and close", "Discard upload") rather than
    /// Yes/No, which is what makes people read the choice instead of clicking through it.
    /// </summary>
    public partial class ChoiceDialog : Window
    {
        private ChoiceResult _result = ChoiceResult.Close;

        private ChoiceDialog()
        {
            InitializeComponent();
        }

        /// <remarks>secondary: Null for a two-button dialog.</remarks>
        /// <remarks>items: Optional list shown under the message, such as the changes
        /// about to be written.</remarks>
        /// <remarks>safeIsDefault: Make the safe button respond to Enter, for actions
        /// where an absent-minded Enter must not proceed.</remarks>
        public static ChoiceResult Ask(Window owner, string title, string heading, string body,
            string primary, string? secondary, string close,
            IEnumerable<string>? items = null, ChoiceTone tone = ChoiceTone.Question, bool safeIsDefault = false)
        {
            var dialog = new ChoiceDialog { Owner = owner, Title = title };
            dialog.Heading.Text = heading;
            dialog.Body.Text = body;
            dialog.Body.Visibility = string.IsNullOrWhiteSpace(body) ? Visibility.Collapsed : Visibility.Visible;

            var (glyph, color) = tone switch
            {
                ChoiceTone.Danger => ("\uE7BA", "#B42318"),
                ChoiceTone.Warning => ("\uE7BA", "#8A6100"),
                _ => ("\uE897", "#2E6BE6"),
            };
            dialog.ToneIcon.Text = glyph;
            dialog.ToneIcon.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));

            dialog.PrimaryChoice.Content = primary;
            dialog.PrimaryChoice.Style = (Style)dialog.FindResource(tone == ChoiceTone.Danger ? "DangerButton" : "PrimaryButton");
            dialog.SecondaryChoice.Content = secondary;
            dialog.SecondaryChoice.Visibility = secondary == null ? Visibility.Collapsed : Visibility.Visible;
            dialog.CloseChoice.Content = close;

            var list = items?.ToList();
            if (list is { Count: > 0 })
            {
                dialog.Items.ItemsSource = list;
                dialog.ListBorder.Visibility = Visibility.Visible;
            }

            var defaultButton = safeIsDefault ? dialog.CloseChoice : dialog.PrimaryChoice;
            defaultButton.IsDefault = true;
            dialog.Loaded += (_, _) => defaultButton.Focus();

            dialog.ShowDialog();
            return dialog._result;
        }

        private void PrimaryChoice_Click(object sender, RoutedEventArgs e) => Finish(ChoiceResult.Primary);

        private void SecondaryChoice_Click(object sender, RoutedEventArgs e) => Finish(ChoiceResult.Secondary);

        private void CloseChoice_Click(object sender, RoutedEventArgs e) => Finish(ChoiceResult.Close);

        private void Finish(ChoiceResult result)
        {
            _result = result;
            DialogResult = result != ChoiceResult.Close;
        }
    }
}
