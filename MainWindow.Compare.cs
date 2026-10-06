using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;

namespace SequenceNavigator
{
    /// <summary>Comparing backups with each other, or the open backup with the PLC.</summary>
    public partial class MainWindow
    {
        private void CompareSequences_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy)
            {
                return;
            }

            var dialog = new CompareSequencesDialog(_settings.LastZipDir, _zipPath)
            {
                Owner = this
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            if (dialog.Mode == CompareMode.AgainstPlc)
            {
                CompareWithPlc();
                return;
            }

            var a = dialog.FirstZipPath!;
            var b = dialog.SecondZipPath!;
            _settings.LastZipDir = Path.GetDirectoryName(b) ?? string.Empty;
            SaveSettings();
            NoteUnsavedEditsExcluded(a, b);
            OpenCompareWindow(a, Path.GetFileName(a), b, Path.GetFileName(b), null);
        }

        /// <summary>
        /// The drift check: reads the controller now (changing nothing) and compares it with
        /// the open backup, answering "has anyone changed the PLC since this backup?"
        /// </summary>
        private async void CompareWithPlc()
        {
            if (_isBusy || _zipPath == null)
            {
                return;
            }

            var conn = AskForPlc(PlcAction.Compare, null);
            if (conn == null)
            {
                return;
            }

            var backupPath = _zipPath;
            NoteUnsavedEditsExcluded(backupPath);
            var snapshot = await CaptureFromPlcAsync(conn, "Reading the PLC to compare", clearWindow: false,
                retry: CompareWithPlc);
            if (snapshot == null)
            {
                return;
            }

            var label = string.Create(CultureInfo.InvariantCulture, $"PLC {conn.Describe}, read {DateTime.Now:HH:mm}");
            OpenCompareWindow(backupPath, Path.GetFileName(backupPath), snapshot, label, () => TryDeleteFile(snapshot));
        }

        /// <summary>Comparisons read files on disk, so unsaved edits are not part of them.</summary>
        private void NoteUnsavedEditsExcluded(params string[] paths)
        {
            if (HasPendingEdits && _zipPath != null &&
                paths.Any(p => string.Equals(p, _zipPath, StringComparison.OrdinalIgnoreCase)))
            {
                ShowInfo(InfoSeverity.Info, "Comparing the saved file.",
                    $"Your {ChangeCountText()} aren't included until you save.");
            }
        }

        private void OpenCompareWindow(string pathA, string labelA, string pathB, string labelB, Action? onClosed)
        {
            try
            {
                var window = new CompareWindow(pathA, labelA, pathB, labelB, LookupDescription, ShowDifference, onClosed)
                {
                    Owner = this
                };
                window.Show();
                SetStatus($"Compared {labelA} with {labelB}");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            {
                ShowInfo(InfoSeverity.Error, "Couldn't compare those backups.", ex.Message);
                onClosed?.Invoke();
            }
        }

        /// <summary>Opens backup A at one difference, in the HMI-matched cards.</summary>
        private void ShowDifference(string pathA, SequenceDifference difference)
        {
            if (!OpenBackupInteractive(pathA))
            {
                return;
            }

            var entry = _zipJsonCache.Keys.FirstOrDefault(k =>
                string.Equals(Path.GetFileNameWithoutExtension(k), difference.Sequence, StringComparison.OrdinalIgnoreCase));
            if (entry != null)
            {
                NavigateTo(entry, difference.Step, difference.Group);
            }
            Activate();
        }
    }
}
