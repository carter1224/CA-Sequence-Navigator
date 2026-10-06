using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SequenceNavigator
{
    public enum PlcAction
    {
        Upload,
        Download,
        /// <summary>Read for a comparison only: nothing is saved or changed.</summary>
        Compare,
    }

    /// <summary>What a controller reported about itself in a connection test.</summary>
    public sealed class PlcIdentity
    {
        public string? Name { get; init; }
        public string? ProductName { get; init; }
        public string? Revision { get; init; }
        public string? Serial { get; init; }
        public string? Keyswitch { get; init; }

        public string Describe() => string.Join(" · ", new[]
        {
            Name,
            ProductName,
            Revision == null ? null : "firmware " + Revision,
            Keyswitch,
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    public sealed record PlcTestResult(PlcIdentity? Identity, string? Error);

    public partial class PlcConnectionDialog : Window
    {
        private static readonly Brush GoodBrush = Frozen("#2D7D46");
        private static readonly Brush BadBrush = Frozen("#B42318");

        // The backplane port is carried through unchanged rather than shown. It is not a
        // slot and it is not about Ethernet: in a CIP route, port 1 IS the backplane on
        // every Logix chassis, which is why it is always 1. RSLinx spells the same hop as
        // "\Backplane\". Exposing it only gave users a second number to get wrong.
        private readonly int _ethSlot;
        private readonly Func<string, int, int, Task<PlcTestResult>> _test;
        private (string Ip, int Slot)? _testedFor;

        // Values always come from AppSettings, which owns the install-time defaults.
        public PlcConnectionDialog(PlcAction action, string ip, int ethSlot, int cpuSlot,
            IReadOnlyList<RecentPlc> recent, Func<string, int, int, Task<PlcTestResult>> test,
            string? summary = null)
        {
            InitializeComponent();
            _ethSlot = ethSlot;
            _test = test;

            (Title, Intro.Text, GoBtn.Content) = action switch
            {
                PlcAction.Download => ("Download to PLC",
                    "Writes the open backup's sequences to the controller, replacing what it runs now.",
                    "Download"),
                PlcAction.Compare => ("Compare with PLC",
                    "Reads every sequence from the controller and compares it with the open backup. Nothing is changed or saved.",
                    "Read and compare"),
                _ => ("Upload from PLC",
                    "Reads every SEQ[100] sequence from the controller. Nothing on the PLC is changed.",
                    "Upload"),
            };
            Heading.Text = Title;
            GoBtn.Style = (Style)FindResource(action == PlcAction.Download ? "DangerButton" : "PrimaryButton");

            AddressBox.ItemsSource = recent;
            AddressBox.Text = ip;
            SlotBox.Text = cpuSlot.ToString(CultureInfo.InvariantCulture);

            if (!string.IsNullOrWhiteSpace(summary))
            {
                Summary.Text = summary;
                SummaryBorder.Visibility = Visibility.Visible;
            }

            Loaded += (_, _) => AddressBox.Focus();
        }

        public string IpAddress { get; private set; } = string.Empty;
        public int EthSlot { get; private set; }
        public int CpuSlot { get; private set; }

        /// <summary>Set when a connection test succeeded for the address that was used.</summary>
        public PlcIdentity? Identity { get; private set; }

        private void AddressBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AddressBox.SelectedItem is RecentPlc plc)
            {
                SlotBox.Text = plc.Slot.ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>Checks both fields, showing any problem under the field itself.</summary>
        private bool TryRead(out string ip, out int cpuSlot)
        {
            ip = AddressBox.Text.Trim();
            bool ok = true;

            if (!PlcAddress.IsValid(ip))
            {
                ShowError(AddressError, "Enter an IPv4 address such as 192.168.1.11, or a host name.");
                ok = false;
            }
            else
            {
                AddressError.Visibility = Visibility.Collapsed;
            }

            if (!int.TryParse(SlotBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out cpuSlot) ||
                cpuSlot < 0)
            {
                ShowError(SlotError, "Enter a slot number, 0 or higher.");
                ok = false;
            }
            else
            {
                SlotError.Visibility = Visibility.Collapsed;
            }

            return ok;
        }

        private static void ShowError(TextBlock target, string text)
        {
            target.Text = text;
            target.Visibility = Visibility.Visible;
        }

        private async void TestBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!TryRead(out var ip, out var cpuSlot))
            {
                return;
            }

            TestBtn.IsEnabled = false;
            GoBtn.IsEnabled = false;
            TestResult.Foreground = (Brush)FindResource("SubtleTextBrush");
            TestResult.Text = "Connecting to " + ip + "…";
            try
            {
                var result = await _test(ip, _ethSlot, cpuSlot);
                if (result.Identity != null)
                {
                    Identity = result.Identity;
                    _testedFor = (ip, cpuSlot);
                    TestResult.Foreground = GoodBrush;
                    TestResult.Text = "✓ " + result.Identity.Describe();
                }
                else
                {
                    Identity = null;
                    TestResult.Foreground = BadBrush;
                    TestResult.Text = "Couldn't connect: " + (result.Error ?? "no response.");
                }
            }
            finally
            {
                TestBtn.IsEnabled = true;
                GoBtn.IsEnabled = true;
            }
        }

        private void GoBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!TryRead(out var ip, out var cpuSlot))
            {
                return;
            }

            // A test of a different address says nothing about this one.
            if (_testedFor != (ip, cpuSlot))
            {
                Identity = null;
            }

            IpAddress = ip;
            EthSlot = _ethSlot;
            CpuSlot = cpuSlot;
            DialogResult = true;
        }

        private static SolidColorBrush Frozen(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
    }
}
