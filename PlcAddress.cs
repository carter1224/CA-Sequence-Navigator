using System.Net;
using System.Net.Sockets;

namespace SequenceNavigator
{
    /// <summary>
    /// Validation for the controller address typed into the connection dialogs.
    /// </summary>
    public static class PlcAddress
    {
        /// <summary>
        /// Accepts an IPv4 address or a plain hostname. pycomm3 builds its connection
        /// path as "{address}/{eth-slot}/{cpu-slot}", so an address carrying a path
        /// separator, whitespace, or an option-like prefix can never be usable.
        /// </summary>
        public static bool IsValid(string? address)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                return false;
            }

            var trimmed = address.Trim();

            if (IPAddress.TryParse(trimmed, out var ip))
            {
                // IPv6 would break the slash-delimited path format above.
                return ip.AddressFamily == AddressFamily.InterNetwork;
            }

            return IsHostname(trimmed);
        }

        private static bool IsHostname(string value)
        {
            if (value.Length > 253 || value.StartsWith('-') || value.EndsWith('-'))
            {
                return false;
            }

            foreach (var label in value.Split('.'))
            {
                if (label.Length == 0 || label.Length > 63)
                {
                    return false;
                }

                foreach (var ch in label)
                {
                    // Underscores are irregular in DNS but do occur in Windows host
                    // names, and they are harmless here — the argument list is what
                    // keeps a value from being reinterpreted, not this check.
                    if (!char.IsAsciiLetterOrDigit(ch) && ch != '-' && ch != '_')
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }
}
