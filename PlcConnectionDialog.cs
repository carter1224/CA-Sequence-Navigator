using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace SequenceNavigator
{
    public sealed class PlcConnectionDialog : Window
    {
        private readonly TextBox _ipBox;
        private readonly TextBox _ethBox;
        private readonly TextBox _cpuBox;

        public PlcConnectionDialog(string? ip, int? ethSlot, int? cpuSlot)
        {
            Title = "PLC Connection";
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;

            var grid = new Grid
            {
                Margin = new Thickness(14)
            };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });

            grid.Children.Add(MakeLabel("PLC IP:", 0, 0));
            _ipBox = new TextBox { Text = ip ?? "192.168.1.11" };
            Grid.SetRow(_ipBox, 0);
            Grid.SetColumn(_ipBox, 1);
            grid.Children.Add(_ipBox);

            grid.Children.Add(MakeLabel("Ethernet Slot:", 1, 0));
            _ethBox = new TextBox { Text = (ethSlot ?? 1).ToString(CultureInfo.InvariantCulture) };
            Grid.SetRow(_ethBox, 1);
            Grid.SetColumn(_ethBox, 1);
            grid.Children.Add(_ethBox);

            grid.Children.Add(MakeLabel("Controller Slot:", 2, 0));
            _cpuBox = new TextBox { Text = (cpuSlot ?? 0).ToString(CultureInfo.InvariantCulture) };
            Grid.SetRow(_cpuBox, 2);
            Grid.SetColumn(_cpuBox, 1);
            grid.Children.Add(_cpuBox);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var okBtn = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 0, 8, 0) };
            okBtn.Click += OkBtn_Click;
            var cancelBtn = new Button { Content = "Cancel", Width = 80, IsCancel = true };
            cancelBtn.Click += (_, _) => DialogResult = false;
            buttonPanel.Children.Add(okBtn);
            buttonPanel.Children.Add(cancelBtn);

            Grid.SetRow(buttonPanel, 3);
            Grid.SetColumnSpan(buttonPanel, 2);
            grid.Children.Add(buttonPanel);

            Content = grid;
        }

        public string IpAddress { get; private set; } = string.Empty;
        public int EthSlot { get; private set; }
        public int CpuSlot { get; private set; }

        private void OkBtn_Click(object? sender, RoutedEventArgs e)
        {
            string ip = _ipBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(ip))
            {
                MessageBox.Show("Enter a PLC IP address.", "Invalid IP",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(_ethBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int ethSlot))
            {
                MessageBox.Show("Ethernet slot must be a whole number.", "Invalid Slot",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(_cpuBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int cpuSlot))
            {
                MessageBox.Show("Controller slot must be a whole number.", "Invalid Slot",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            IpAddress = ip;
            EthSlot = ethSlot;
            CpuSlot = cpuSlot;
            DialogResult = true;
        }

        private static Label MakeLabel(string text, int row, int col)
        {
            var label = new Label { Content = text, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(label, row);
            Grid.SetColumn(label, col);
            return label;
        }
    }
}

