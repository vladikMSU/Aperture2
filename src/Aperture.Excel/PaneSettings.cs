using System;
using System.IO;
using System.Reflection;
using Aperture.Runtime;
using Newtonsoft.Json;

namespace Aperture.Excel
{
    internal sealed class PaneSettings
    {
        public string RepositoryRoot { get; set; }
        public string NodePath { get; set; }
        public string PythonPath { get; set; }
        public string Provider { get; set; }
        public string Model { get; set; }

        internal static string SettingsPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aperture2", "settings.json"); }
        }

        internal static PaneSettings Load()
        {
            PaneSettings settings = null;
            var settingsFileExists = File.Exists(SettingsPath);
            if (settingsFileExists)
            {
                try { settings = JsonConvert.DeserializeObject<PaneSettings>(File.ReadAllText(SettingsPath)); }
                catch (Exception ex) when (ex is IOException || ex is JsonException)
                { Diagnostics.Write("settings_read_failed", ex.GetType().Name); }
            }
            settings = settings ?? (settingsFileExists
                ? new PaneSettings()
                : new PaneSettings { Provider = "openrouter", Model = "openrouter/free" });
            if (String.IsNullOrWhiteSpace(settings.RepositoryRoot)) settings.RepositoryRoot = DiscoverRepository();
            return settings;
        }

        internal void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
            File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(this, Formatting.Indented));
        }

        internal PiSessionOptions ToOptions()
        {
            return new PiSessionOptions
            {
                RepositoryRoot = RepositoryRoot,
                NodePath = NullIfEmpty(NodePath),
                PythonPath = NullIfEmpty(PythonPath),
                Provider = NullIfEmpty(Provider),
                Model = NullIfEmpty(Model)
            };
        }

        private static string NullIfEmpty(string value) { return String.IsNullOrWhiteSpace(value) ? null : value.Trim(); }

        private static string DiscoverRepository()
        {
            var configured = Environment.GetEnvironmentVariable("APERTURE_ROOT");
            if (!String.IsNullOrWhiteSpace(configured)) return configured;
            // CodeBase preserves the installed path when VSTO shadow-copies the assembly.
            var uri = new Uri(Assembly.GetExecutingAssembly().CodeBase);
            var directory = new DirectoryInfo(Path.GetDirectoryName(uri.LocalPath));
            while (directory != null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "external", "pi")) &&
                    Directory.Exists(Path.Combine(directory.FullName, "src", "Aperture.Runtime"))) return directory.FullName;
                directory = directory.Parent;
            }
            return "";
        }
    }
}
