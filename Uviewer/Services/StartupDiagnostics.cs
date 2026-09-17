using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Uviewer.Services
{
    internal static class StartupDiagnostics
    {
        private static readonly object Sync = new();

        internal static string LogPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Uviewer", "startup.log");

        internal static void Record(string stage, Exception exception)
        {
            // Diagnostics must never replace the original startup exception.
            try
            {
                string package;
                try { package = Windows.ApplicationModel.Package.Current.Id.FullName; }
                catch { package = "Unpackaged"; }

                var details = new StringBuilder()
                    .AppendLine($"{DateTimeOffset.Now:O} {stage}")
                    .AppendLine($"App: {typeof(App).Assembly.GetName().Version}; Package: {package}")
                    .AppendLine($"OS: {Environment.OSVersion}; UI culture: {CultureInfo.CurrentUICulture.Name}");
                for (Exception? current = exception; current != null; current = current.InnerException)
                    details.AppendLine($"HRESULT: 0x{current.HResult:X8}; {current}");

                lock (Sync)
                {
                    string path = LogPath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                        File.Move(path, path + ".previous", overwrite: true);
                    File.AppendAllText(path, details.AppendLine().ToString());
                }
            }
            catch { }
        }
    }
}
