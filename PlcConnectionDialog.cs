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

        // Values always come from AppSettings, which owns the install-time defaults.
        public PlcConnectionDialog(string ip, int ethSlot, int cpuSlot)
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

            UiHelpers.MakeLabel(grid, "PLC IP:", 0, 0);
            _ipBox = UiHelpers.MakeTextBox(grid, ip, 0, 1);

            UiHelpers.MakeLabel(grid, "Ethernet Slot:", 1, 0);
            _ethBox = UiHelpers.MakeTextBox(grid, ethSlot.ToString(CultureInfo.InvariantCulture), 1, 1);

            UiHelpers.MakeLabel(grid, "Controller Slot:", 2, 0);
            _cpuBox = UiHelpers.MakeTextBox(grid, cpuSlot.ToString(CultureInfo.InvariantCulture), 2, 1);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var okBtn = new Button { Content = "OK", Margin = new Thickness(0, 0, 8, 0) };
            okBtn.Click += OkBtn_Click;
            var cancelBtn = new Button { Content = "Cancel", IsCancel = true };
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
            if (!PlcAddress.IsValid(ip))
            {
                MessageBox.Show("Enter a valid PLC IPv4 address or hostname.", "Invalid Address",
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
    }
}

