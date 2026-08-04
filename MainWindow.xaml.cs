using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SequenceNavigator
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private const int SeqLength = 100;

        // SEQ[0] is deliberately not navigable or editable. The element still exists in
        // the JSON and is written back to the PLC untouched — the array must stay 100
        // long — it simply is not reachable from the UI.
        private const int FirstStep = 1;
        private const int LastStep = SeqLength - 1;

        // Utility tags rather than real sequences, so they are not offered in the picker.
        // They stay in the cache and in the ZIP, so saving and downloading still round-trip
        // them untouched — this hides them, it does not drop them.
        private static readonly HashSet<string> HiddenTags = new(StringComparer.OrdinalIgnoreCase)
        {
            "EMPTYSEQ",
            "COPYSEQDATA",
            "PROGRAM_SEQ_DATA",
        };
        private static readonly TimeSpan MinProcessTimeout = TimeSpan.FromMinutes(3);
        private int _currentIndex;
        private readonly ObservableCollection<FieldItem> _c1Items = new();
        private readonly ObservableCollection<FieldItem> _c2Items = new();
        private readonly ObservableCollection<FieldItem> _c3Items = new();
        private readonly ObservableCollection<FieldItem> _v1Items = new();
        private readonly ObservableCollection<FieldItem> _v2Items = new();
        private readonly ObservableCollection<FieldItem> _v3Items = new();
        private readonly ObservableCollection<FieldItem> _setpointItems = new();
        private readonly Dictionary<string, string> _zipJsonCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _zipJsonOriginal = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, string>> _typeDescriptions =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _groupTypeMap = new(StringComparer.OrdinalIgnoreCase)
        {
            { "C1", "CMD1" },
            { "C2", "CMD2" },
            { "C3", "CMD3" },
            { "V1", "Valves_1" },
            { "V2", "Valves_2" },
            { "V3", "Valves_3" },
            { "SEQ", "SEQ" },
        };
        private string? _zipPath;
        private string? _currentJsonName;
        private JsonNode? _currentJsonRoot;
        private bool _hasPendingEdits;
        private bool _suppressEditToggle;
        private bool _isBusy;
        private AppSettings _settings = new();
        private bool _showDescriptions = true;
        private bool _highlightActive = true;
        private bool _isEditMode;
        private bool _settingsWarningShown;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            C1Grid.ItemsSource = _c1Items;
            C2Grid.ItemsSource = _c2Items;
            C3Grid.ItemsSource = _c3Items;
            V1Grid.ItemsSource = _v1Items;
            V2Grid.ItemsSource = _v2Items;
            V3Grid.ItemsSource = _v3Items;
            SetpointGrid.ItemsSource = _setpointItems;
            LoadL5xDescriptions();
            LoadSettings();
            RefreshControlStates();
        }

        public bool ShowDescriptions
        {
            get => _showDescriptions;
            set
            {
                if (_showDescriptions == value)
                {
                    return;
                }
                _showDescriptions = value;
                OnPropertyChanged(nameof(ShowDescriptions));
            }
        }

        public bool HighlightActive
        {
            get => _highlightActive;
            set
            {
                if (_highlightActive == value)
                {
                    return;
                }
                _highlightActive = value;
                OnPropertyChanged(nameof(HighlightActive));
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // A helper is mid-transfer; closing now would orphan it while it is still
            // talking to the controller.
            if (_isBusy)
            {
                MessageBox.Show(
                    "A PLC transfer is still running. Wait for it to finish before closing.",
                    "Transfer In Progress",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                e.Cancel = true;
                return;
            }

            if (_hasPendingEdits)
            {
                var result = MessageBox.Show(
                    "You have unsaved changes. Save them before closing?",
                    "Unsaved Changes",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    // Closing anyway after a failed save would discard the very edits
                    // the user just asked to keep.
                    if (!SaveZipInPlace(showMessages: true))
                    {
                        e.Cancel = true;
                        return;
                    }
                }
                else if (result != MessageBoxResult.No)
                {
                    e.Cancel = true;
                    return;
                }
            }

            base.OnClosing(e);
        }

        public bool IsEditMode
        {
            get => _isEditMode;
            set
            {
                if (_isEditMode == value)
                {
                    return;
                }
                _isEditMode = value;
                OnPropertyChanged(nameof(IsEditMode));
            }
        }

        private bool HasSeqData =>
            _currentJsonRoot?["value"] is JsonArray arr && arr.Count == SeqLength;

        private void SetBusy(bool isBusy)
        {
            _isBusy = isBusy;
            RefreshControlStates();
        }

        /// <summary>
        /// The single source of truth for control enablement and the step readout.
        /// Everything here derives from _isBusy, the cache count, HasSeqData and
        /// _currentIndex, so it is safe to call after any state change.
        /// </summary>
        private void RefreshControlStates()
        {
            bool idle = !_isBusy;
            bool hasZip = _zipJsonCache.Count > 0;
            bool hasSeq = HasSeqData;

            // Always available unless a helper is running.
            ImportBtn.IsEnabled = idle;
            OpenZipBtn.IsEnabled = idle;
            AdvancedBtn.IsEnabled = idle;
            CompareBtn.IsEnabled = idle;

            // Need JSON loaded from a ZIP.
            JsonPicker.IsEnabled = idle && hasZip;
            EditToggle.IsEnabled = idle && hasZip;
            ExportBtn.IsEnabled = idle && hasZip;
            ExportPlcBtn.IsEnabled = idle && hasZip;

            // Need a valid 100-element sequence to navigate.
            PrevBtn.IsEnabled = idle && hasSeq && _currentIndex > FirstStep;
            NextBtn.IsEnabled = idle && hasSeq && _currentIndex < LastStep;
            IndexBox.IsEnabled = idle && hasSeq;

            IndexLabel.Text = hasSeq ? $"Step {_currentIndex}" : $"Step {FirstStep}";
            if (!IndexBox.IsFocused)
            {
                IndexBox.Text = hasSeq ? _currentIndex.ToString(CultureInfo.InvariantCulture) : string.Empty;
            }
        }

        private void OpenZip_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select SEQ ZIP file",
                Filter = "ZIP Files (*.zip)|*.zip|All Files (*.*)|*.*",
                InitialDirectory = UiHelpers.ResolveInitialDirectory(_settings.LastZipDir)
            };

            if (dialog.ShowDialog() == true)
            {
                _settings.LastZipDir = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
                SaveSettings();
                LoadZip(dialog.FileName);
            }
        }

        private void AdvancedSettings_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new AdvancedSettingsDialog(_settings)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true)
            {
                _settings = dialog.Settings;
                SaveSettings();
                ApplySettings();
            }
        }

        private async void ImportFromPlc_Click(object sender, RoutedEventArgs e)
        {
            var connDialog = new PlcConnectionDialog(_settings.PlcIp, _settings.EthSlot, _settings.CpuSlot)
            {
                Owner = this
            };

            if (connDialog.ShowDialog() != true)
            {
                return;
            }

            // Remember whatever was entered here, not just what Advanced Settings set.
            var ip = connDialog.IpAddress;
            var ethSlot = connDialog.EthSlot;
            var cpuSlot = connDialog.CpuSlot;
            _settings.PlcIp = ip;
            _settings.EthSlot = ethSlot;
            _settings.CpuSlot = cpuSlot;
            // Persist now: cancelling the save dialog below returns early, and the
            // address the user just typed should survive that.
            SaveSettings();

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var defaultName = $"seq_export_{stamp}.zip";
            var saveDialog = new SaveFileDialog
            {
                Title = "Save Export ZIP",
                Filter = "ZIP Files (*.zip)|*.zip|All Files (*.*)|*.*",
                FileName = defaultName,
                InitialDirectory = UiHelpers.ResolveInitialDirectory(_settings.LastZipDir)
            };

            if (saveDialog.ShowDialog() != true)
            {
                return;
            }

            _settings.LastZipDir = Path.GetDirectoryName(saveDialog.FileName) ?? string.Empty;
            SaveSettings();

            var scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "seq_exporter.py");
            if (!File.Exists(scriptPath))
            {
                MessageBox.Show("seq_exporter.py was not found next to the executable.",
                    "Missing Exporter", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            SetBusy(true);
            try
            {
                var args = new List<string>
                {
                    scriptPath,
                    "--ip", ip,
                    "--eth-slot", ethSlot.ToString(CultureInfo.InvariantCulture),
                    "--cpu-slot", cpuSlot.ToString(CultureInfo.InvariantCulture),
                    "--out-zip", saveDialog.FileName,
                    "--retries", _settings.RetryCount.ToString(CultureInfo.InvariantCulture),
                    "--retry-delay", _settings.RetryDelaySeconds.ToString(CultureInfo.InvariantCulture),
                    "--timeout", _settings.TimeoutSeconds.ToString(CultureInfo.InvariantCulture),
                };
                if (_settings.IncludeProgramTags)
                {
                    args.Add("--include-program-tags");
                }
                var result = await RunProcessAsync(ResolvePythonPath(), args);

                if (result.ExitCode != 0)
                {
                    MessageBox.Show(
                        "Import failed.\n\n" + result.StdErr + result.StdOut,
                        "Import Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                LoadZip(saveDialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to run the PLC import.\n\n" + ex.Message,
                    "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void LoadZip(string path)
        {
            try
            {
                _zipPath = path;
                _zipJsonCache.Clear();
                _zipJsonOriginal.Clear();
                JsonPicker.ItemsSource = null;
                JsonPicker.Items.Clear();
                ClearSeqContent();
                _hasPendingEdits = false;

                using var archive = ZipFile.OpenRead(path);
                foreach (var entry in archive.Entries)
                {
                    if (!entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    using var stream = entry.Open();
                    using var reader = new StreamReader(stream);
                    var jsonText = reader.ReadToEnd();
                    // Cached regardless, so a save rewrites it byte-for-byte even when
                    // the tag is not selectable.
                    _zipJsonCache[entry.FullName] = jsonText;
                    _zipJsonOriginal[entry.FullName] = jsonText;

                    var display = Path.GetFileNameWithoutExtension(entry.FullName);
                    if (HiddenTags.Contains(display))
                    {
                        continue;
                    }

                    var item = new ComboBoxItem { Content = display, Tag = entry.FullName };
                    JsonPicker.Items.Add(item);
                }

                // Show the file name; the directory is long and rarely what the user
                // needs on screen, so it lives in the tooltip.
                PathBox.Text = Path.GetFileName(path);
                PathBox.ToolTip = path;
                if (JsonPicker.Items.Count > 0)
                {
                    JsonPicker.SelectedIndex = 0;
                }
                else
                {
                    MessageBox.Show("No JSON files found in the ZIP.", "No JSON Files",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to load ZIP:\n" + ex.Message, "Load Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }

            RefreshControlStates();
        }

        private void JsonPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (JsonPicker.SelectedItem is not ComboBoxItem item ||
                item.Tag is not string name)
            {
                return;
            }

            if (!_zipJsonCache.TryGetValue(name, out var json))
            {
                return;
            }

            LoadJsonContent(name, json);
        }

        private void LoadJsonContent(string name, string json)
        {
            // Validate into locals first. Committing to _currentJsonName/_currentJsonRoot
            // before the checks pass would leave the grids rendering the previously loaded
            // file while pointing every edit at a document we already rejected.
            try
            {
                var root = JsonNode.Parse(json);
                if (root is null)
                {
                    throw new InvalidOperationException("JSON root is empty.");
                }

                if (root["value"] is not JsonArray values)
                {
                    ClearSeqContent();
                    MessageBox.Show("Expected a JSON file with a 'value' array.", "Invalid SEQ File",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (values.Count != SeqLength)
                {
                    ClearSeqContent();
                    MessageBox.Show($"Expected 'value' array length of {SeqLength}.", "Invalid SEQ File",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                _currentJsonName = name;
                _currentJsonRoot = root;
                _currentIndex = FirstStep;
                RenderCurrent();
                RefreshControlStates();
            }
            catch (Exception ex)
            {
                ClearSeqContent();
                MessageBox.Show("Failed to load JSON:\n" + ex.Message, "Load Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ClearFieldItems()
        {
            _c1Items.Clear();
            _c2Items.Clear();
            _c3Items.Clear();
            _v1Items.Clear();
            _v2Items.Clear();
            _v3Items.Clear();
            _setpointItems.Clear();
        }

        private void ClearSeqContent()
        {
            _currentJsonName = null;
            _currentJsonRoot = null;
            _currentIndex = FirstStep;
            ClearFieldItems();
            RefreshControlStates();
        }

        private void Prev_Click(object sender, RoutedEventArgs e)
        {
            if (_currentIndex > FirstStep)
            {
                _currentIndex--;
                RenderCurrent();
                RefreshControlStates();
            }
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            if (_currentIndex < LastStep)
            {
                _currentIndex++;
                RenderCurrent();
                RefreshControlStates();
            }
        }

        private void RenderCurrent()
        {
            ClearFieldItems();
            if (!HasSeqData)
            {
                return;
            }

            JsonArray arr = (JsonArray)_currentJsonRoot!["value"]!;
            JsonObject? nodeObject = arr[_currentIndex] as JsonObject;
            if (nodeObject == null)
            {
                _setpointItems.Add(new FieldItem("Value", arr[_currentIndex]?.ToString() ?? string.Empty));
                return;
            }

            foreach (var prop in nodeObject)
            {
                switch (prop.Key)
                {
                    case "C1":
                        FillGroup(_c1Items, nodeObject, "C1");
                        break;
                    case "C2":
                        FillGroup(_c2Items, nodeObject, "C2");
                        break;
                    case "C3":
                        FillGroup(_c3Items, nodeObject, "C3");
                        break;
                    case "V1":
                        FillGroup(_v1Items, nodeObject, "V1");
                        break;
                    case "V2":
                        FillGroup(_v2Items, nodeObject, "V2");
                        break;
                    case "V3":
                        FillGroup(_v3Items, nodeObject, "V3");
                        break;
                    default:
                        AddFieldItem(_setpointItems, nodeObject, prop.Key, "SEQ");
                        break;
                }
            }
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

            if (!int.TryParse(IndexBox.Text?.Trim(), out int value))
            {
                MessageBox.Show($"Enter a number from {FirstStep} to {LastStep}.", "Invalid Index",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (value < FirstStep || value > LastStep)
            {
                // Step 0 is intentionally out of range, not an oversight.
                MessageBox.Show(
                    value == 0
                        ? $"Step 0 cannot be viewed or edited. Enter {FirstStep} to {LastStep}."
                        : $"Index must be between {FirstStep} and {LastStep}.",
                    "Invalid Index",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _currentIndex = value;
            RenderCurrent();
            RefreshControlStates();
        }

        private void IndexBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter || e.Key == System.Windows.Input.Key.Return)
            {
                GoToTypedStep();
                e.Handled = true;
            }
        }

        // Rejects anything that is not a digit before it reaches the box. Paste is not
        // covered, but MaxLength plus the parse check on Enter still catch that.
        private void IndexBox_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
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

        private void FillGroup(ObservableCollection<FieldItem> target, JsonObject? nodeObject, string groupName)
        {
            JsonObject? groupObj = nodeObject?[groupName] as JsonObject;
            if (groupObj == null)
            {
                return;
            }

            foreach (var prop in groupObj)
            {
                AddFieldItem(target, groupObj, prop.Key, groupName);
            }
        }

        private void AddFieldItem(ObservableCollection<FieldItem> target, JsonObject parent, string key, string? groupName = null)
        {
            JsonNode? node = parent[key];
            string? description = groupName == null ? null : LookupDescription(groupName, key);
            if (node is not JsonValue valueNode)
            {
                target.Add(new FieldItem(key, node?.ToString() ?? string.Empty, description: description));
                return;
            }

            if (valueNode.TryGetValue<bool>(out bool boolValue))
            {
                target.Add(new FieldItem(
                    key,
                    boolValue ? "true" : "false",
                    isBoolean: true,
                    isNumber: false,
                    valueSetter: v => SetBooleanValue(parent, key, v),
                    description: description));
                return;
            }

            if (valueNode.TryGetValue<int>(out int intValue))
            {
                target.Add(new FieldItem(
                    key,
                    intValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    isBoolean: false,
                    isNumber: true,
                    valueSetter: v => SetNumberValue(parent, key, v, isInteger: true),
                    description: description));
                return;
            }

            if (valueNode.TryGetValue<double>(out double dblValue))
            {
                target.Add(new FieldItem(
                    key,
                    dblValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    isBoolean: false,
                    isNumber: true,
                    valueSetter: v => SetNumberValue(parent, key, v, isInteger: false),
                    description: description));
                return;
            }

            target.Add(new FieldItem(key, node?.ToString() ?? string.Empty, description: description));
        }

        private bool SetBooleanValue(JsonObject parent, string key, string rawValue)
        {
            if (!bool.TryParse(rawValue, out bool value))
            {
                WarnRejectedValue(key, rawValue, "true or false");
                return false;
            }

            parent[key] = value;
            _hasPendingEdits = true;
            UpdateJsonCache();
            return true;
        }

        private bool SetNumberValue(JsonObject parent, string key, string rawValue, bool isInteger)
        {
            if (isInteger)
            {
                if (!int.TryParse(rawValue, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int intValue))
                {
                    WarnRejectedValue(key, rawValue, "a whole number");
                    return false;
                }
                parent[key] = intValue;
            }
            else
            {
                if (!double.TryParse(rawValue, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double dblValue))
                {
                    WarnRejectedValue(key, rawValue, "a number");
                    return false;
                }
                parent[key] = dblValue;
            }

            _hasPendingEdits = true;
            UpdateJsonCache();
            return true;
        }

        // Queued rather than shown inline: this runs from inside a binding update, and a
        // modal dialog there fights with the focus change that triggered it.
        private void WarnRejectedValue(string key, string rawValue, string expected)
        {
            Dispatcher.BeginInvoke(new Action(() =>
                MessageBox.Show(
                    $"'{rawValue}' is not {expected}. {key} was left unchanged.",
                    "Invalid Value",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning)));
        }

        private void LoadL5xDescriptions()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var l5xPath = Path.Combine(baseDir, "SEQ_DataType.L5X");

            // Not shipped alongside the exe: fields fall back to showing bare member
            // names, which is a supported configuration rather than a failure.
            if (!File.Exists(l5xPath))
            {
                return;
            }

            try
            {
                var doc = XDocument.Load(l5xPath);
                foreach (var dataType in doc.Descendants("DataType"))
                {
                    var typeName = (string?)dataType.Attribute("Name");
                    if (string.IsNullOrWhiteSpace(typeName))
                    {
                        continue;
                    }

                    var members = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var member in dataType.Descendants("Member"))
                    {
                        var memberName = (string?)member.Attribute("Name");
                        if (string.IsNullOrWhiteSpace(memberName))
                        {
                            continue;
                        }

                        var description = member.Element("Description")?.Value?.Trim();
                        if (string.IsNullOrWhiteSpace(description))
                        {
                            continue;
                        }

                        if (description.StartsWith("(", StringComparison.Ordinal))
                        {
                            int idx = description.IndexOf(')');
                            if (idx >= 0 && idx < description.Length - 1)
                            {
                                description = description[(idx + 1)..].Trim();
                            }
                        }

                        members[memberName] = description;
                    }

                    if (members.Count > 0)
                    {
                        _typeDescriptions[typeName] = members;
                    }
                }
            }
            catch (Exception ex)
            {
                // The file is present but unreadable. Dropping every description without
                // a word looks like the feature is broken, so say so once.
                Dispatcher.BeginInvoke(new Action(() =>
                    MessageBox.Show(
                        "Field descriptions could not be read from SEQ_DataType.L5X, so fields " +
                        $"will show tag names only.\n\n{ex.Message}",
                        "Descriptions Unavailable",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning)));
            }
        }

        private string? LookupDescription(string groupName, string memberName)
        {
            if (_groupTypeMap.TryGetValue(groupName, out var typeName) &&
                _typeDescriptions.TryGetValue(typeName, out var members) &&
                members.TryGetValue(memberName, out var description))
            {
                return description;
            }

            return null;
        }

        private void UpdateJsonCache()
        {
            if (_currentJsonName == null || _currentJsonRoot == null)
            {
                return;
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            _zipJsonCache[_currentJsonName] = _currentJsonRoot.ToJsonString(options);
        }

        private void EditToggle_Checked(object sender, RoutedEventArgs e)
        {
            IsEditMode = true;
        }

        private void EditToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            IsEditMode = false;

            if (_suppressEditToggle || !_hasPendingEdits)
            {
                return;
            }

            var result = MessageBox.Show(
                "You have unsaved changes. Discard them and exit Edit Mode?",
                "Discard Changes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.No)
            {
                _suppressEditToggle = true;
                EditToggle.IsChecked = true;
                _suppressEditToggle = false;
                return;
            }

            _zipJsonCache.Clear();
            foreach (var kvp in _zipJsonOriginal)
            {
                _zipJsonCache[kvp.Key] = kvp.Value;
            }
            _hasPendingEdits = false;

            if (_currentJsonName != null && _zipJsonCache.TryGetValue(_currentJsonName, out var json))
            {
                LoadJsonContent(_currentJsonName, json);
            }
        }

        private void ExportZip_Click(object sender, RoutedEventArgs e)
        {
            if (!SaveZipInPlace(showMessages: true))
            {
                return;
            }

            if (EditToggle.IsChecked == true)
            {
                _suppressEditToggle = true;
                EditToggle.IsChecked = false;
                _suppressEditToggle = false;
            }
        }

        private async void ExportToPlc_Click(object sender, RoutedEventArgs e)
        {
            if (_zipJsonCache.Count == 0 || string.IsNullOrWhiteSpace(_zipPath))
            {
                MessageBox.Show("Load a ZIP with JSON files first.", "No Data",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_hasPendingEdits)
            {
                var result = MessageBox.Show(
                    "You have unsaved changes. Save them before exporting to the PLC?",
                    "Save Changes",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    if (!SaveZipInPlace(showMessages: false))
                    {
                        return;
                    }
                }
                else
                {
                    return;
                }
            }

            var connDialog = new PlcConnectionDialog(_settings.PlcIp, _settings.EthSlot, _settings.CpuSlot)
            {
                Owner = this
            };

            if (connDialog.ShowDialog() != true)
            {
                return;
            }

            // Remember whatever was entered here, not just what Advanced Settings set.
            var ip = connDialog.IpAddress;
            var ethSlot = connDialog.EthSlot;
            var cpuSlot = connDialog.CpuSlot;
            _settings.PlcIp = ip;
            _settings.EthSlot = ethSlot;
            _settings.CpuSlot = cpuSlot;
            SaveSettings();

            var scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "seq_importer.py");
            if (!File.Exists(scriptPath))
            {
                MessageBox.Show("seq_importer.py was not found next to the executable.",
                    "Missing Importer", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            SetBusy(true);
            try
            {
                var args = new List<string>
                {
                    scriptPath,
                    "--ip", ip,
                    "--eth-slot", ethSlot.ToString(CultureInfo.InvariantCulture),
                    "--cpu-slot", cpuSlot.ToString(CultureInfo.InvariantCulture),
                    "--zip", _zipPath,
                    "--retries", _settings.RetryCount.ToString(CultureInfo.InvariantCulture),
                    "--retry-delay", _settings.RetryDelaySeconds.ToString(CultureInfo.InvariantCulture),
                    "--timeout", _settings.TimeoutSeconds.ToString(CultureInfo.InvariantCulture),
                };
                var result = await RunProcessAsync(ResolvePythonPath(), args);

                if (result.ExitCode != 0)
                {
                    MessageBox.Show(
                        "Export failed.\n\n" + result.StdErr + result.StdOut,
                        "Export Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                MessageBox.Show("Export to PLC completed.", "Export Complete",
                    MessageBoxButton.OK, MessageBoxImage.Information);

                _settings.LastZipDir = Path.GetDirectoryName(_zipPath) ?? string.Empty;
                SaveSettings();

                if (EditToggle.IsChecked == true)
                {
                    _suppressEditToggle = true;
                    EditToggle.IsChecked = false;
                    _suppressEditToggle = false;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to run the PLC export.\n\n" + ex.Message,
                    "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private bool SaveZipInPlace(bool showMessages)
        {
            if (_zipJsonCache.Count == 0)
            {
                if (showMessages)
                {
                    MessageBox.Show("Load a ZIP with JSON files first.", "No Data",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                return false;
            }

            UpdateJsonCache();

            if (string.IsNullOrWhiteSpace(_zipPath))
            {
                if (showMessages)
                {
                    MessageBox.Show("No ZIP path is set. Open a ZIP first.", "No ZIP Path",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return false;
            }

            try
            {
                using var archive = ZipFile.Open(_zipPath, ZipArchiveMode.Update);
                foreach (var kvp in _zipJsonCache)
                {
                    var existing = archive.GetEntry(kvp.Key);
                    existing?.Delete();
                    var entry = archive.CreateEntry(kvp.Key);
                    using var entryStream = entry.Open();
                    using var writer = new StreamWriter(entryStream);
                    writer.Write(kvp.Value);
                }

                _zipJsonOriginal.Clear();
                foreach (var kvp in _zipJsonCache)
                {
                    _zipJsonOriginal[kvp.Key] = kvp.Value;
                }
                _hasPendingEdits = false;

                if (showMessages)
                {
                    MessageBox.Show("ZIP updated successfully.", "Save Complete",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                return true;
            }
            catch (Exception ex)
            {
                if (showMessages)
                {
                    MessageBox.Show("Failed to update ZIP:\n" + ex.Message, "Save Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                return false;
            }
        }

        // Arguments are passed as a discrete list rather than one concatenated string so
        // that a value typed into a dialog cannot split itself into extra arguments.
        private async Task<ProcessResult> RunProcessAsync(string fileName, IReadOnlyList<string> arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
            {
                psi.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = psi };
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            var timeout = ResolveProcessTimeout();
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                var partialOut = await ReadOrEmpty(stdoutTask);
                var partialErr = await ReadOrEmpty(stderrTask);
                if (_settings.EnableDebugLog)
                {
                    WriteDebugLog(
                        $"TIMEOUT after {timeout.TotalSeconds:N0}s: {fileName} " +
                        $"{string.Join(" ", arguments)}\n{partialOut}\n{partialErr}\n");
                }
                throw new TimeoutException(
                    $"The helper script did not finish within {timeout.TotalSeconds:N0} seconds and was stopped." +
                    (string.IsNullOrWhiteSpace(partialErr) ? string.Empty : "\n\n" + partialErr));
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (_settings.EnableDebugLog)
            {
                WriteDebugLog($"Command: {fileName} {string.Join(" ", arguments)}\n{stdout}\n{stderr}\n");
            }
            return new ProcessResult(process.ExitCode, stdout, stderr);
        }

        // A backstop for a wedged helper, not a performance budget: scaled off the retry
        // settings so raising them doesn't strand this value, with a floor generous enough
        // that a slow-but-working transfer is never killed.
        private TimeSpan ResolveProcessTimeout()
        {
            var attempts = Math.Max(1, _settings.RetryCount);
            var perAttempt = Math.Max(0, _settings.TimeoutSeconds) + Math.Max(0, _settings.RetryDelaySeconds);
            var scaled = TimeSpan.FromSeconds(attempts * perAttempt * 3);
            return scaled > MinProcessTimeout ? scaled : MinProcessTimeout;
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Already exited, or we lost the race with it exiting. Nothing to do.
            }
        }

        private static async Task<string> ReadOrEmpty(Task<string> readTask)
        {
            try
            {
                return await readTask;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ResolvePythonPath()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var bundled = Path.Combine(baseDir, "python", "python.exe");
            if (File.Exists(bundled))
            {
                return bundled;
            }

            return "python";
        }

        private void WriteDebugLog(string text)
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var logPath = _settings.LogPath;
                if (string.IsNullOrWhiteSpace(logPath))
                {
                    logPath = "SequenceNavigator.log";
                }
                if (!Path.IsPathRooted(logPath))
                {
                    logPath = Path.Combine(baseDir, logPath);
                }
                File.AppendAllText(logPath, text);
            }
            catch (Exception)
            {
                // Deliberately swallowed, and the one place that is right: this IS the
                // logger, so there is nowhere to report to, and a failed debug write must
                // never break the PLC transfer that triggered it.
            }
        }

        private void LoadSettings()
        {
            _settings = AppSettings.Load();
            if (AppSettings.LastError != null)
            {
                // Running on defaults silently looks like the app forgot the user's setup.
                WarnOnce($"Saved settings could not be loaded, so defaults are in use.\n\n{AppSettings.LastError}",
                    "Settings Not Loaded");
            }
            ApplySettings();
        }

        private void SaveSettings()
        {
            if (!AppSettings.Save(_settings))
            {
                WarnOnce($"Settings could not be saved, so preferences will not persist.\n\n{AppSettings.LastError}",
                    "Settings Not Saved");
            }
        }

        // SaveSettings runs on nearly every action, so a broken settings folder would
        // otherwise produce a dialog per click. Warn once and stay quiet after that.
        private void WarnOnce(string message, string caption)
        {
            if (!_settingsWarningShown)
            {
                _settingsWarningShown = true;
                Dispatcher.BeginInvoke(new Action(() =>
                    MessageBox.Show(message, caption, MessageBoxButton.OK, MessageBoxImage.Warning)));
            }
        }

        private void ApplySettings()
        {
            // The PLC connection is read straight from _settings wherever it is needed,
            // so there is no separate copy here to fall out of step with the saved file.
            ShowDescriptions = _settings.ShowDescriptions;
            HighlightActive = _settings.HighlightActive;
        }

        private void CompareSequences_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new CompareSequencesDialog(_settings.LastZipDir, _zipPath)
            {
                Owner = this
            };

            if (dialog.ShowDialog() != true ||
                string.IsNullOrWhiteSpace(dialog.FirstZipPath) ||
                string.IsNullOrWhiteSpace(dialog.SecondZipPath))
            {
                return;
            }

            _settings.LastZipDir = Path.GetDirectoryName(dialog.SecondZipPath) ?? string.Empty;
            SaveSettings();

            try
            {
                var reportPath = BuildComparisonReport(dialog.FirstZipPath, dialog.SecondZipPath);
                Process.Start(new ProcessStartInfo
                {
                    FileName = reportPath,
                    UseShellExecute = true
                });
                MessageBox.Show("Comparison report saved:\n" + reportPath,
                    "Compare Sequences", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to compare ZIP files.\n\n" + ex.Message,
                    "Compare Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Not static: the readable diff labels come from the L5X descriptions this window
        // already loaded, so the report can speak the same language as the card grid.
        private string BuildComparisonReport(string firstZip, string secondZip)
        {
            var firstMap = LoadZipJson(firstZip);
            var secondMap = LoadZipJson(secondZip);
            var allNames = new SortedSet<string>(firstMap.Keys, StringComparer.OrdinalIgnoreCase);
            allNames.UnionWith(secondMap.Keys);

            // The per-tag sections are built first so the header can lead with a count.
            var body = new StringBuilder();
            var flagged = new List<string>();
            var skipped = new List<string>();

            foreach (var name in allNames)
            {
                var tag = Path.GetFileNameWithoutExtension(name);

                // Utility tags are hidden in the picker, so comparing them here would be
                // noise. Recorded by name below rather than dropped silently: a report
                // that says "no differences" must not be hiding one.
                if (HiddenTags.Contains(tag))
                {
                    skipped.Add(tag);
                    continue;
                }

                bool hasA = firstMap.TryGetValue(name, out var jsonA);
                bool hasB = secondMap.TryGetValue(name, out var jsonB);

                if (!hasA || !hasB)
                {
                    flagged.Add(tag);
                    body.AppendLine("============================================================");
                    body.AppendLine(CultureInfo.InvariantCulture, $"Sequence: {tag}");
                    body.AppendLine(hasA ? "Missing in Zip B." : "Missing in Zip A.");
                    body.AppendLine();
                    continue;
                }

                JsonNode? nodeA = jsonA == null ? null : JsonNode.Parse(jsonA);
                JsonNode? nodeB = jsonB == null ? null : JsonNode.Parse(jsonB);
                var diffs = new List<(string Path, string Detail)>();
                CompareJsonNodes(nodeA, nodeB, "$", diffs);

                if (diffs.Count == 0)
                {
                    continue;
                }

                flagged.Add(tag);
                body.AppendLine("============================================================");
                body.AppendLine(CultureInfo.InvariantCulture, $"Sequence: {tag}");
                body.AppendLine(CultureInfo.InvariantCulture, $"{diffs.Count} difference(s):");
                foreach (var (path, detail) in diffs)
                {
                    var note = IsUnreachableStep(path)
                        ? "   [step 0 - not viewable in the UI; inspect the JSON directly]"
                        : string.Empty;
                    body.AppendLine(CultureInfo.InvariantCulture,
                        $"  - {DescribeDiffPath(path)} : {detail}{note}");
                }
                body.AppendLine();
            }

            // Formatted with InvariantCulture throughout: reports get compared between
            // machines, so dates and sizes must not shift with the local locale.
            var sb = new StringBuilder();
            sb.AppendLine("Sequence Comparison Report");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            AppendZipInfo(sb, "Zip A", firstZip);
            AppendZipInfo(sb, "Zip B", secondZip);
            sb.AppendLine();

            int compared = allNames.Count - skipped.Count;
            sb.AppendLine(flagged.Count == 0
                ? $"No differences found across {compared} sequence(s)."
                : $"{flagged.Count} of {compared} sequence(s) differ: {string.Join(", ", flagged)}");
            if (skipped.Count > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"Not compared ({skipped.Count} utility tag(s)): {string.Join(", ", skipped)}");
            }

            sb.AppendLine("============================================================");
            sb.AppendLine();
            sb.Append(body);

            var outputDir = Path.GetDirectoryName(firstZip) ?? AppDomain.CurrentDomain.BaseDirectory;
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var reportPath = Path.Combine(outputDir, $"seq_compare_{stamp}.txt");
            File.WriteAllText(reportPath, sb.ToString());
            return reportPath;
        }

        /// <summary>
        /// Step 0 is excluded from the UI, so a difference there is real but cannot be
        /// opened and inspected.
        /// </summary>
        private static bool IsUnreachableStep(string path) =>
            path.StartsWith("$.value[0]", StringComparison.Ordinal);

        /// <summary>
        /// Turns a JSONPath emitted by <see cref="CompareJsonNodes"/> into something a
        /// mechanical engineer can read, e.g. "$.value[5].C1.TM" becomes
        /// "Step 5  |  C1  |  TM (Tool Mount)". Anything that does not match the expected
        /// shape is returned unchanged rather than dropped.
        /// </summary>
        private string DescribeDiffPath(string path)
        {
            const string prefix = "$.value[";
            if (!path.StartsWith(prefix, StringComparison.Ordinal))
            {
                return path;
            }

            int close = path.IndexOf(']', prefix.Length);
            if (close < 0 ||
                !int.TryParse(path.AsSpan(prefix.Length, close - prefix.Length),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out int step))
            {
                return path;
            }

            var rest = path[(close + 1)..].TrimStart('.');
            if (rest.Length == 0)
            {
                return $"Step {step}";
            }

            // "C1.TM" is a group member; a bare "MTFR" is one of the SEQ setpoints.
            var parts = rest.Split('.');
            string group = parts.Length >= 2 ? parts[0] : "SEQ";
            string member = parts.Length >= 2 ? parts[1] : parts[0];

            var description = LookupDescription(group, member);
            var label = string.IsNullOrWhiteSpace(description) ? member : $"{member} ({description})";
            var groupLabel = string.Equals(group, "SEQ", StringComparison.Ordinal) ? "Setpoints" : group;

            return $"Step {step}  |  {groupLabel}  |  {label}";
        }

        private static void AppendZipInfo(StringBuilder sb, string label, string zipPath)
        {
            var info = new FileInfo(zipPath);
            sb.AppendLine(CultureInfo.InvariantCulture, $"{label}: {zipPath}");
            if (info.Exists)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Size: {info.Length:N0} bytes");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Modified: {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
            }
        }

        private static Dictionary<string, string> LoadZipJson(string zipPath)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                if (!entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var stream = entry.Open();
                using var reader = new StreamReader(stream);
                map[entry.FullName] = reader.ReadToEnd();
            }
            return map;
        }

        private static void CompareJsonNodes(JsonNode? a, JsonNode? b, string path,
            List<(string Path, string Detail)> diffs)
        {
            if (a is null && b is null)
            {
                return;
            }
            if (a is null)
            {
                diffs.Add((path, "missing in Zip A"));
                return;
            }
            if (b is null)
            {
                diffs.Add((path, "missing in Zip B"));
                return;
            }

            if (a is JsonValue && b is JsonValue)
            {
                var aText = a.ToJsonString();
                var bText = b.ToJsonString();
                if (!string.Equals(aText, bText, StringComparison.Ordinal))
                {
                    diffs.Add((path, $"{aText} != {bText}"));
                }
                return;
            }

            if (a is JsonObject objA && b is JsonObject objB)
            {
                var keys = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in objA)
                {
                    keys.Add(prop.Key);
                }
                foreach (var prop in objB)
                {
                    keys.Add(prop.Key);
                }
                foreach (var key in keys)
                {
                    objA.TryGetPropertyValue(key, out var valA);
                    objB.TryGetPropertyValue(key, out var valB);
                    CompareJsonNodes(valA, valB, $"{path}.{key}", diffs);
                }
                return;
            }

            if (a is JsonArray arrA && b is JsonArray arrB)
            {
                if (arrA.Count != arrB.Count)
                {
                    diffs.Add((path, $"array length {arrA.Count} != {arrB.Count}"));
                }
                int count = Math.Min(arrA.Count, arrB.Count);
                for (int i = 0; i < count; i++)
                {
                    CompareJsonNodes(arrA[i], arrB[i], $"{path}[{i}]", diffs);
                }
                return;
            }

            diffs.Add((path, $"type mismatch ({a.GetType().Name} vs {b.GetType().Name})"));
        }

        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr);

    public sealed class FieldItem : INotifyPropertyChanged
    {
        private string _value;
        private string _lastGoodValue;
        private readonly Func<string, bool>? _valueSetter;

        public FieldItem(
            string name,
            string value,
            bool isBoolean = false,
            bool isNumber = false,
            Func<string, bool>? valueSetter = null,
            string? description = null)
        {
            Name = name;
            _value = value;
            _lastGoodValue = value;
            IsBoolean = isBoolean;
            IsNumber = isNumber;
            _valueSetter = valueSetter;
            Description = description ?? string.Empty;
        }

        public string Name { get; }
        public string Description { get; }
        public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
        public bool IsBoolean { get; }
        public bool IsNumber { get; }

        public string Value
        {
            get => _value;
            set
            {
                if (_value == value)
                {
                    return;
                }
                _value = value;

                if (_valueSetter != null && !_valueSetter(value))
                {
                    // The edit was rejected, so the backing JSON still holds the old value.
                    // Snap the display back to match it instead of leaving the two out of
                    // sync. Deferred so WPF finishes the current binding update first —
                    // a synchronous notification here can leave the stale text on screen.
                    Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
                    {
                        _value = _lastGoodValue;
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
                    }));
                    return;
                }

                _lastGoodValue = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}

