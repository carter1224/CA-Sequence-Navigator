using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;

namespace SequenceNavigator
{
    /// <summary>
    /// The main window. Split across partial files by concern: this one owns the open
    /// backup, the cards and edits; Transfer handles the PLC; Navigation handles steps,
    /// the keyboard, search and the start screen; Compare handles comparisons.
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private const int SeqLength = 100;

        // SEQ[0] is deliberately not navigable or editable. The element still exists in
        // the JSON and is written back to the PLC untouched — the array must stay 100
        // long — it simply is not reachable from the UI.
        private const int FirstStep = 1;
        private const int LastStep = SeqLength - 1;

        private int _currentIndex = FirstStep;
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

        // Unsaved edits, one per field, so the app can say "3 unsaved changes" and list
        // them. Editing a field back to its original value removes it.
        private readonly Dictionary<string, FieldChange> _changes = new(StringComparer.Ordinal);

        // Edits saved since this backup was opened. A download confirmation lists them,
        // because saving clears _changes but the PLC has not seen them yet.
        private readonly List<FieldChange> _savedChanges = new();

        private string? _zipPath;
        private string? _currentJsonName;
        private JsonNode? _currentJsonRoot;
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
            RecentList.ItemsSource = _recentItems;
            VersionText.Text = "Sequence Navigator " + AppVersion.Number;
            LoadL5xDescriptions();
            LoadSettings();
            ApplyWindowPlacement();
            RefreshRecentList();
            UpdateFileHeader();
            RefreshEditState();
            RefreshControlStates();
            SetStatus("Ready");

            Loaded += (_, _) =>
            {
                if (App.StartupFile != null)
                {
                    LoadZip(App.StartupFile);
                }
            };
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
                RefreshEditState();
            }
        }

        private bool HasPendingEdits => _changes.Count > 0;

        private bool HasSeqData =>
            _currentJsonRoot?["value"] is JsonArray arr && arr.Count == SeqLength;

        private string OpenFileName => _zipPath == null ? "this backup" : Path.GetFileName(_zipPath);

        protected override void OnClosing(CancelEventArgs e)
        {
            // A helper is mid-transfer; closing now would orphan it while it is still
            // talking to the controller.
            if (_isBusy)
            {
                ShowInfo(InfoSeverity.Warning, "A PLC transfer is still running.", "Wait for it to finish before closing.");
                e.Cancel = true;
                return;
            }

            if (HasPendingEdits)
            {
                var choice = ChoiceDialog.Ask(this, "Unsaved changes",
                    "Save your changes before closing?",
                    $"{ChangeCountText()} in {OpenFileName}.",
                    "Save and close", "Close without saving", "Cancel",
                    ChangeLines(), ChoiceTone.Warning);

                if (choice == ChoiceResult.Close)
                {
                    e.Cancel = true;
                    return;
                }

                // Closing anyway after a failed save would discard the very edits the user
                // just asked to keep.
                if (choice == ChoiceResult.Primary && !SaveZipInPlace())
                {
                    e.Cancel = true;
                    return;
                }
            }

            RememberCurrentPosition();
            SaveWindowPlacement();
            SaveSettings();
            _findWindow?.Close();
            base.OnClosing(e);
        }

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

            // Need a backup open.
            FindBtn.IsEnabled = idle && hasZip;
            JsonPicker.IsEnabled = idle && hasZip;
            EditToggle.IsEnabled = idle && hasZip;
            ExportBtn.IsEnabled = idle && hasZip && HasPendingEdits;
            ExportPlcBtn.IsEnabled = idle && hasZip;

            // Need a valid 100-element sequence to navigate.
            PrevBtn.IsEnabled = idle && hasSeq && _currentIndex > FirstStep;
            NextBtn.IsEnabled = idle && hasSeq && _currentIndex < LastStep;
            IndexBox.IsEnabled = idle && hasSeq;
            FirstStepBtn.IsEnabled = idle && hasSeq && _currentIndex > FirstStep;
            LastStepBtn.IsEnabled = idle && hasSeq && SequenceEnd() is int end && _currentIndex != end;

            IndexLabel.Text = hasSeq ? $"Step {_currentIndex}" : "Step –";
            if (!IndexBox.IsFocused)
            {
                IndexBox.Text = hasSeq ? _currentIndex.ToString(CultureInfo.InvariantCulture) : string.Empty;
            }
            UpdateStepUsage();

            // Start screen when nothing is open; never over a running transfer's spinner.
            bool showStart = !hasZip && idle;
            StartScreen.Visibility = showStart ? Visibility.Visible : Visibility.Collapsed;
            TabBandButtons.Visibility = showStart ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>
        /// Asks what to do with unsaved edits before something replaces the open backup.
        /// Returns false if the user cancelled (or a save they asked for failed).
        /// </summary>
        private bool ResolvePendingEdits(string action, string primary, string secondary)
        {
            if (!HasPendingEdits)
            {
                return true;
            }

            var choice = ChoiceDialog.Ask(this, "Unsaved changes",
                $"Save your changes before {action}?",
                $"{ChangeCountText()} in {OpenFileName}.",
                primary, secondary, "Cancel", ChangeLines(), ChoiceTone.Warning);

            return choice switch
            {
                ChoiceResult.Primary => SaveZipInPlace(),
                ChoiceResult.Secondary => true,
                _ => false,
            };
        }

        private void OpenZip_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy || !ResolvePendingEdits("opening another backup", "Save and open", "Open without saving"))
            {
                return;
            }

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Open a backup",
                Filter = "Backup ZIP (*.zip)|*.zip|All Files (*.*)|*.*",
                InitialDirectory = UiHelpers.ResolveInitialDirectory(_settings.LastZipDir)
            };

            if (dialog.ShowDialog(this) == true)
            {
                _settings.LastZipDir = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
                LoadZip(dialog.FileName);
            }
        }

        /// <summary>Opens a backup from the start screen, a drop or a comparison.</summary>
        private bool OpenBackupInteractive(string path)
        {
            if (_isBusy)
            {
                return false;
            }
            if (string.Equals(path, _zipPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (!ResolvePendingEdits("opening another backup", "Save and open", "Open without saving"))
            {
                return false;
            }
            return LoadZip(path);
        }

        private void AdvancedSettings_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SettingsDialog(_settings, (show, highlight) =>
            {
                ShowDescriptions = show;
                HighlightActive = highlight;
            })
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true)
            {
                _settings = dialog.Settings;
                SaveSettings();
                ApplySettings();
                SetStatus("Settings saved");
            }
        }

        private bool LoadZip(string path)
        {
            RememberCurrentPosition();
            try
            {
                // Read into locals first, so a file that fails to open leaves whatever was
                // already open exactly as it was, rather than half of each.
                var entries = new List<(string Name, string Json)>();
                using (var archive = ZipFile.OpenRead(path))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (!entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        using var stream = entry.Open();
                        using var reader = new StreamReader(stream);
                        entries.Add((entry.FullName, reader.ReadToEnd()));
                    }
                }

                if (entries.Count == 0)
                {
                    ShowInfo(InfoSeverity.Warning, $"No sequences in {Path.GetFileName(path)}.",
                        "This ZIP has no .json sequence files. Is it a Sequence Navigator backup?");
                    return false;
                }

                ClearLoadedZip();
                _zipPath = path;
                foreach (var (name, json) in entries)
                {
                    // Cached regardless, so a save rewrites it byte-for-byte even when the
                    // tag is not selectable.
                    _zipJsonCache[name] = json;
                    _zipJsonOriginal[name] = json;

                    var display = Path.GetFileNameWithoutExtension(name);
                    if (SequenceComparer.HiddenTags.Contains(display))
                    {
                        continue;
                    }
                    JsonPicker.Items.Add(new ComboBoxItem { Content = display, Tag = name });
                }

                _settings.AddRecentFile(path);
                _settings.LastZipDir = Path.GetDirectoryName(path) ?? _settings.LastZipDir;
                SaveSettings();
                RefreshRecentList();
                Info.Hide();
                UpdateFileHeader();
                StartScan();
                RestorePosition(path);
                SetStatus($"Opened {Path.GetFileName(path)} · {JsonPicker.Items.Count} sequences");
                return true;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                ShowInfo(InfoSeverity.Error, $"Couldn't open {Path.GetFileName(path)}.", ex.Message);
                if (!File.Exists(path))
                {
                    _settings.RemoveRecentFile(path);
                    RefreshRecentList();
                }
                return false;
            }
            finally
            {
                RefreshControlStates();
            }
        }

        /// <summary>
        /// Drops whatever ZIP is open so the window shows nothing loaded, including the
        /// file name in the header.
        /// </summary>
        private void ClearLoadedZip()
        {
            _zipPath = null;
            _zipJsonCache.Clear();
            _zipJsonOriginal.Clear();
            // A scan still running for the old backup must not land on the new one.
            _scanGeneration++;
            _scanTask = null;
            _scans.Clear();
            _changes.Clear();
            _savedChanges.Clear();
            JsonPicker.ItemsSource = null;
            JsonPicker.Items.Clear();
            ClearSeqContent();
            _findWindow?.Invalidate();
            UpdateFileHeader();
            RefreshEditState();
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

            // Keep the step when switching sequences: comparing P1 to P6 at step 12 should
            // not mean typing 12 six times.
            LoadJsonContent(name, json, _currentIndex);
        }

        private void LoadJsonContent(string name, string json, int step)
        {
            // Validate into locals first. Committing to _currentJsonName/_currentJsonRoot
            // before the checks pass would leave the grids rendering the previously loaded
            // file while pointing every edit at a document we already rejected.
            var display = Path.GetFileNameWithoutExtension(name);
            try
            {
                var root = JsonNode.Parse(json) ?? throw new JsonException("The file is empty.");
                if (root["value"] is not JsonArray values || values.Count != SeqLength)
                {
                    ClearSeqContent();
                    ShowInfo(InfoSeverity.Error, $"{display} isn't a SEQ[100] sequence.",
                        $"Expected a 'value' list of {SeqLength} steps.");
                    return;
                }

                _currentJsonName = name;
                _currentJsonRoot = root;
                _currentIndex = Math.Clamp(step, FirstStep, LastStep);
                RenderCurrent();
                RefreshControlStates();
            }
            catch (JsonException ex)
            {
                ClearSeqContent();
                ShowInfo(InfoSeverity.Error, $"{display} couldn't be read.", ex.Message);
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

        private void AddFieldItem(ObservableCollection<FieldItem> target, JsonObject parent, string key, string groupName)
        {
            JsonNode? node = parent[key];
            string? description = LookupDescription(groupName, key);
            if (node is not JsonValue valueNode)
            {
                target.Add(new FieldItem(key, node?.ToString() ?? string.Empty, description: description));
                return;
            }

            var field = new FieldRef(_currentJsonName ?? string.Empty, _currentIndex, groupName, key, description);

            if (valueNode.TryGetValue<bool>(out bool boolValue))
            {
                target.Add(new FieldItem(
                    key,
                    boolValue ? "true" : "false",
                    isBoolean: true,
                    isNumber: false,
                    valueSetter: v => SetBooleanValue(parent, field, v),
                    description: description));
                return;
            }

            if (valueNode.TryGetValue<int>(out int intValue))
            {
                target.Add(new FieldItem(
                    key,
                    intValue.ToString(CultureInfo.InvariantCulture),
                    isBoolean: false,
                    isNumber: true,
                    valueSetter: v => SetNumberValue(parent, field, v, isInteger: true),
                    description: description));
                return;
            }

            if (valueNode.TryGetValue<double>(out double dblValue))
            {
                // REALs are single precision in the controller. Shown as the double the
                // helper wrote, 0.3 reads as 0.30000001192092896; as a float it is 0.3,
                // and that is also exactly what the PLC stores.
                target.Add(new FieldItem(
                    key,
                    ((float)dblValue).ToString(CultureInfo.InvariantCulture),
                    isBoolean: false,
                    isNumber: true,
                    valueSetter: v => SetNumberValue(parent, field, v, isInteger: false),
                    description: description));
                return;
            }

            target.Add(new FieldItem(key, node?.ToString() ?? string.Empty, description: description));
        }

        private bool SetBooleanValue(JsonObject parent, FieldRef field, string rawValue)
        {
            if (!bool.TryParse(rawValue, out bool value))
            {
                WarnRejectedValue(field, rawValue, "true or false");
                return false;
            }

            var before = SequenceComparer.Display(parent[field.Member]!);
            parent[field.Member] = value;
            RecordChange(field, before, value ? "true" : "false");
            return true;
        }

        private bool SetNumberValue(JsonObject parent, FieldRef field, string rawValue, bool isInteger)
        {
            var before = SequenceComparer.Display(parent[field.Member]!);
            string after;
            if (isInteger)
            {
                if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intValue))
                {
                    WarnRejectedValue(field, rawValue, "a whole number");
                    return false;
                }
                parent[field.Member] = intValue;
                after = intValue.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                if (!double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double dblValue))
                {
                    WarnRejectedValue(field, rawValue, "a number");
                    return false;
                }
                parent[field.Member] = dblValue;
                after = ((float)dblValue).ToString(CultureInfo.InvariantCulture);
            }

            RecordChange(field, before, after);
            return true;
        }

        /// <summary>
        /// Tracks one field's edit against its value when the backup was opened (or last
        /// saved). Changing it back removes the change entirely.
        /// </summary>
        private void RecordChange(FieldRef field, string before, string after)
        {
            var key = field.Key;
            if (_changes.TryGetValue(key, out var existing))
            {
                before = existing.Original;
            }

            if (before == after)
            {
                _changes.Remove(key);
            }
            else
            {
                _changes[key] = new FieldChange(field, before, after);
            }

            UpdateJsonCache();
            RescanCurrent();
            RefreshEditState();
            RefreshControlStates();
        }

        // Deferred rather than shown inline: this runs from inside a binding update, and
        // UI changes there fight with the focus change that triggered it.
        private void WarnRejectedValue(FieldRef field, string rawValue, string expected)
        {
            Dispatcher.BeginInvoke(new Action(() =>
                ShowInfo(InfoSeverity.Warning, $"'{rawValue}' isn't {expected}.",
                    $"{field.Label} was left unchanged.")));
        }

        private string ChangeCountText() =>
            _changes.Count == 1 ? "1 unsaved change" : $"{_changes.Count} unsaved changes";

        private IEnumerable<string> ChangeLines() =>
            _changes.Values.OrderBy(c => c.Field.Sequence).ThenBy(c => c.Field.Step).Select(c => c.Line);

        /// <summary>Banner, status bar and button states that follow the edit state.</summary>
        private void RefreshEditState()
        {
            EditBanner.Visibility = _isEditMode ? Visibility.Visible : Visibility.Collapsed;
            EditBannerText.Text = HasPendingEdits ? "Editing · " + ChangeCountText() : "Editing · no changes yet";
            EditStateText.Text = HasPendingEdits ? ChangeCountText() : string.Empty;
            Title = HasPendingEdits ? "Sequence Navigator *" : "Sequence Navigator";
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
                            int idx = description.IndexOf(')', StringComparison.Ordinal);
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
            catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
            {
                // The file is present but unreadable. Dropping every description without
                // a word looks like the feature is broken, so say so.
                Dispatcher.BeginInvoke(new Action(() =>
                    ShowInfo(InfoSeverity.Warning, "Field descriptions are unavailable.",
                        "SEQ_DataType.L5X could not be read, so fields show tag names only.", ex.Message)));
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
            if (_suppressEditToggle || !HasPendingEdits)
            {
                IsEditMode = false;
                return;
            }

            var choice = ChoiceDialog.Ask(this, "Discard changes",
                "Discard your changes?",
                $"{ChangeCountText()} will be lost. The backup on disk is not affected.",
                "Discard changes", null, "Keep editing", ChangeLines(), ChoiceTone.Danger, safeIsDefault: true);

            if (choice != ChoiceResult.Primary)
            {
                _suppressEditToggle = true;
                EditToggle.IsChecked = true;
                _suppressEditToggle = false;
                return;
            }

            IsEditMode = false;
            _zipJsonCache.Clear();
            foreach (var kvp in _zipJsonOriginal)
            {
                _zipJsonCache[kvp.Key] = kvp.Value;
            }
            _changes.Clear();
            RefreshEditState();
            StartScan();

            if (_currentJsonName != null && _zipJsonCache.TryGetValue(_currentJsonName, out var json))
            {
                LoadJsonContent(_currentJsonName, json, _currentIndex);
            }
            SetStatus("Changes discarded");
        }

        private void ExitEditMode()
        {
            if (EditToggle.IsChecked == true)
            {
                _suppressEditToggle = true;
                EditToggle.IsChecked = false;
                _suppressEditToggle = false;
            }
            IsEditMode = false;
        }

        private void ExportZip_Click(object sender, RoutedEventArgs e)
        {
            if (SaveZipInPlace())
            {
                ExitEditMode();
            }
        }

        private bool SaveZipInPlace()
        {
            if (_zipJsonCache.Count == 0 || string.IsNullOrWhiteSpace(_zipPath))
            {
                return false;
            }

            UpdateJsonCache();
            try
            {
                using (var archive = ZipFile.Open(_zipPath, ZipArchiveMode.Update))
                {
                    foreach (var kvp in _zipJsonCache)
                    {
                        var existing = archive.GetEntry(kvp.Key);
                        existing?.Delete();
                        var entry = archive.CreateEntry(kvp.Key);
                        using var entryStream = entry.Open();
                        using var writer = new StreamWriter(entryStream);
                        writer.Write(kvp.Value);
                    }
                }

                _zipJsonOriginal.Clear();
                foreach (var kvp in _zipJsonCache)
                {
                    _zipJsonOriginal[kvp.Key] = kvp.Value;
                }
                int saved = _changes.Count;
                _savedChanges.AddRange(_changes.Values);
                _changes.Clear();
                RefreshEditState();
                RefreshControlStates();
                UpdateFileHeader();
                SetStatus(string.Create(CultureInfo.InvariantCulture,
                    $"Saved {saved} change{(saved == 1 ? "" : "s")} to {OpenFileName} at {DateTime.Now:HH:mm}"));
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                ShowInfo(InfoSeverity.Error, $"Couldn't save {OpenFileName}.",
                    "Your changes are still here. Check the file isn't open elsewhere or read-only, then save again.", ex.Message);
                return false;
            }
        }

        private void LoadSettings()
        {
            _settings = AppSettings.Load();
            if (AppSettings.LastError != null)
            {
                // Running on defaults silently looks like the app forgot the user's setup.
                WarnOnce("Saved settings couldn't be loaded, so defaults are in use.", AppSettings.LastError);
            }
            ApplySettings();
        }

        private void SaveSettings()
        {
            if (!AppSettings.Save(_settings))
            {
                WarnOnce("Settings couldn't be saved, so preferences won't persist.", AppSettings.LastError);
            }
        }

        // SaveSettings runs on many actions, so a broken settings folder would otherwise
        // produce a message per click. Warn once and stay quiet after that.
        private void WarnOnce(string title, string? detail)
        {
            if (!_settingsWarningShown)
            {
                _settingsWarningShown = true;
                Dispatcher.BeginInvoke(new Action(() => ShowInfo(InfoSeverity.Warning, title, string.Empty, detail)));
            }
        }

        private void ApplySettings()
        {
            // The PLC connection is read straight from _settings wherever it is needed,
            // so there is no separate copy here to fall out of step with the saved file.
            ShowDescriptions = _settings.ShowDescriptions;
            HighlightActive = _settings.HighlightActive;
        }

        private void ShowInfo(InfoSeverity severity, string title, string message,
            string? details = null, string? actionText = null, Action? action = null) =>
            Info.Show(severity, title, message, details, actionText, action);

        private void SetStatus(string text) => StatusText.Text = text;

        /// <summary>The header's file name and the line under it saying what it is.</summary>
        private void UpdateFileHeader()
        {
            if (_zipPath == null)
            {
                PathBox.Text = "No backup open";
                PathBox.ToolTip = null;
                FileDetailText.Text = "Open a backup or upload one from a PLC";
                return;
            }

            PathBox.Text = Path.GetFileName(_zipPath);
            PathBox.ToolTip = _zipPath;
            var info = new FileInfo(_zipPath);
            var parts = new List<string>
            {
                string.Create(CultureInfo.InvariantCulture, $"{JsonPicker.Items.Count} sequences"),
            };
            if (info.Exists)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"saved {info.LastWriteTime:yyyy-MM-dd HH:mm}"));
            }
            parts.Add(Path.GetDirectoryName(_zipPath) ?? string.Empty);
            FileDetailText.Text = string.Join(" · ", parts);
        }

        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr);

    /// <summary>Where a field lives: sequence entry, step, group and member.</summary>
    public sealed record FieldRef(string Entry, int Step, string Group, string Member, string? Description)
    {
        public string Sequence => Path.GetFileNameWithoutExtension(Entry);
        public string Key => $"{Entry}|{Step}|{Group}|{Member}";
        public string Tab => Group == "SEQ" ? "Setpoints" : Group;
        public string Label => string.IsNullOrWhiteSpace(Description) ? Member : $"{Description} ({Member})";
    }

    /// <summary>One edited field: what it was when opened (or last saved), and what it is now.</summary>
    public sealed record FieldChange(FieldRef Field, string Original, string Current)
    {
        public string Line => $"{Field.Sequence} · Step {Field.Step} · {Field.Tab} · {Field.Label}: {Original} → {Current}";
    }

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
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
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
