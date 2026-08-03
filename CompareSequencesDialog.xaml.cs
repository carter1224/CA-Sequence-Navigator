using System.Windows;
using Microsoft.Win32;

namespace SequenceNavigator
{
    public partial class CompareSequencesDialog : Window
    {
        private readonly string? _initialDirectory;

        public CompareSequencesDialog(string? initialDirectory)
        {
            InitializeComponent();
            _initialDirectory = initialDirectory;
            UpdateCompareEnabled();
        }

        public string? FirstZipPath { get; private set; }
        public string? SecondZipPath { get; private set; }

        private void BrowseFirst_Click(object sender, RoutedEventArgs e)
        {
            var path = PickZipFile();
            if (path == null)
            {
                return;
            }

            FirstZipPath = path;
            FirstZipBox.Text = path;
            UpdateCompareEnabled();
        }

        private void BrowseSecond_Click(object sender, RoutedEventArgs e)
        {
            var path = PickZipFile();
            if (path == null)
            {
                return;
            }

            SecondZipPath = path;
            SecondZipBox.Text = path;
            UpdateCompareEnabled();
        }

        private string? PickZipFile()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select SEQ ZIP file",
                Filter = "ZIP Files (*.zip)|*.zip|All Files (*.*)|*.*",
                InitialDirectory = UiHelpers.ResolveInitialDirectory(_initialDirectory)
            };

            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }

        private void Compare_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(FirstZipPath) ||
                string.IsNullOrWhiteSpace(SecondZipPath))
            {
                MessageBox.Show("Select two ZIP files to compare.",
                    "Compare Sequences", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void UpdateCompareEnabled()
        {
            CompareBtn.IsEnabled = !string.IsNullOrWhiteSpace(FirstZipPath) &&
                                   !string.IsNullOrWhiteSpace(SecondZipPath);
        }
    }
}
