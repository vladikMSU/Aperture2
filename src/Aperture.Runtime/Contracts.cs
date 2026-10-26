using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Aperture.Runtime
{
    public sealed class ExcelTarget
    {
        [JsonProperty("excelInstanceId")] public string ExcelInstanceId { get; set; }
        [JsonProperty("workbookId")] public string WorkbookId { get; set; }
        [JsonProperty("excelProcessId")] public int ExcelProcessId { get; set; }
        [JsonProperty("excelHwnd")] public long ExcelHwnd { get; set; }
        [JsonProperty("workbookHwnd")] public long WorkbookHwnd { get; set; }
        [JsonProperty("workbookName")] public string WorkbookName { get; set; }
        [JsonProperty("workbookFullName")] public string WorkbookFullName { get; set; }

        internal void Validate()
        {
            if (string.IsNullOrWhiteSpace(ExcelInstanceId) || string.IsNullOrWhiteSpace(WorkbookId)
                || ExcelProcessId <= 0 || ExcelHwnd == 0 || WorkbookHwnd == 0
                || string.IsNullOrWhiteSpace(WorkbookName) || string.IsNullOrWhiteSpace(WorkbookFullName))
                throw new ArgumentException("Capture the Excel process, workbook window and workbook identity before starting Pi.");
        }
    }

    public sealed class PiSessionOptions
    {
        public string RepositoryRoot { get; set; }
        public string NodePath { get; set; }
        public string PythonPath { get; set; }
        public string Provider { get; set; }
        public string Model { get; set; }
        /// <summary>Pi's --thinking level for this chat; empty keeps Pi's default. Pi clamps it to the model.</summary>
        public string Thinking { get; set; }
        public string StateRoot { get; set; }
        public bool EnableExcelTools { get; set; } = true;
        public int CommandTimeoutSeconds { get; set; } = 45;

        /// <summary>The levels of the pinned Pi's --thinking (docs/cli.md, docs/settings.md).</summary>
        public static readonly string[] ThinkingLevels = { "off", "minimal", "low", "medium", "high", "xhigh", "max" };
    }

    public sealed class PiEvent : EventArgs
    {
        public string Type { get; }
        public JObject Json { get; }
        public PiEvent(JObject json) { Json = json; Type = (string)json["type"] ?? "unknown"; }
    }

    public sealed class PiDiagnosticEventArgs : EventArgs
    {
        public string Message { get; }
        public PiDiagnosticEventArgs(string message) { Message = message; }
    }
}
