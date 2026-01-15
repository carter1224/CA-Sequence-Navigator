using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace SequenceNavigator
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private readonly List<JsonElement> _seqValues = new();
        private int _currentIndex;
        private readonly ObservableCollection<FieldItem> _c1Items = new();
        private readonly ObservableCollection<FieldItem> _c2Items = new();
        private readonly ObservableCollection<FieldItem> _c3Items = new();
        private readonly ObservableCollection<FieldItem> _v1Items = new();
        private readonly ObservableCollection<FieldItem> _v2Items = new();
        private readonly ObservableCollection<FieldItem> _v3Items = new();
        private readonly ObservableCollection<FieldItem> _scalarItems = new();
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
        private string? _lastIp;
        private int? _lastEthSlot;
        private int? _lastCpuSlot;
        private AppSettings _settings = new();
        private bool _showDescriptions = true;
        private bool _highlightActive = true;

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
            ScalarGrid.ItemsSource = _scalarItems;
            LoadL5xDescriptions();
            LoadSettings();
            UpdateNavState();
            UpdateExportState();
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

        private bool HasSeqData =>
            _currentJsonRoot?["value"] is JsonArray arr && arr.Count == 100;

        private void SetBusy(bool isBusy)
        {
            _isBusy = isBusy;
            ImportBtn.IsEnabled = !_isBusy;
            OpenZipBtn.IsEnabled = !_isBusy;
            AdvancedBtn.IsEnabled = !_isBusy;
            CompareBtn.IsEnabled = !_isBusy;
            JsonPicker.IsEnabled = !_isBusy && _zipJsonCache.Count > 0;
            PrevBtn.IsEnabled = !_isBusy && HasSeqData && _currentIndex > 0;
            NextBtn.IsEnabled = !_isBusy && HasSeqData && _currentIndex < 99;
            IndexBox.IsEnabled = !_isBusy && HasSeqData;
            GoBtn.IsEnabled = !_isBusy && HasSeqData;
            EditToggle.IsEnabled = !_isBusy && _zipJsonCache.Count > 0;
            ExportBtn.IsEnabled = !_isBusy && _zipJsonCache.Count > 0;
            ExportPlcBtn.IsEnabled = !_isBusy && _zipJsonCache.Count > 0;
        }

        private void OpenZip_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select SEQ ZIP file",
                Filter = "ZIP Files (*.zip)|*.zip|All Files (*.*)|*.*",
                InitialDirectory = ResolveInitialDirectory(_settings.LastZipDir)
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
            var connDialog = new PlcConnectionDialog(_lastIp, _lastEthSlot, _lastCpuSlot)
            {
                Owner = this
            };

            if (connDialog.ShowDialog() != true)
            {
                return;
            }

            _lastIp = connDialog.IpAddress;
            _lastEthSlot = connDialog.EthSlot;
            _lastCpuSlot = connDialog.CpuSlot;

            var defaultName = $"seq_export_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
            var saveDialog = new SaveFileDialog
            {
                Title = "Save Export ZIP",
                Filter = "ZIP Files (*.zip)|*.zip|All Files (*.*)|*.*",
                FileName = defaultName,
                InitialDirectory = ResolveInitialDirectory(_settings.LastZipDir)
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
                string args =
                    $"\"{scriptPath}\" --ip {_lastIp} --eth-slot {_lastEthSlot} --cpu-slot {_lastCpuSlot} " +
                    $"--out-zip \"{saveDialog.FileName}\" --retries {_settings.RetryCount} " +
                    $"--retry-delay {_settings.RetryDelaySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                    $"--timeout {_settings.TimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                if (_settings.IncludeProgramTags)
                {
                    args += " --include-program-tags";
                }
                var result = await RunProcessAsync("python", args);

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
                _currentJsonName = null;
                _currentJsonRoot = null;
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
                    _zipJsonCache[entry.FullName] = jsonText;
                    _zipJsonOriginal[entry.FullName] = jsonText;
                    var display = Path.GetFileNameWithoutExtension(entry.FullName);
                    var item = new ComboBoxItem { Content = display, Tag = entry.FullName };
                    JsonPicker.Items.Add(item);
                }

                PathBox.Text = path;
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

            UpdateExportState();
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
            try
            {
                _currentJsonName = name;
                _currentJsonRoot = JsonNode.Parse(json);
                if (_currentJsonRoot is null)
                {
                    throw new InvalidOperationException("JSON root is empty.");
                }
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("value", out var valueElem) ||
                    valueElem.ValueKind != JsonValueKind.Array)
                {
                    MessageBox.Show("Expected a JSON file with a 'value' array.", "Invalid SEQ File",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                _seqValues.Clear();
                foreach (var item in valueElem.EnumerateArray())
                {
                    _seqValues.Add(item.Clone());
                }

                if (_seqValues.Count != 100)
                {
                    MessageBox.Show("Expected 'value' array length of 100.", "Invalid SEQ File",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    _seqValues.Clear();
                    return;
                }

                TagLabel.Text = "Tag: " + (doc.RootElement.TryGetProperty("source_tag_name", out var tag)
                    ? tag.GetString() : "");
                DefLabel.Text = "Definition: " + (doc.RootElement.TryGetProperty("required_definition", out var def)
                    ? def.GetString() : "");

                _currentIndex = 0;
                RenderCurrent();
                UpdateNavState();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to load JSON:\n" + ex.Message, "Load Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Prev_Click(object sender, RoutedEventArgs e)
        {
            if (_currentIndex > 0)
            {
                _currentIndex--;
                RenderCurrent();
                UpdateNavState();
            }
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            if (_currentIndex < 99)
            {
                _currentIndex++;
                RenderCurrent();
                UpdateNavState();
            }
        }

        private void UpdateNavState()
        {
            bool hasData = HasSeqData;
            PrevBtn.IsEnabled = !_isBusy && hasData && _currentIndex > 0;
            NextBtn.IsEnabled = !_isBusy && hasData && _currentIndex < 99;
            IndexBox.IsEnabled = !_isBusy && hasData;
            JsonPicker.IsEnabled = !_isBusy && _zipJsonCache.Count > 0;
            IndexLabel.Text = hasData ? $"Step {_currentIndex + 1}" : "Step 1";
            if (!IndexBox.IsFocused)
            {
                IndexBox.Text = hasData ? (_currentIndex + 1).ToString() : string.Empty;
            }
        }

        private void RenderCurrent()
        {
            _c1Items.Clear();
            _c2Items.Clear();
            _c3Items.Clear();
            _v1Items.Clear();
            _v2Items.Clear();
            _v3Items.Clear();
            _scalarItems.Clear();
            if (!HasSeqData)
            {
                return;
            }

            JsonArray arr = (JsonArray)_currentJsonRoot!["value"]!;
            JsonObject? nodeObject = arr[_currentIndex] as JsonObject;
            if (nodeObject == null)
            {
                _scalarItems.Add(new FieldItem("Value", arr[_currentIndex]?.ToString() ?? string.Empty));
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
                        AddFieldItem(_scalarItems, nodeObject, prop.Key, "SEQ");
                        break;
                }
            }
        }

        private void Go_Click(object sender, RoutedEventArgs e)
        {
            if (!HasSeqData)
            {
                return;
            }

            if (!int.TryParse(IndexBox.Text?.Trim(), out int value))
            {
                MessageBox.Show("Enter a number from 1 to 100.", "Invalid Index",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (value < 1 || value > 100)
            {
                MessageBox.Show("Index must be between 1 and 100.", "Invalid Index",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _currentIndex = value - 1;
            RenderCurrent();
            UpdateNavState();
        }

        private void IndexBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                Go_Click(sender, e);
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

        private void SetBooleanValue(JsonObject parent, string key, string rawValue)
        {
            bool value = string.Equals(rawValue, "true", StringComparison.OrdinalIgnoreCase);
            parent[key] = value;
            _hasPendingEdits = true;
            UpdateJsonCache();
        }

        private void SetNumberValue(JsonObject parent, string key, string rawValue, bool isInteger)
        {
            if (isInteger)
            {
                if (int.TryParse(rawValue, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int intValue))
                {
                    parent[key] = intValue;
                    _hasPendingEdits = true;
                    UpdateJsonCache();
                }
            }
            else
            {
                if (double.TryParse(rawValue, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double dblValue))
                {
                    parent[key] = dblValue;
                    _hasPendingEdits = true;
                    UpdateJsonCache();
                }
            }
        }

        private static string FormatValue(JsonElement value)
        {
            return value.ValueKind switch
            {
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Number => value.ToString(),
                JsonValueKind.Null => "null",
                _ => value.ToString(),
            };
        }

        private void LoadL5xDescriptions()
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var l5xPath = Path.Combine(baseDir, "SEQ_DataType.L5X");
                if (!File.Exists(l5xPath))
                {
                    return;
                }

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
            catch
            {
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

        private void UpdateExportState()
        {
            bool hasData = _zipJsonCache.Count > 0;
            ExportBtn.IsEnabled = !_isBusy && hasData;
            EditToggle.IsEnabled = !_isBusy && hasData;
            GoBtn.IsEnabled = !_isBusy && hasData;
            ImportBtn.IsEnabled = !_isBusy;
            OpenZipBtn.IsEnabled = !_isBusy;
            ExportPlcBtn.IsEnabled = !_isBusy && hasData;
            AdvancedBtn.IsEnabled = !_isBusy;
            CompareBtn.IsEnabled = !_isBusy;
        }

        private void EditToggle_Unchecked(object sender, RoutedEventArgs e)
        {
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

            var connDialog = new PlcConnectionDialog(_lastIp, _lastEthSlot, _lastCpuSlot)
            {
                Owner = this
            };

            if (connDialog.ShowDialog() != true)
            {
                return;
            }

            _lastIp = connDialog.IpAddress;
            _lastEthSlot = connDialog.EthSlot;
            _lastCpuSlot = connDialog.CpuSlot;

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
                string args =
                    $"\"{scriptPath}\" --ip {_lastIp} --eth-slot {_lastEthSlot} --cpu-slot {_lastCpuSlot} " +
                    $"--zip \"{_zipPath}\" --retries {_settings.RetryCount} " +
                    $"--retry-delay {_settings.RetryDelaySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                    $"--timeout {_settings.TimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                var result = await RunProcessAsync("python", args);

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

        private async Task<ProcessResult> RunProcessAsync(string fileName, string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (_settings.EnableDebugLog)
            {
                WriteDebugLog($"Command: {fileName} {arguments}\n{stdout}\n{stderr}\n");
            }
            return new ProcessResult(process.ExitCode, stdout, stderr);
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
            catch
            {
            }
        }

        private void LoadSettings()
        {
            _settings = AppSettings.Load();
            ApplySettings();
        }

        private void SaveSettings()
        {
            AppSettings.Save(_settings);
        }

        private void ApplySettings()
        {
            _lastIp = _settings.DefaultIp;
            _lastEthSlot = _settings.DefaultEthSlot;
            _lastCpuSlot = _settings.DefaultCpuSlot;
            ShowDescriptions = _settings.ShowDescriptions;
            HighlightActive = _settings.HighlightActive;
        }

        private void CompareSequences_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new CompareSequencesDialog(_settings.LastZipDir)
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

        private static string BuildComparisonReport(string firstZip, string secondZip)
        {
            var firstMap = LoadZipJson(firstZip);
            var secondMap = LoadZipJson(secondZip);
            var allNames = new SortedSet<string>(firstMap.Keys, StringComparer.OrdinalIgnoreCase);
            allNames.UnionWith(secondMap.Keys);

            var sb = new StringBuilder();
            sb.AppendLine("Sequence Comparison Report");
            sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            AppendZipInfo(sb, "Zip A", firstZip);
            AppendZipInfo(sb, "Zip B", secondZip);
            sb.AppendLine("============================================================");
            sb.AppendLine();

            foreach (var name in allNames)
            {
                bool hasA = firstMap.TryGetValue(name, out var jsonA);
                bool hasB = secondMap.TryGetValue(name, out var jsonB);

                if (!hasA)
                {
                    sb.AppendLine("============================================================");
                    sb.AppendLine($"JSON: {Path.GetFileName(name)}");
                    sb.AppendLine("Missing in Zip A.");
                    sb.AppendLine();
                    continue;
                }

                if (!hasB)
                {
                    sb.AppendLine("============================================================");
                    sb.AppendLine($"JSON: {Path.GetFileName(name)}");
                    sb.AppendLine("Missing in Zip B.");
                    sb.AppendLine();
                    continue;
                }

                JsonNode? nodeA = jsonA == null ? null : JsonNode.Parse(jsonA);
                JsonNode? nodeB = jsonB == null ? null : JsonNode.Parse(jsonB);
                var diffs = new List<string>();
                CompareJsonNodes(nodeA, nodeB, "$", diffs);

                if (diffs.Count > 0)
                {
                    sb.AppendLine("============================================================");
                    sb.AppendLine($"JSON: {Path.GetFileName(name)}");
                    sb.AppendLine("Differences:");
                    foreach (var diff in diffs)
                    {
                        sb.AppendLine("  - " + diff);
                    }
                    sb.AppendLine();
                }
            }

            var outputDir = Path.GetDirectoryName(firstZip) ?? AppDomain.CurrentDomain.BaseDirectory;
            var reportName = $"seq_compare_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            var reportPath = Path.Combine(outputDir, reportName);
            if (!sb.ToString().Contains("Differences:"))
            {
                sb.AppendLine("No differences found.");
            }
            File.WriteAllText(reportPath, sb.ToString());
            return reportPath;
        }

        private static void AppendZipInfo(StringBuilder sb, string label, string zipPath)
        {
            var info = new FileInfo(zipPath);
            sb.AppendLine($"{label}: {zipPath}");
            if (info.Exists)
            {
                sb.AppendLine($"  Size: {info.Length:N0} bytes");
                sb.AppendLine($"  Modified: {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
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

        private static void CompareJsonNodes(JsonNode? a, JsonNode? b, string path, List<string> diffs)
        {
            if (a is null && b is null)
            {
                return;
            }
            if (a is null)
            {
                diffs.Add($"{path}: missing in Zip A");
                return;
            }
            if (b is null)
            {
                diffs.Add($"{path}: missing in Zip B");
                return;
            }

            if (a is JsonValue && b is JsonValue)
            {
                var aText = a.ToJsonString();
                var bText = b.ToJsonString();
                if (!string.Equals(aText, bText, StringComparison.Ordinal))
                {
                    diffs.Add($"{path}: {aText} != {bText}");
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
                    diffs.Add($"{path}: array length {arrA.Count} != {arrB.Count}");
                }
                int count = Math.Min(arrA.Count, arrB.Count);
                for (int i = 0; i < count; i++)
                {
                    CompareJsonNodes(arrA[i], arrB[i], $"{path}[{i}]", diffs);
                }
                return;
            }

            diffs.Add($"{path}: type mismatch ({a.GetType().Name} vs {b.GetType().Name})");
        }

        private static string? ResolveInitialDirectory(params string?[] candidates)
        {
            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
            return null;
        }

        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr);

    public sealed class FieldItem : INotifyPropertyChanged
    {
        private string _value;
        private readonly Action<string>? _valueSetter;

        public FieldItem(
            string name,
            string value,
            bool isBoolean = false,
            bool isNumber = false,
            Action<string>? valueSetter = null,
            string? description = null)
        {
            Name = name;
            _value = value;
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
                _valueSetter?.Invoke(value);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}

