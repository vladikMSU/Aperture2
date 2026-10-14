using System;
using System.Diagnostics;
using System.IO;

namespace Aperture.Excel
{
    internal static class Diagnostics
    {
        internal static void Write(string eventName, string detail = null)
        {
            try
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aperture2", "logs");
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, "excel-" + Process.GetCurrentProcess().Id + ".log");
                // Lifecycle diagnostics only: no prompts, workbook paths, cells or provider secrets.
                File.AppendAllText(path, DateTime.UtcNow.ToString("o") + " " + eventName + " " + detail + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
