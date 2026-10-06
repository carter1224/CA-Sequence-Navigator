using System;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;

namespace SequenceNavigator
{
    public enum CompareMode
    {
        Files,
        AgainstPlc,
    }

    public partial class CompareSequencesDialog : Window
    {
        private readonly string? _initialDirectory;

        public CompareSequencesDialog(string? initialDirectory, string? currentZipPath)
        {
            InitializeComponent();
            _initialDirectory = initialDirectory;

            // Seed A with whatever is already open. The usual comparison is "what I am
            // looking at" against another backup, so this removes one of the two browses.
            if (!string.IsNullOrWhiteSpace(currentZipPath) && File.Exists(currentZipPath))
            {
                FirstZipPath = currentZipPath;
                FirstZipBox.Text = currentZipPath;
            }
            else
            {
                ComparePlcBtn.IsEnabled = false;
                PlcOptionText.Text = "Open a backup first to compare it with the controller.";
            }

            UpdateCompareEnabled();
        }

        public string? FirstZipPath { get; private set; }
        public string? SecondZipPath { get; private set; }
        public CompareMode Mode { get; private set; } = CompareMode.Files;

        private void BrowseFirst_Click(object sender, RoutedEventArgs e) => SetFirst(PickZipFile());

        private void BrowseSecond_Click(object sender, RoutedEventArgs e) => SetSecond(PickZipFile());

        private void SetFirst(string? path)
        {
            if (path == null)
            {
                return;
            }
            FirstZipPath = path;
            FirstZipBox.Text = path;
            UpdateCompareEnabled();
        }

        private void SetSecond(string? path)
        {
            if (path == null)
            {
                return;
            }
            SecondZipPath = path;
            SecondZipBox.Text = path;
            UpdateCompareEnabled();
        }

        private void Swap_Click(object sender, RoutedEventArgs e)
        {
            (FirstZipPath, SecondZipPath) = (SecondZipPath, FirstZipPath);
            FirstZipBox.Text = FirstZipPath ?? string.Empty;
            SecondZipBox.Text = SecondZipPath ?? string.Empty;
            UpdateCompareEnabled();
        }

        private string? PickZipFile()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select a backup ZIP",
                Filter = "ZIP Files (*.zip)|*.zip|All Files (*.*)|*.*",
                InitialDirectory = UiHelpers.ResolveInitialDirectory(_initialDirectory)
            };

            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }

        private static string? DroppedZip(DragEventArgs e) =>
            e.Data.GetData(DataFormats.FileDrop) is string[] files
                ? files.FirstOrDefault(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                : null;

        // TextBoxes take text drops by default; the Preview handlers claim file drops first.
        private void Box_PreviewDragOver(object sender, DragEventArgs e)
        {
            e.Effects = DroppedZip(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void FirstBox_PreviewDrop(object sender, DragEventArgs e)
        {
            SetFirst(DroppedZip(e));
            e.Handled = true;
        }

        private void SecondBox_PreviewDrop(object sender, DragEventArgs e)
        {
            SetSecond(DroppedZip(e));
            e.Handled = true;
        }

        // Dropped elsewhere on the dialog: fill whichever box is still empty.
        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = DroppedZip(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            var path = DroppedZip(e);
            if (string.IsNullOrEmpty(FirstZipPath))
            {
                SetFirst(path);
            }
            else
            {
                SetSecond(path);
            }
        }

        private void Compare_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(FirstZipPath) || string.IsNullOrWhiteSpace(SecondZipPath))
            {
                return;
            }

            Mode = CompareMode.Files;
            DialogResult = true;
        }

        private void ComparePlc_Click(object sender, RoutedEventArgs e)
        {
            Mode = CompareMode.AgainstPlc;
            DialogResult = true;
        }

        private void UpdateCompareEnabled()
        {
            CompareBtn.IsEnabled = !string.IsNullOrWhiteSpace(FirstZipPath) &&
                                   !string.IsNullOrWhiteSpace(SecondZipPath);
        }
    }
}
