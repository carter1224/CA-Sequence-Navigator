using System.Globalization;
using System.IO;
using System.Reflection;
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

            UiHelpers.MakeLabel(grid, "PLC IP:", 0, 0);
            _ipBox = UiHelpers.MakeTextBox(grid, settings.PlcIp, 0, 1);

            UiHelpers.MakeLabel(grid, "Ethernet Slot:", 1, 0);
            _ethBox = UiHelpers.MakeTextBox(grid, settings.EthSlot.ToString(CultureInfo.InvariantCulture), 1, 1);

            UiHelpers.MakeLabel(grid, "Controller Slot:", 2, 0);
            _cpuBox = UiHelpers.MakeTextBox(grid, settings.CpuSlot.ToString(CultureInfo.InvariantCulture), 2, 1);

            UiHelpers.MakeLabel(grid, "Retry Count:", 3, 0);
            _retryBox = UiHelpers.MakeTextBox(grid, settings.RetryCount.ToString(CultureInfo.InvariantCulture), 3, 1);

            UiHelpers.MakeLabel(grid, "Retry Delay (sec):", 4, 0);
            _delayBox = UiHelpers.MakeTextBox(grid, settings.RetryDelaySeconds.ToString(CultureInfo.InvariantCulture), 4, 1);

            UiHelpers.MakeLabel(grid, "PLC Timeout (sec):", 5, 0);
            _timeoutBox = UiHelpers.MakeTextBox(grid, settings.TimeoutSeconds.ToString(CultureInfo.InvariantCulture), 5, 1);

            _includeProgramTags = UiHelpers.MakeCheckBox(grid, "Include program tags", settings.IncludeProgramTags, 6, 1);
            _showDescriptions = UiHelpers.MakeCheckBox(grid, "Show descriptions", settings.ShowDescriptions, 7, 1);
            _highlightActive = UiHelpers.MakeCheckBox(grid, "Highlight active values", settings.HighlightActive, 8, 1);
            _enableDebug = UiHelpers.MakeCheckBox(grid, "Enable debug log", settings.EnableDebugLog, 9, 1);

            UiHelpers.MakeLabel(grid, "Log Path:", 10, 0);
            _logPathBox = UiHelpers.MakeTextBox(grid, settings.LogPath, 10, 1);
            var browseBtn = new Button { Content = "Browse", Margin = new Thickness(8, 0, 0, 0) };
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
                Text = BuildLabel(),
                VerticalAlignment = VerticalAlignment.Center
            };
            var okBtn = new Button { Content = "OK", Margin = new Thickness(8, 0, 0, 0) };
            okBtn.Click += OkBtn_Click;
            var cancelBtn = new Button { Content = "Cancel", Margin = new Thickness(8, 0, 0, 0) };
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
                PlcIp = settings.PlcIp,
                EthSlot = settings.EthSlot,
                CpuSlot = settings.CpuSlot,
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

        /// <summary>
        /// Reads the version from assembly metadata so &lt;Version&gt; in the csproj is the
        /// only place it has to be bumped.
        /// </summary>
        private static string BuildLabel()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            // Strip any "+<commit>" suffix in case the SDK appends one.
            var version = informational?.Split('+')[0];
            if (string.IsNullOrWhiteSpace(version))
            {
                version = assembly.GetName().Version?.ToString(3);
            }

            var author = assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company;
            if (string.IsNullOrWhiteSpace(author))
            {
                author = "Carter Smith";
            }

            return $"Build {version ?? "?"} by {author}";
        }

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
            if (!PlcAddress.IsValid(_ipBox.Text))
            {
                MessageBox.Show("PLC IP must be a valid IPv4 address or hostname.", "Invalid Input",
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

            Settings.PlcIp = _ipBox.Text.Trim();
            Settings.EthSlot = ethSlot;
            Settings.CpuSlot = cpuSlot;
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

    }
}

