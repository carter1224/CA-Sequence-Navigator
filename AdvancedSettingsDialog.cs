using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace SequenceNavigator
{
    public sealed class AdvancedSettingsDialog : Window
    {
        private readonly TextBox _ipBox;
        private readonly TextBox _ethBox;
        private readonly TextBox _cpuBox;
        private readonly TextBox _retryBox;
        private readonly TextBox _delayBox;
        private readonly TextBox _timeoutBox;
        private readonly CheckBox _includeProgramTags;
        private readonly CheckBox _showDescriptions;
        private readonly CheckBox _highlightActive;
        private readonly CheckBox _enableDebug;
        private readonly TextBox _logPathBox;

        public AdvancedSettingsDialog(AppSettings settings)
        {
            Title = "Advanced Settings";
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;

            var grid = new Grid { Margin = new Thickness(14) };
            for (int i = 0; i < 12; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            grid.Children.Add(MakeLabel("Default IP:", 0, 0));
            _ipBox = MakeTextBox(settings.DefaultIp, 0, 1, grid);

            grid.Children.Add(MakeLabel("Default Ethernet Slot:", 1, 0));
            _ethBox = MakeTextBox(settings.DefaultEthSlot.ToString(CultureInfo.InvariantCulture), 1, 1, grid);

            grid.Children.Add(MakeLabel("Default CPU Slot:", 2, 0));
            _cpuBox = MakeTextBox(settings.DefaultCpuSlot.ToString(CultureInfo.InvariantCulture), 2, 1, grid);

            grid.Children.Add(MakeLabel("Retry Count:", 3, 0));
            _retryBox = MakeTextBox(settings.RetryCount.ToString(CultureInfo.InvariantCulture), 3, 1, grid);

            grid.Children.Add(MakeLabel("Retry Delay (sec):", 4, 0));
            _delayBox = MakeTextBox(settings.RetryDelaySeconds.ToString(CultureInfo.InvariantCulture), 4, 1, grid);

            grid.Children.Add(MakeLabel("PLC Timeout (sec):", 5, 0));
            _timeoutBox = MakeTextBox(settings.TimeoutSeconds.ToString(CultureInfo.InvariantCulture), 5, 1, grid);

            _includeProgramTags = MakeCheckBox("Include program tags", settings.IncludeProgramTags, 6, 1, grid);
            _showDescriptions = MakeCheckBox("Show descriptions", settings.ShowDescriptions, 7, 1, grid);
            _highlightActive = MakeCheckBox("Highlight active values", settings.HighlightActive, 8, 1, grid);
            _enableDebug = MakeCheckBox("Enable debug log", settings.EnableDebugLog, 9, 1, grid);

            grid.Children.Add(MakeLabel("Log Path:", 10, 0));
            _logPathBox = MakeTextBox(settings.LogPath, 10, 1, grid);
            var browseBtn = new Button { Content = "Browse", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
            browseBtn.Click += BrowseBtn_Click;
            Grid.SetRow(browseBtn, 10);
            Grid.SetColumn(browseBtn, 2);
            grid.Children.Add(browseBtn);

            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var buildText = new TextBlock
            {
                Text = "Build 1.2.0 by Carter Smith",
                VerticalAlignment = VerticalAlignment.Center
            };
            var spacer = new FrameworkElement { Width = 20 };
            var okBtn = new Button { Content = "OK", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
            okBtn.Click += OkBtn_Click;
            var cancelBtn = new Button { Content = "Cancel", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
            cancelBtn.Click += (_, _) => DialogResult = false;

            footer.Children.Add(buildText);
            footer.Children.Add(new FrameworkElement { Width = 20 });
            footer.Children.Add(okBtn);
            footer.Children.Add(cancelBtn);

            Grid.SetRow(footer, 11);
            Grid.SetColumnSpan(footer, 3);
            grid.Children.Add(footer);

            Content = grid;

            Settings = new AppSettings
            {
                DefaultIp = settings.DefaultIp,
                DefaultEthSlot = settings.DefaultEthSlot,
                DefaultCpuSlot = settings.DefaultCpuSlot,
                RetryCount = settings.RetryCount,
                RetryDelaySeconds = settings.RetryDelaySeconds,
                TimeoutSeconds = settings.TimeoutSeconds,
                IncludeProgramTags = settings.IncludeProgramTags,
                ShowDescriptions = settings.ShowDescriptions,
                HighlightActive = settings.HighlightActive,
                EnableDebugLog = settings.EnableDebugLog,
                LogPath = settings.LogPath
            };
        }

        public AppSettings Settings { get; }

        private void BrowseBtn_Click(object? sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Select Log File",
                Filter = "Log Files (*.log)|*.log|All Files (*.*)|*.*",
                FileName = Path.GetFileName(_logPathBox.Text)
            };
            if (dialog.ShowDialog() == true)
            {
                _logPathBox.Text = dialog.FileName;
            }
        }

        private void OkBtn_Click(object? sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_ipBox.Text))
            {
                MessageBox.Show("Default IP is required.", "Invalid Input",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(_ethBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int ethSlot))
            {
                MessageBox.Show("Ethernet slot must be a whole number.", "Invalid Input",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(_cpuBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int cpuSlot))
            {
                MessageBox.Show("CPU slot must be a whole number.", "Invalid Input",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(_retryBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int retries) || retries < 0)
            {
                MessageBox.Show("Retry count must be 0 or greater.", "Invalid Input",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!double.TryParse(_delayBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double delay) || delay < 0)
            {
                MessageBox.Show("Retry delay must be 0 or greater.", "Invalid Input",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!double.TryParse(_timeoutBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double timeout) || timeout <= 0)
            {
                MessageBox.Show("Timeout must be greater than 0.", "Invalid Input",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Settings.DefaultIp = _ipBox.Text.Trim();
            Settings.DefaultEthSlot = ethSlot;
            Settings.DefaultCpuSlot = cpuSlot;
            Settings.RetryCount = retries;
            Settings.RetryDelaySeconds = delay;
            Settings.TimeoutSeconds = timeout;
            Settings.IncludeProgramTags = _includeProgramTags.IsChecked == true;
            Settings.ShowDescriptions = _showDescriptions.IsChecked == true;
            Settings.HighlightActive = _highlightActive.IsChecked == true;
            Settings.EnableDebugLog = _enableDebug.IsChecked == true;
            Settings.LogPath = _logPathBox.Text.Trim();

            DialogResult = true;
        }

        private static Label MakeLabel(string text, int row, int col)
        {
            var label = new Label { Content = text, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(label, row);
            Grid.SetColumn(label, col);
            return label;
        }

        private static TextBox MakeTextBox(string text, int row, int col, Grid grid)
        {
            var box = new TextBox { Text = text };
            Grid.SetRow(box, row);
            Grid.SetColumn(box, col);
            grid.Children.Add(box);
            return box;
        }

        private static CheckBox MakeCheckBox(string text, bool isChecked, int row, int col, Grid grid)
        {
            var box = new CheckBox { Content = text, IsChecked = isChecked };
            Grid.SetRow(box, row);
            Grid.SetColumn(box, col);
            grid.Children.Add(box);
            return box;
        }
    }
}

