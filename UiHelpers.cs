using System.IO;

namespace SequenceNavigator
{
    /// <summary>
    /// Small helpers shared by the dialogs.
    /// </summary>
    internal static class UiHelpers
    {
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
