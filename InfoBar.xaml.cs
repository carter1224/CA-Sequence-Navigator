using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SequenceNavigator
{
    public enum InfoSeverity
    {
        Info,
        Success,
        Warning,
        Error,
    }

    /// <summary>
    /// Inline message strip, after the Windows InfoBar: an icon and colour per severity,
    /// a title and message, an optional action, and raw details hidden behind a toggle so
    /// a plain-English summary leads and the technical text is still one click away.
    /// </summary>
    public partial class InfoBar : UserControl
    {
        private Action? _action;

        public InfoBar()
        {
            InitializeComponent();
        }

        public void Show(InfoSeverity severity, string title, string message,
            string? details = null, string? actionText = null, Action? action = null)
        {
            var (background, border, glyph, iconColor) = severity switch
            {
                InfoSeverity.Success => ("#E8F5EC", "#9BD2AE", "\uE930", "#2D7D46"),
                InfoSeverity.Warning => ("#FFF4CE", "#E8C95A", "\uE7BA", "#8A6100"),
                InfoSeverity.Error => ("#FDE7E9", "#F1A9AF", "\uEA39", "#B42318"),
                _ => ("#EAF1FD", "#A9C4F5", "\uE946", "#2E6BE6"),
            };
            Root.Background = Brush(background);
            Root.BorderBrush = Brush(border);
            Icon.Text = glyph;
            Icon.Foreground = Brush(iconColor);

            TitleRun.Text = title;
            MessageRun.Text = string.IsNullOrEmpty(message) ? string.Empty : " " + message;

            DetailsBox.Text = details ?? string.Empty;
            DetailsBox.Visibility = Visibility.Collapsed;
            DetailsButton.Content = "Show details";
            DetailsButton.Visibility = string.IsNullOrWhiteSpace(details) ? Visibility.Collapsed : Visibility.Visible;

            _action = action;
            ActionButton.Content = actionText;
            ActionButton.Visibility = action != null && actionText != null ? Visibility.Visible : Visibility.Collapsed;

            Visibility = Visibility.Visible;
        }

        public void Hide()
        {
            Visibility = Visibility.Collapsed;
            _action = null;
        }

        private void DetailsButton_Click(object sender, RoutedEventArgs e)
        {
            bool show = DetailsBox.Visibility != Visibility.Visible;
            DetailsBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            DetailsButton.Content = show ? "Hide details" : "Show details";
        }

        private void ActionButton_Click(object sender, RoutedEventArgs e)
        {
            // Closed first: the action may show a new message of its own.
            var action = _action;
            Hide();
            action?.Invoke();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

        private static SolidColorBrush Brush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
    }
}
