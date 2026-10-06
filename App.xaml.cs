using System;
using System.IO;
using System.Windows;

namespace SequenceNavigator
{
    public partial class App : Application
    {
        /// <summary>
        /// A backup passed on the command line, which is how Explorer's "Open with" hands
        /// a file over. MainWindow opens it once it has loaded.
        /// </summary>
        public static string? StartupFile { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            if (e.Args.Length > 0 && File.Exists(e.Args[0]))
            {
                StartupFile = Path.GetFullPath(e.Args[0]);
            }
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                MessageBox.Show(
                    args.ExceptionObject?.ToString() ?? "Unknown error",
                    "Unhandled Exception",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(-1);
            };
            DispatcherUnhandledException += (_, args) =>
            {
                MessageBox.Show(
                    args.Exception.ToString(),
                    "Unhandled Exception",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                args.Handled = true;
                Shutdown(-1);
            };

        }
    }
}

