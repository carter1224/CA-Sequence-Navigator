using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SequenceNavigator
{
    /// <summary>
    /// Moving around: steps (including jumps between used ones), tabs, the keyboard,
    /// search, drag and drop, the start screen's recent list, and remembering where the
    /// window and each backup were left.
    /// </summary>
    public partial class MainWindow
    {
        private static readonly string[] GroupTabs = { "C1", "C2", "C3", "V1", "V2", "V3", "SEQ" };

        private readonly ObservableCollection<RecentFileItem> _recentItems = new();

        // Which steps of each sequence have anything on, plus every active field for search.
        // Built in the background after a backup opens; one sequence is rebuilt per edit.
        private Dictionary<string, SequenceScan> _scans = new(StringComparer.OrdinalIgnoreCase);
        private Task? _scanTask;
        private int _scanGeneration;
        private FindWindow? _findWindow;

        private void Prev_Click(object sender, RoutedEventArgs e)
        {
            if (_currentIndex > FirstStep)
            {
                GoToStep(_currentIndex - 1);
            }
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            if (_currentIndex < LastStep)
            {
                GoToStep(_currentIndex + 1);
            }
        }

        private void FirstStep_Click(object sender, RoutedEventArgs e) => GoToStep(FirstStep);

        private void LastStep_Click(object sender, RoutedEventArgs e)
        {
            if (LastUsedStep() is int step)
            {
                GoToStep(step);
            }
        }

        private void GoToStep(int step)
        {
            _currentIndex = Math.Clamp(step, FirstStep, LastStep);
            RenderCurrent();
            RefreshControlStates();
        }

        /// <summary>The highest step that has anything on, or null for an empty sequence.</summary>
        private int? LastUsedStep()
        {
            if (_currentJsonName == null || !_scans.TryGetValue(_currentJsonName, out var scan))
            {
                return null;
            }

            for (int step = LastStep; step >= FirstStep; step--)
            {
                if (scan.Used[step])
                {
                    return step;
                }
            }
            return null;
        }

        private void UpdateStepUsage()
        {
            if (!HasSeqData || _currentJsonName == null || !_scans.TryGetValue(_currentJsonName, out var scan))
            {
                StepUsageText.Text = string.Empty;
                return;
            }

            // Steps run in order from 1, so the end of the sequence is what is worth showing.
            // An empty step before the end is unusual and called out.
            int? last = LastUsedStep();
            StepUsageText.Text = last is not int end
                ? "No steps used in this sequence"
                : _currentIndex > end
                    ? string.Create(CultureInfo.InvariantCulture, $"Step {_currentIndex} is after the last step in use ({end})")
                    : !scan.Used[_currentIndex]
                        ? string.Create(CultureInfo.InvariantCulture, $"Step {_currentIndex} is empty · last step in use: {end}")
                        : string.Create(CultureInfo.InvariantCulture, $"Last step in use: {end}");
        }

        /// <summary>
        /// Commits whatever is typed in the step box. Reached only by pressing Enter —
        /// navigating on each keystroke would hop through the intermediate values (typing
        /// "25" would land on step 2 first) and re-render every field on the way.
        /// </summary>
        private void GoToTypedStep()
        {
            if (!HasSeqData)
            {
                return;
            }

            if (!int.TryParse(IndexBox.Text?.Trim(), out int value) || value < FirstStep || value > LastStep)
            {
                // Inline, not a pop-up: a red outline on the box and a line in the status bar.
                // Step 0 is intentionally out of range, not an oversight.
                IndexBox.BorderBrush = (System.Windows.Media.Brush)FindResource("DangerBrush");
                SetStatus(value == 0 && IndexBox.Text?.Trim() == "0"
                    ? $"Step 0 can't be viewed or edited. Enter {FirstStep} to {LastStep}."
                    : $"Enter a step from {FirstStep} to {LastStep}.");
                return;
            }

            GoToStep(value);
        }

        private void IndexBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Return)
            {
                GoToTypedStep();
                e.Handled = true;
            }
        }

        private void IndexBox_TextChanged(object sender, TextChangedEventArgs e) =>
            IndexBox.ClearValue(BorderBrushProperty);

        // Rejects anything that is not a digit before it reaches the box. Paste is not
        // covered, but MaxLength plus the parse check on Enter still catch that.
        private void IndexBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            foreach (var ch in e.Text)
            {
                if (!char.IsAsciiDigit(ch))
                {
                    e.Handled = true;
                    return;
                }
            }
        }

        private void SeqTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Selection changes bubble up from the cards' own drop-downs; only the tabs count.
            if (ReferenceEquals(e.OriginalSource, SeqTabs))
            {
                RememberCurrentPosition();
            }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_isBusy)
            {
                return;
            }

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

            // Inside an open drop-down, Page Up/Down belong to the list.
            bool inDropDown = Keyboard.FocusedElement is ComboBoxItem;

            switch (key)
            {
                case Key.O when ctrl:
                    OpenZip_Click(this, e);
                    break;
                case Key.U when ctrl:
                    ImportFromPlc_Click(this, e);
                    break;
                case Key.S when ctrl:
                    if (ExportBtn.IsEnabled)
                    {
                        ExportZip_Click(this, e);
                    }
                    break;
                case Key.E when ctrl:
                    if (EditToggle.IsEnabled)
                    {
                        EditToggle.IsChecked = EditToggle.IsChecked != true;
                    }
                    break;
                case Key.F when ctrl:
                    Find_Click(this, e);
                    break;
                case Key.G when ctrl:
                    if (IndexBox.IsEnabled)
                    {
                        IndexBox.Focus();
                        IndexBox.SelectAll();
                    }
                    break;
                case Key.C when ctrl && shift:
                    CompareSequences_Click(this, e);
                    break;
                case >= Key.D1 and <= Key.D7 when ctrl:
                    SelectTab(key - Key.D1);
                    break;
                case >= Key.NumPad1 and <= Key.NumPad7 when ctrl:
                    SelectTab(key - Key.NumPad1);
                    break;
                case Key.PageDown when !inDropDown:
                    if (ctrl)
                    {
                        LastStep_Click(this, e);
                    }
                    else if (NextBtn.IsEnabled)
                    {
                        Next_Click(this, e);
                    }
                    break;
                case Key.PageUp when !inDropDown:
                    if (ctrl)
                    {
                        FirstStep_Click(this, e);
                    }
                    else if (PrevBtn.IsEnabled)
                    {
                        Prev_Click(this, e);
                    }
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }

        private void SelectTab(int index)
        {
            if (HasSeqData && index >= 0 && index < SeqTabs.Items.Count)
            {
                SeqTabs.SelectedIndex = index;
            }
        }

        /// <summary>Shows a sequence at a step and on the tab that holds the group.</summary>
        private void NavigateTo(string entryName, int step, string group)
        {
            var item = JsonPicker.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Tag as string, entryName, StringComparison.OrdinalIgnoreCase));
            if (item == null)
            {
                return;
            }

            _currentIndex = Math.Clamp(step, FirstStep, LastStep);
            if (!ReferenceEquals(JsonPicker.SelectedItem, item))
            {
                JsonPicker.SelectedItem = item;
            }
            else
            {
                GoToStep(step);
            }

            int tab = Array.IndexOf(GroupTabs, group);
            if (tab >= 0)
            {
                SeqTabs.SelectedIndex = tab;
            }
        }

        private void Find_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy || _zipJsonCache.Count == 0)
            {
                return;
            }

            if (_findWindow == null)
            {
                _findWindow = new FindWindow(BuildSearchIndexAsync, entry =>
                {
                    NavigateTo(entry.EntryName, entry.Step, entry.Group);
                    Activate();
                })
                {
                    Owner = this
                };
                _findWindow.Closed += (_, _) => _findWindow = null;
                _findWindow.Show();
            }
            else
            {
                _findWindow.Activate();
            }
        }

        private async Task<IReadOnlyList<SearchEntry>> BuildSearchIndexAsync()
        {
            if (_scanTask != null)
            {
                await _scanTask;
            }

            return _scans.Values
                .Where(s => !SequenceComparer.HiddenTags.Contains(s.Sequence))
                .SelectMany(s => s.Active)
                .OrderBy(a => a.Sequence, StringComparer.OrdinalIgnoreCase)
                .ThenBy(a => a.Step)
                .ToList();
        }

        /// <summary>Scans every sequence in the background after a backup opens.</summary>
        private void StartScan()
        {
            int generation = ++_scanGeneration;
            var snapshot = new Dictionary<string, string>(_zipJsonCache, StringComparer.OrdinalIgnoreCase);
            var task = Task.Run(() => snapshot.ToDictionary(
                kv => kv.Key,
                kv => SequenceScan.Build(kv.Key, kv.Value, LookupDescription),
                StringComparer.OrdinalIgnoreCase));
            _scanTask = task;

            task.ContinueWith(t =>
            {
                // A newer backup opened while this one was scanning: drop the stale result.
                if (generation != _scanGeneration || t.IsFaulted)
                {
                    return;
                }
                _scans = t.Result;
                MarkEmptySequences();
                RefreshControlStates();
                _findWindow?.Invalidate();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>After an edit, rebuilds just the sequence that changed.</summary>
        private void RescanCurrent()
        {
            if (_currentJsonName != null && _zipJsonCache.TryGetValue(_currentJsonName, out var json))
            {
                _scans[_currentJsonName] = SequenceScan.Build(_currentJsonName, json, LookupDescription);
                MarkEmptySequences();
                _findWindow?.Invalidate();
            }
        }

        /// <summary>Marks sequences with nothing on at any step as "(empty)" in the picker.</summary>
        private void MarkEmptySequences()
        {
            foreach (var item in JsonPicker.Items.OfType<ComboBoxItem>())
            {
                if (item.Tag is not string name)
                {
                    continue;
                }
                var display = Path.GetFileNameWithoutExtension(name);
                bool empty = _scans.TryGetValue(name, out var scan) && scan.UsedCount == 0;
                item.Content = empty ? display + "  (empty)" : display;
                if (empty)
                {
                    item.Foreground = (System.Windows.Media.Brush)FindResource("SubtleTextBrush");
                }
                else
                {
                    item.ClearValue(ForegroundProperty);
                }
            }
        }

        private static string? DroppedZip(DragEventArgs e) =>
            e.Data.GetData(DataFormats.FileDrop) is string[] files
                ? files.FirstOrDefault(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                : null;

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = !_isBusy && DroppedZip(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (DroppedZip(e) is string path)
            {
                OpenBackupInteractive(path);
            }
        }

        private void RefreshRecentList()
        {
            _recentItems.Clear();
            foreach (var path in _settings.RecentFiles.Where(File.Exists))
            {
                _recentItems.Add(new RecentFileItem(path));
            }
            NoRecentText.Visibility = _recentItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RecentItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string path })
            {
                OpenBackupInteractive(path);
            }
        }

        private void RememberCurrentPosition()
        {
            if (_zipPath != null && _currentJsonName != null)
            {
                _settings.RememberPosition(_zipPath, new ViewPosition
                {
                    Sequence = _currentJsonName,
                    Step = _currentIndex,
                    Tab = SeqTabs.SelectedIndex,
                });
            }
        }

        /// <summary>Opens a backup where it was left last time: sequence, step and tab.</summary>
        private void RestorePosition(string path)
        {
            ComboBoxItem? item = null;
            if (_settings.LastPositions.TryGetValue(path, out var pos))
            {
                item = JsonPicker.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => string.Equals(i.Tag as string, pos.Sequence, StringComparison.OrdinalIgnoreCase));
            }

            if (item != null)
            {
                _currentIndex = Math.Clamp(pos!.Step, FirstStep, LastStep);
                if (pos.Tab >= 0 && pos.Tab < SeqTabs.Items.Count)
                {
                    SeqTabs.SelectedIndex = pos.Tab;
                }
                JsonPicker.SelectedItem = item;
            }
            else if (JsonPicker.Items.Count > 0)
            {
                _currentIndex = FirstStep;
                JsonPicker.SelectedIndex = 0;
            }
        }

        /// <summary>
        /// Restores the last size and position, or fits the default size to this screen:
        /// 1180x820 is taller than a 1920x1080 laptop at 150% scaling.
        /// </summary>
        private void ApplyWindowPlacement()
        {
            var work = SystemParameters.WorkArea;
            MinWidth = Math.Min(MinWidth, work.Width - 16);
            MinHeight = Math.Min(MinHeight, work.Height - 16);

            var saved = _settings.Window;
            var virtualScreen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (saved != null && saved.Width >= MinWidth && saved.Height >= MinHeight &&
                virtualScreen.IntersectsWith(new Rect(saved.Left, saved.Top, saved.Width, saved.Height)))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = saved.Left;
                Top = saved.Top;
                Width = saved.Width;
                Height = saved.Height;
                if (saved.Maximized)
                {
                    WindowState = WindowState.Maximized;
                }
                return;
            }

            Width = Math.Min(Width, work.Width - 16);
            Height = Math.Min(Height, work.Height - 16);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        private void SaveWindowPlacement()
        {
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            if (bounds.IsEmpty)
            {
                return;
            }
            _settings.Window = new WindowPlacement
            {
                Left = bounds.Left,
                Top = bounds.Top,
                Width = bounds.Width,
                Height = bounds.Height,
                Maximized = WindowState == WindowState.Maximized,
            };
        }
    }

    /// <summary>A recent backup on the start screen.</summary>
    public sealed class RecentFileItem
    {
        public RecentFileItem(string path)
        {
            Path = path;
            Name = System.IO.Path.GetFileName(path);
            var info = new FileInfo(path);
            Detail = string.Join(" · ", new[]
            {
                System.IO.Path.GetDirectoryName(path) ?? string.Empty,
                info.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, info.Length / 1024)} KB"),
            });
        }

        public string Path { get; }
        public string Name { get; }
        public string Detail { get; }
    }

    /// <summary>
    /// One sequence's step usage and active fields, read straight from its JSON. "Used"
    /// means anything on: a true command or valve, or a non-zero setpoint.
    /// </summary>
    public sealed class SequenceScan
    {
        private const int Steps = 100;

        private SequenceScan(string entryName)
        {
            Sequence = System.IO.Path.GetFileNameWithoutExtension(entryName);
        }

        public string Sequence { get; }
        public bool[] Used { get; } = new bool[Steps];
        public int UsedCount { get; private set; }
        public List<SearchEntry> Active { get; } = new();

        public static SequenceScan Build(string entryName, string json, Func<string, string, string?> describe)
        {
            var scan = new SequenceScan(entryName);
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("value", out var steps) || steps.ValueKind != JsonValueKind.Array)
                {
                    return scan;
                }

                int step = 0;
                foreach (var element in steps.EnumerateArray())
                {
                    // Step 0 is not reachable in the UI, so it neither counts nor searches.
                    if (step > 0 && step < Steps && element.ValueKind == JsonValueKind.Object)
                    {
                        scan.ScanStep(entryName, step, element, describe);
                    }
                    step++;
                }
            }
            catch (JsonException)
            {
                // A sequence the main view also rejects; it simply has no used steps.
            }
            return scan;
        }

        private void ScanStep(string entryName, int step, JsonElement element, Func<string, string, string?> describe)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var member in prop.Value.EnumerateObject())
                    {
                        if (member.Value.ValueKind == JsonValueKind.True)
                        {
                            Mark(step);
                            Active.Add(new SearchEntry(Sequence, entryName, step, prop.Name, member.Name,
                                describe(prop.Name, member.Name), "true"));
                        }
                    }
                }
                else if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetDouble(out var number) && number != 0)
                {
                    Mark(step);
                    var text = prop.Value.TryGetInt64(out var whole)
                        ? whole.ToString(CultureInfo.InvariantCulture)
                        : ((float)number).ToString(CultureInfo.InvariantCulture);
                    Active.Add(new SearchEntry(Sequence, entryName, step, "SEQ", prop.Name,
                        describe("SEQ", prop.Name), text));
                }
            }
        }

        private void Mark(int step)
        {
            if (!Used[step])
            {
                Used[step] = true;
                UsedCount++;
            }
        }
    }
}
