using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace SequenceNavigator
{
    /// <summary>
    /// Differences between two backups, by sequence and step, in the same plain-English
    /// field names as the cards. "Show in main window" opens backup A at the exact spot.
    /// </summary>
    public partial class CompareWindow : Window
    {
        private const string AllSequences = "All sequences";

        private readonly Func<string, string, string?> _describe;
        private readonly Action<string, SequenceDifference> _show;
        private readonly Action? _onClosed;
        private ComparisonResult _result;
        private Dictionary<string, string> _jsonA;
        private Dictionary<string, string> _jsonB;

        /// <remarks>show: Opens backup A (its path) at the given difference.</remarks>
        /// <remarks>onClosed: Clean-up, such as deleting a temporary PLC snapshot.</remarks>
        public CompareWindow(string pathA, string labelA, string pathB, string labelB,
            Func<string, string, string?> describe, Action<string, SequenceDifference> show, Action? onClosed = null)
        {
            InitializeComponent();
            _describe = describe;
            _show = show;
            _onClosed = onClosed;
            _jsonA = SequenceComparer.LoadZipJson(pathA);
            _jsonB = SequenceComparer.LoadZipJson(pathB);
            _result = SequenceComparer.Compare(pathA, labelA, _jsonA, pathB, labelB, _jsonB, describe);
            Render();
        }

        private void Render()
        {
            LabelA.Text = _result.LabelA;
            LabelA.ToolTip = _result.PathA;
            LabelB.Text = _result.LabelB;
            LabelB.ToolTip = _result.PathB;

            int fields = _result.Differences.Count;
            SummaryText.Text = _result.Differing.Count == 0
                ? string.Create(CultureInfo.InvariantCulture, $"No differences across {_result.Compared} sequences.")
                : string.Create(CultureInfo.InvariantCulture,
                    $"{_result.Differing.Count} of {_result.Compared} sequences differ · {fields} field{(fields == 1 ? "" : "s")}");
            if (_result.Skipped.Count > 0)
            {
                SummaryText.Text += string.Create(CultureInfo.InvariantCulture,
                    $" · {_result.Skipped.Count} utility tags not compared ({string.Join(", ", _result.Skipped)})");
            }

            var items = new List<SequenceSummary>();
            if (_result.Differing.Count > 1)
            {
                items.Add(new SequenceSummary(AllSequences, fields));
            }
            items.AddRange(_result.Differing);
            SequenceList.ItemsSource = items;
            IdenticalText.Text = string.Create(CultureInfo.InvariantCulture, $"{_result.IdenticalCount} identical (not listed)");

            bool none = _result.Differing.Count == 0;
            NoDifferences.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
            DifferenceList.Visibility = none ? Visibility.Collapsed : Visibility.Visible;
            NoDifferencesDetail.Text = SummaryText.Text;
            if (!none)
            {
                SequenceList.SelectedIndex = 0;
            }
            UpdateShowState();
        }

        private void SequenceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selected = SequenceList.SelectedItem as SequenceSummary;
            DifferenceList.ItemsSource = selected == null || selected.Sequence == AllSequences
                ? _result.Differences
                : _result.Differences.Where(d => d.Sequence == selected.Sequence).ToList();
            if (DifferenceList.Items.Count > 0)
            {
                DifferenceList.SelectedIndex = 0;
            }
            UpdateShowState();
        }

        private void DifferenceList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateShowState();

        private void UpdateShowState()
        {
            var d = DifferenceList.SelectedItem as SequenceDifference;
            bool reachable = d != null && d.Step >= 1;
            ShowBtn.IsEnabled = reachable;
            ShowHint.Text = d == null
                ? string.Empty
                : reachable
                    ? $"Opens A at {d.Sequence}, step {d.StepText}, {d.Tab}"
                    : "This one has no step to open";
        }

        private void Swap_Click(object sender, RoutedEventArgs e)
        {
            (_jsonA, _jsonB) = (_jsonB, _jsonA);
            _result = SequenceComparer.Compare(_result.PathB, _result.LabelB, _jsonA, _result.PathA, _result.LabelA, _jsonB, _describe);
            Render();
        }

        private void Show_Click(object sender, RoutedEventArgs e) => ShowSelected();

        private void DifferenceList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ShowSelected();

        private void ShowSelected()
        {
            if (DifferenceList.SelectedItem is SequenceDifference d && d.Step >= 1)
            {
                _show(_result.PathA, d);
            }
        }

        private void ExportText_Click(object sender, RoutedEventArgs e) =>
            Export("Text report (*.txt)|*.txt", ".txt", SequenceComparer.BuildTextReport(_result));

        private void ExportCsv_Click(object sender, RoutedEventArgs e) =>
            Export("CSV (*.csv)|*.csv", ".csv", SequenceComparer.BuildCsv(_result));

        private void Export(string filter, string extension, string content)
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var dialog = new SaveFileDialog
            {
                Title = "Export comparison",
                Filter = filter,
                FileName = $"seq_compare_{stamp}{extension}",
                InitialDirectory = UiHelpers.ResolveInitialDirectory(Path.GetDirectoryName(_result.PathA)),
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            try
            {
                File.WriteAllText(dialog.FileName, content);
                ShowHint.Text = "Exported " + Path.GetFileName(dialog.FileName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, $"The comparison could not be saved to:\n{dialog.FileName}\n\n{ex.Message}",
                    "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _onClosed?.Invoke();
        }
    }
}
