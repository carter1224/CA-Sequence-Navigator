using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace SequenceNavigator
{
    /// <summary>A field that is on (or a setpoint that is non-zero) at one step.</summary>
    public sealed record SearchEntry(string Sequence, string EntryName, int Step, string Group, string Member,
        string? Description, string Value)
    {
        public string Tab => Group == "SEQ" ? "Setpoints" : Group;
        public string StepText => Step.ToString(CultureInfo.InvariantCulture);
        public string Field => string.IsNullOrWhiteSpace(Description) ? Member : $"{Description} ({Member})";
    }

    public partial class FindWindow : Window
    {
        private const int MaxShown = 500;

        private readonly Func<Task<IReadOnlyList<SearchEntry>>> _loadIndex;
        private readonly Action<SearchEntry> _navigate;
        private readonly DispatcherTimer _debounce;
        private IReadOnlyList<SearchEntry>? _index;

        /// <remarks>loadIndex: Builds (or returns the cached) list of active fields.</remarks>
        /// <remarks>navigate: Shows a result in the main window.</remarks>
        public FindWindow(Func<Task<IReadOnlyList<SearchEntry>>> loadIndex, Action<SearchEntry> navigate)
        {
            InitializeComponent();
            _loadIndex = loadIndex;
            _navigate = navigate;
            // Waits for a pause in typing rather than filtering on every keystroke.
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _debounce.Tick += async (_, _) =>
            {
                _debounce.Stop();
                await RunSearchAsync();
            };
            Loaded += (_, _) => SearchBox.Focus();
        }

        /// <summary>Called when the open backup changes or is edited, so results stay true.</summary>
        public void Invalidate()
        {
            _index = null;
            if (!string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                _debounce.Stop();
                _debounce.Start();
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _debounce.Stop();
            _debounce.Start();
        }

        private async Task RunSearchAsync()
        {
            var words = SearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (words.Length == 0)
            {
                Results.ItemsSource = null;
                ResultText.Text = "Type part of a description or tag name, e.g. Material Transfer Run or MTFR_R. Lists every step where it is on.";
                return;
            }

            if (_index == null)
            {
                ResultText.Text = "Reading sequences…";
                _index = await _loadIndex();
            }

            // Every word must appear in the description or the tag name, in any order.
            var matches = _index.Where(entry => words.All(w =>
                    (entry.Description?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    entry.Member.Contains(w, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            Results.ItemsSource = matches.Take(MaxShown).ToList();
            var sequences = matches.Select(m => m.Sequence).Distinct().Count();
            var summary = string.Create(CultureInfo.InvariantCulture,
                $"{matches.Count} step{(matches.Count == 1 ? "" : "s")} in {sequences} sequence{(sequences == 1 ? "" : "s")}");
            if (matches.Count > MaxShown)
            {
                summary += string.Create(CultureInfo.InvariantCulture, $" (first {MaxShown} shown)");
            }
            ResultText.Text = matches.Count == 0
                ? "Not on at any step in this backup."
                : summary + ". Double-click a row to go there.";
        }

        private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Down arrow moves into the results so the keyboard alone is enough.
            if (e.Key == Key.Down && Results.Items.Count > 0)
            {
                Results.SelectedIndex = 0;
                (Results.ItemContainerGenerator.ContainerFromIndex(0) as ListViewItem)?.Focus();
                e.Handled = true;
            }
        }

        private void Results_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                GoToSelected();
                e.Handled = true;
            }
        }

        private void Results_MouseDoubleClick(object sender, MouseButtonEventArgs e) => GoToSelected();

        private void GoTo_Click(object sender, RoutedEventArgs e) => GoToSelected();

        private void GoToSelected()
        {
            if (Results.SelectedItem is SearchEntry entry)
            {
                _navigate(entry);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
