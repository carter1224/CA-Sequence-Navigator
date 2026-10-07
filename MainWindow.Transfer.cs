using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SequenceNavigator
{
    /// <summary>A controller the user chose in the PLC Connection dialog.</summary>
    public sealed record PlcConnection(string Ip, int EthSlot, int CpuSlot, PlcIdentity? Identity)
    {
        /// <summary>"PAINT_MAIN (192.168.1.11)" when the name is known, else the address.</summary>
        public string Describe => Identity?.Name is { Length: > 0 } name ? $"{name} ({Ip})" : Ip;
    }

    /// <summary>Everything that talks to a PLC through the bundled Python helpers.</summary>
    public partial class MainWindow
    {
        private static readonly TimeSpan MinProcessTimeout = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan IdentityTimeout = TimeSpan.FromSeconds(30);

        private async void ImportFromPlc_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy || !ResolvePendingEdits("uploading from the PLC", "Save and upload", "Upload without saving"))
            {
                return;
            }

            var conn = AskForPlc(PlcAction.Upload, null);
            if (conn == null)
            {
                return;
            }

            // Unload the open file first. An empty window that fills when the upload
            // finishes makes it unmistakable which data came from the PLC.
            var stagingPath = await CaptureFromPlcAsync(conn, "Uploading from PLC", clearWindow: true,
                retry: () => ImportFromPlc_Click(this, new RoutedEventArgs()), keepBusyOnSuccess: true);
            if (stagingPath == null)
            {
                return;
            }

            try
            {
                // The full upload is in hand and verified; only now ask where it goes. The
                // window stays in its transfer state meanwhile: nothing is loaded yet, so
                // leaving it would show the start screen behind the save dialog.
                Spinner.Visibility = Visibility.Collapsed;
                LoadingCaption.Text = "Upload complete. Choose where to save it.";
                SetStatus("Upload complete. Choose where to save it.");
                var destination = SaveUploadAs(stagingPath, conn);
                if (destination == null)
                {
                    SetStatus("Upload discarded");
                    return;
                }

                _settings.LastZipDir = Path.GetDirectoryName(destination) ?? string.Empty;
                if (LoadZip(destination))
                {
                    ShowInfo(InfoSeverity.Success, "Upload complete.",
                        $"{JsonPicker.Items.Count} sequences from {conn.Describe} saved as {Path.GetFileName(destination)}.");
                }
            }
            finally
            {
                EndTransfer();
                TryDeleteFile(stagingPath);
            }
        }

        private async void ExportToPlc_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy)
            {
                return;
            }
            if (_zipJsonCache.Count == 0 || string.IsNullOrWhiteSpace(_zipPath))
            {
                ShowInfo(InfoSeverity.Info, "Open a backup first.", "Download to PLC writes the open backup's sequences.");
                return;
            }

            // The helper downloads the file on disk, so anything unsaved would silently not
            // be what reaches the PLC.
            if (HasPendingEdits)
            {
                var save = ChoiceDialog.Ask(this, "Unsaved changes", "Save your changes before downloading?",
                    $"Only saved changes are written to the PLC. {ChangeCountText()} in {OpenFileName}.",
                    "Save and continue", null, "Cancel", ChangeLines(), ChoiceTone.Warning);
                if (save != ChoiceResult.Primary || !SaveZipInPlace())
                {
                    return;
                }
            }

            int tagCount = _zipJsonCache.Count;
            var summary = _savedChanges.Count == 0
                ? $"Writes all {tagCount} tags in {OpenFileName}, exactly as saved."
                : $"Writes all {tagCount} tags in {OpenFileName}, including {_savedChanges.Count} change{(_savedChanges.Count == 1 ? "" : "s")} saved this session.";
            var conn = AskForPlc(PlcAction.Download, summary);
            if (conn == null)
            {
                return;
            }

            // The last stop before a running controller changes: name the target, say what
            // happens, list what changed, and make Cancel the Enter key.
            var target = conn.Identity?.Name is { Length: > 0 } name ? name : conn.Ip;
            var body = "This replaces the sequences the controller runs now. " + (conn.Identity is { } identity
                ? $"Connected to {identity.Describe()}."
                : "The controller wasn't tested: use Test connection to confirm it's the right one.");
            IEnumerable<string> items = _savedChanges.Count > 0
                ? _savedChanges.Select(c => c.Line)
                : new[] { "No fields were changed in this session; the backup is written as saved." };
            var confirm = ChoiceDialog.Ask(this, "Download to PLC", $"Write {tagCount} tags to {target}?", body,
                $"Download {tagCount} tags", null, "Cancel", items, ChoiceTone.Danger, safeIsDefault: true);
            if (confirm != ChoiceResult.Primary)
            {
                return;
            }

            BeginTransfer("Downloading to PLC");
            try
            {
                var result = await RunHelperAsync("seq_importer.py", new List<string>
                {
                    "--ip", conn.Ip,
                    "--eth-slot", conn.EthSlot.ToString(CultureInfo.InvariantCulture),
                    "--cpu-slot", conn.CpuSlot.ToString(CultureInfo.InvariantCulture),
                    "--zip", _zipPath,
                    "--retries", _settings.RetryCount.ToString(CultureInfo.InvariantCulture),
                    "--retry-delay", _settings.RetryDelaySeconds.ToString(CultureInfo.InvariantCulture),
                    "--timeout", _settings.TimeoutSeconds.ToString(CultureInfo.InvariantCulture),
                });
                if (result == null)
                {
                    return;
                }

                if (result.Value.ExitCode != 0)
                {
                    var output = result.Value.StdErr + result.Value.StdOut;
                    // The importer reports what landed before it gave up.
                    bool nothingWritten = output.Contains("Fully written: none", StringComparison.Ordinal) &&
                                          !output.Contains("INTERRUPTED", StringComparison.Ordinal);
                    if (nothingWritten || !output.Contains("PARTIALLY UPDATED", StringComparison.Ordinal))
                    {
                        ShowInfo(InfoSeverity.Error, "Download failed.",
                            FailureReason(result.Value, conn) + (nothingWritten ? " Nothing was written to the PLC." : string.Empty),
                            output, "Try again", () => ExportToPlc_Click(this, new RoutedEventArgs()));
                    }
                    else
                    {
                        ShowInfo(InfoSeverity.Error, "Download stopped partway. The PLC may be partly updated.",
                            FailureReason(result.Value, conn) + " Download again to put every sequence back in a known state.",
                            output, "Download again", () => ExportToPlc_Click(this, new RoutedEventArgs()));
                    }
                    return;
                }

                _savedChanges.Clear();
                ExitEditMode();
                ShowInfo(InfoSeverity.Success, "Download complete.",
                    string.Create(CultureInfo.InvariantCulture, $"{tagCount} tags written to {conn.Describe} at {DateTime.Now:HH:mm}."));
                SetStatus($"Downloaded {OpenFileName} to {conn.Describe}");
            }
            catch (Exception ex) when (IsHelperFailure(ex))
            {
                ShowInfo(InfoSeverity.Error, "Download failed.", HelperExceptionReason(ex), ex.ToString(),
                    "Try again", () => ExportToPlc_Click(this, new RoutedEventArgs()));
            }
            finally
            {
                EndTransfer();
            }
        }

        /// <summary>
        /// Shows the PLC Connection dialog and remembers what was entered (address, slot,
        /// and the controller's identity if it was tested). Null if cancelled.
        /// </summary>
        private PlcConnection? AskForPlc(PlcAction action, string? summary)
        {
            var dialog = new PlcConnectionDialog(action, _settings.PlcIp, _settings.EthSlot, _settings.CpuSlot,
                _settings.RecentPlcs, TestPlcAsync, summary)
            {
                Owner = this
            };

            if (dialog.ShowDialog() != true)
            {
                return null;
            }

            // Remember whatever was entered here, even if the transfer is then cancelled.
            _settings.PlcIp = dialog.IpAddress;
            _settings.EthSlot = dialog.EthSlot;
            _settings.CpuSlot = dialog.CpuSlot;
            _settings.AddRecentPlc(dialog.IpAddress, dialog.CpuSlot, dialog.Identity?.Name, dialog.Identity?.ProductName);
            SaveSettings();
            return new PlcConnection(dialog.IpAddress, dialog.EthSlot, dialog.CpuSlot, dialog.Identity);
        }

        /// <summary>Reads only the controller's identity, for the dialog's Test connection.</summary>
        private async Task<PlcTestResult> TestPlcAsync(string ip, int ethSlot, int cpuSlot)
        {
            try
            {
                var result = await RunHelperAsync("plc_identity.py", new List<string>
                {
                    "--ip", ip,
                    "--eth-slot", ethSlot.ToString(CultureInfo.InvariantCulture),
                    "--cpu-slot", cpuSlot.ToString(CultureInfo.InvariantCulture),
                    "--timeout", _settings.TimeoutSeconds.ToString(CultureInfo.InvariantCulture),
                }, IdentityTimeout);
                if (result == null)
                {
                    return new PlcTestResult(null, "the connection-test helper is missing.");
                }
                if (result.Value.ExitCode != 0)
                {
                    return new PlcTestResult(null, ErrorLine(result.Value));
                }

                var json = JsonNode.Parse(result.Value.StdOut.Trim());
                var identity = new PlcIdentity
                {
                    Name = (string?)json?["name"],
                    ProductName = (string?)json?["product_name"],
                    Revision = (string?)json?["revision"],
                    Serial = (string?)json?["serial"],
                    Keyswitch = (string?)json?["keyswitch"],
                };
                return new PlcTestResult(identity, null);
            }
            catch (Exception ex) when (IsHelperFailure(ex) || ex is JsonException)
            {
                return new PlcTestResult(null, HelperExceptionReason(ex));
            }
        }

        /// <summary>
        /// Reads every SEQ[100] tag into a verified ZIP in the temp folder. Reports any
        /// failure itself and returns null; on success returns the temp path, which the
        /// caller owns and deletes. With keepBusyOnSuccess the transfer state (spinner
        /// panel, disabled controls) is left on for the caller to end.
        /// </summary>
        private async Task<string?> CaptureFromPlcAsync(PlcConnection conn, string caption, bool clearWindow,
            Action retry, bool keepBusyOnSuccess = false)
        {
            bool succeeded = false;
            // The exporter writes here first. Nothing appears where backups are kept until
            // the whole upload has been read and verified and the user has chosen a place.
            var stagingPath = Path.Combine(Path.GetTempPath(), $"SequenceNavigator_upload_{Guid.NewGuid():N}.zip");
            if (clearWindow)
            {
                ClearLoadedZip();
            }

            BeginTransfer(caption);
            try
            {
                var args = new List<string>
                {
                    "--ip", conn.Ip,
                    "--eth-slot", conn.EthSlot.ToString(CultureInfo.InvariantCulture),
                    "--cpu-slot", conn.CpuSlot.ToString(CultureInfo.InvariantCulture),
                    "--out-zip", stagingPath,
                    "--retries", _settings.RetryCount.ToString(CultureInfo.InvariantCulture),
                    "--retry-delay", _settings.RetryDelaySeconds.ToString(CultureInfo.InvariantCulture),
                    "--timeout", _settings.TimeoutSeconds.ToString(CultureInfo.InvariantCulture),
                };
                if (_settings.IncludeProgramTags)
                {
                    args.Add("--include-program-tags");
                }

                var result = await RunHelperAsync("seq_exporter.py", args);
                if (result == null)
                {
                    return null;
                }
                if (result.Value.ExitCode != 0)
                {
                    ShowInfo(InfoSeverity.Error, "Couldn't read the PLC.",
                        FailureReason(result.Value, conn) + " Nothing was saved.",
                        result.Value.StdErr + result.Value.StdOut, "Try again", retry);
                    TryDeleteFile(stagingPath);
                    return null;
                }

                _settings.AddRecentPlc(conn.Ip, conn.CpuSlot, conn.Identity?.Name, conn.Identity?.ProductName);
                succeeded = true;
                return stagingPath;
            }
            catch (Exception ex) when (IsHelperFailure(ex))
            {
                ShowInfo(InfoSeverity.Error, "Couldn't read the PLC.", HelperExceptionReason(ex) + " Nothing was saved.",
                    ex.ToString(), "Try again", retry);
                TryDeleteFile(stagingPath);
                return null;
            }
            finally
            {
                if (!(succeeded && keepBusyOnSuccess))
                {
                    EndTransfer();
                }
            }
        }

        private void BeginTransfer(string caption)
        {
            Spinner.Visibility = Visibility.Visible;
            LoadingCaption.Text = caption;
            LoadingOverlay.Visibility = Visibility.Visible;
            Info.Hide();
            SetStatus(caption + "…");
            SetBusy(true);
        }

        private void EndTransfer()
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            SetBusy(false);
            if (StatusText.Text.EndsWith('…'))
            {
                SetStatus("Ready");
            }
        }

        /// <summary>
        /// Runs one of the bundled helper scripts. Null (with the problem already shown)
        /// if the script is missing from the install.
        /// </summary>
        private async Task<ProcessResult?> RunHelperAsync(string script, List<string> args, TimeSpan? timeout = null)
        {
            var scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, script);
            if (!File.Exists(scriptPath))
            {
                ShowInfo(InfoSeverity.Error, $"{script} is missing.",
                    "It should be next to SequenceNavigator.exe. Reinstall from the release ZIP.");
                return null;
            }

            args.Insert(0, scriptPath);
            return await RunProcessAsync(ResolvePythonPath(), args, timeout ?? ResolveProcessTimeout());
        }

        // Arguments are passed as a discrete list rather than one concatenated string so
        // that a value typed into a dialog cannot split itself into extra arguments.
        private async Task<ProcessResult> RunProcessAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
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
                    $"The PLC didn't finish within {timeout.TotalSeconds:N0} seconds, so the transfer was stopped." +
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

        private static bool IsHelperFailure(Exception ex) =>
            ex is TimeoutException or Win32Exception or InvalidOperationException or IOException;

        private static string HelperExceptionReason(Exception ex) => ex switch
        {
            TimeoutException => ex.Message,
            Win32Exception => "The PLC helper (bundled Python) couldn't be started. Reinstall from the release ZIP.",
            _ => ex.Message,
        };

        /// <summary>
        /// Plain English first: what went wrong and what to check. The raw output stays
        /// behind the info bar's "Show details".
        /// </summary>
        private static string FailureReason(ProcessResult result, PlcConnection conn)
        {
            var line = ErrorLine(result);
            string[] connectionHints =
            {
                "timed out", "timeout", "connection", "unreachable", "refused", "forward open",
                "forward_open", "no route", "failed to open", "socket", "10060", "10061", "10065",
            };
            if (connectionHints.Any(h => line.Contains(h, StringComparison.OrdinalIgnoreCase)))
            {
                return $"Couldn't reach the PLC at {conn.Ip}, slot {conn.CpuSlot}. Check the cable, the address and the slot.";
            }
            return $"The PLC reported: {line}.";
        }

        /// <summary>The helper's last "ERROR:" line, or failing that its last line.</summary>
        private static string ErrorLine(ProcessResult result)
        {
            var lines = (result.StdErr + "\n" + result.StdOut)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var error = lines.LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal));
            var text = error != null ? error["ERROR:".Length..].Trim() : lines.LastOrDefault();
            return string.IsNullOrWhiteSpace(text) ? "the helper stopped without saying why" : text.TrimEnd('.');
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
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
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
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
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
                File.AppendAllText(_settings.ResolveLogPath(), text);
            }
            catch (Exception)
            {
                // Deliberately swallowed, and the one place that is right: this IS the
                // logger, so there is nowhere to report to, and a failed debug write must
                // never break the PLC transfer that triggered it.
            }
        }

        /// <summary>
        /// Asks where a finished upload should be saved and copies it there. Cancelling
        /// asks before throwing the upload away, since getting it back means another trip
        /// to the PLC. Returns the saved path, or null when the user chose to discard it.
        /// </summary>
        private string? SaveUploadAs(string stagingPath, PlcConnection conn)
        {
            while (true)
            {
                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                var prefix = SafeFileName(conn.Identity?.Name) ?? "seq_export";
                var saveDialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Save the uploaded backup",
                    Filter = "Backup ZIP (*.zip)|*.zip|All Files (*.*)|*.*",
                    FileName = $"{prefix}_{stamp}.zip",
                    InitialDirectory = UiHelpers.ResolveInitialDirectory(_settings.LastZipDir)
                };

                if (saveDialog.ShowDialog(this) != true)
                {
                    var discard = ChoiceDialog.Ask(this, "Discard upload", "Discard this upload?",
                        "It hasn't been saved. Getting it back means reading the PLC again.",
                        "Discard upload", null, "Choose a location", tone: ChoiceTone.Warning, safeIsDefault: true);
                    if (discard == ChoiceResult.Primary)
                    {
                        return null;
                    }
                    continue;
                }

                try
                {
                    CopyIntoPlace(stagingPath, saveDialog.FileName);
                    return saveDialog.FileName;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    var retry = ChoiceDialog.Ask(this, "Couldn't save", "The upload couldn't be saved there.",
                        $"{saveDialog.FileName}\n\n{ex.Message}",
                        "Choose another location", null, "Discard upload", tone: ChoiceTone.Warning);
                    if (retry != ChoiceResult.Primary)
                    {
                        return null;
                    }
                }
            }
        }

        /// <summary>A controller name made safe to start a file name with, or null.</summary>
        private static string? SafeFileName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }
            var invalid = Path.GetInvalidFileNameChars();
            var safe = new string(name.Trim().Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray());
            return safe.Length == 0 ? null : safe;
        }

        /// <summary>
        /// Copies beside the destination first, then swaps it in. A copy cut short (a USB
        /// stick pulled, a network share dropping) must never leave a partial ZIP under the
        /// real name, and an existing file there stays intact until the new one is whole.
        /// </summary>
        private static void CopyIntoPlace(string source, string destination)
        {
            var partial = destination + ".partial";
            try
            {
                File.Copy(source, partial, overwrite: true);
                if (new FileInfo(partial).Length != new FileInfo(source).Length)
                {
                    throw new IOException("The copied file is incomplete.");
                }
                File.Move(partial, destination, overwrite: true);
            }
            catch
            {
                TryDeleteFile(partial);
                throw;
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: a leftover temp or .partial file is untidy, not harmful.
            }
        }
    }
}
