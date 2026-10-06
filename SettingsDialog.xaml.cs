using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace SequenceNavigator
{
    /// <summary>
    /// Grouped by what the engineer is trying to change. The PLC address and slot are not
    /// here: the PLC Connection dialog remembers them, and two places to set one value
    /// only invited them to disagree.
    /// </summary>
    public partial class SettingsDialog : Window
    {
        private const string ReleasesUrl = "https://github.com/carter1224/CA-Sequence-Navigator/releases";

        private readonly AppSettings _original;
        private readonly Action<bool, bool> _previewDisplay;
        private bool _loaded;

        /// <remarks>previewDisplay: Applies description/highlight choices to the main
        /// window immediately, so ticking a box shows its effect.</remarks>
        public SettingsDialog(AppSettings settings, Action<bool, bool> previewDisplay)
        {
            InitializeComponent();
            _original = settings;
            _previewDisplay = previewDisplay;

            ShowDescriptionsBox.IsChecked = settings.ShowDescriptions;
            HighlightBox.IsChecked = settings.HighlightActive;
            RetryBox.Text = settings.RetryCount.ToString(CultureInfo.InvariantCulture);
            DelayBox.Text = settings.RetryDelaySeconds.ToString(CultureInfo.InvariantCulture);
            TimeoutBox.Text = settings.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            ProgramTagsBox.IsChecked = settings.IncludeProgramTags;
            DebugLogBox.IsChecked = settings.EnableDebugLog;
            LogPathBox.Text = settings.LogPath;
            VersionText.Text = $"Sequence Navigator {AppVersion.Number} by {AppVersion.Author}";

            Settings = Copy(settings);
            _loaded = true;
        }

        public AppSettings Settings { get; }

        private void Display_Changed(object sender, RoutedEventArgs e)
        {
            if (_loaded)
            {
                _previewDisplay(ShowDescriptionsBox.IsChecked == true, HighlightBox.IsChecked == true);
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // Anything but Save puts the previewed display back the way it was.
            if (DialogResult != true)
            {
                _previewDisplay(_original.ShowDescriptions, _original.HighlightActive);
            }
            base.OnClosing(e);
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Choose a log file",
                Filter = "Log Files (*.log)|*.log|All Files (*.*)|*.*",
                FileName = Path.GetFileName(LogPathBox.Text),
            };
            if (dialog.ShowDialog(this) == true)
            {
                LogPathBox.Text = dialog.FileName;
            }
        }

        private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
        {
            var probe = Copy(_original);
            probe.LogPath = LogPathBox.Text.Trim();
            var folder = Path.GetDirectoryName(probe.ResolveLogPath());
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
            }
        }

        private void ReleaseNotes_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo { FileName = ReleasesUrl, UseShellExecute = true });
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            string? problem = null;
            if (!int.TryParse(RetryBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int retries) ||
                retries < 0 || retries > 20)
            {
                problem = "Retries must be a whole number from 0 to 20.";
            }
            else if (!double.TryParse(DelayBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double delay) ||
                delay < 0 || delay > 60)
            {
                problem = "The wait between retries must be from 0 to 60 seconds.";
            }
            else if (!double.TryParse(TimeoutBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double timeout) ||
                timeout < 1 || timeout > 120)
            {
                problem = "The timeout must be from 1 to 120 seconds.";
            }
            else
            {
                Settings.RetryCount = retries;
                Settings.RetryDelaySeconds = delay;
                Settings.TimeoutSeconds = timeout;
            }

            if (problem != null)
            {
                ReliabilityError.Text = problem;
                ReliabilityError.Visibility = Visibility.Visible;
                return;
            }

            Settings.ShowDescriptions = ShowDescriptionsBox.IsChecked == true;
            Settings.HighlightActive = HighlightBox.IsChecked == true;
            Settings.IncludeProgramTags = ProgramTagsBox.IsChecked == true;
            Settings.EnableDebugLog = DebugLogBox.IsChecked == true;
            Settings.LogPath = LogPathBox.Text.Trim();
            DialogResult = true;
        }

        /// <summary>Everything carried over, so fields this dialog doesn't show survive Save.</summary>
        private static AppSettings Copy(AppSettings s) => new()
        {
            PlcIp = s.PlcIp,
            EthSlot = s.EthSlot,
            CpuSlot = s.CpuSlot,
            RetryCount = s.RetryCount,
            RetryDelaySeconds = s.RetryDelaySeconds,
            TimeoutSeconds = s.TimeoutSeconds,
            IncludeProgramTags = s.IncludeProgramTags,
            ShowDescriptions = s.ShowDescriptions,
            HighlightActive = s.HighlightActive,
            EnableDebugLog = s.EnableDebugLog,
            LogPath = s.LogPath,
            LastZipDir = s.LastZipDir,
            RecentFiles = s.RecentFiles,
            RecentPlcs = s.RecentPlcs,
            Window = s.Window,
            LastPositions = s.LastPositions,
        };
    }
}
