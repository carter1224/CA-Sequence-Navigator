using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace SequenceNavigator
{
    /// <summary>
    /// Small helpers shared by the hand-built dialogs. Every Make* method places the
    /// control in the grid itself, so callers never have to remember which ones do.
    /// </summary>
    internal static class UiHelpers
    {
        public static Label MakeLabel(Grid grid, string text, int row, int col)
        {
            var label = new Label { Content = text, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(label, row);
            Grid.SetColumn(label, col);
            grid.Children.Add(label);
            return label;
        }

        public static TextBox MakeTextBox(Grid grid, string text, int row, int col)
        {
            var box = new TextBox { Text = text };
            Grid.SetRow(box, row);
            Grid.SetColumn(box, col);
            grid.Children.Add(box);
            return box;
        }

        public static CheckBox MakeCheckBox(Grid grid, string text, bool isChecked, int row, int col)
        {
            var box = new CheckBox { Content = text, IsChecked = isChecked };
            Grid.SetRow(box, row);
            Grid.SetColumn(box, col);
            grid.Children.Add(box);
            return box;
        }

        /// <summary>
        /// Returns the first candidate that exists on disk, or null so the file dialog
        /// falls back to its own default.
        /// </summary>
        public static string? ResolveInitialDirectory(params string?[] candidates)
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
    }
}
