using System.Reflection;

namespace SequenceNavigator
{
    /// <summary>
    /// Reads the version from assembly metadata so &lt;Version&gt; in the csproj is the
    /// only place it has to be bumped.
    /// </summary>
    internal static class AppVersion
    {
        public static string Number { get; } = ReadNumber();

        public static string Author { get; } = ReadAuthor();

        private static string ReadNumber()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            // Strip any "+<commit>" suffix in case the SDK appends one.
            var version = informational?.Split('+')[0];
            if (string.IsNullOrWhiteSpace(version))
            {
                version = assembly.GetName().Version?.ToString(3);
            }
            return version ?? "?";
        }

        private static string ReadAuthor()
        {
            var author = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyCompanyAttribute>()?.Company;
            return string.IsNullOrWhiteSpace(author) ? "Carter Smith" : author;
        }
    }
}
