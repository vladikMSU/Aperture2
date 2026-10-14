// Test-only substitutes for the pane's collaborators. The harness compiles the real
// ChatPaneControl.cs against these types; it never loads VSTO, COM, Pi or a provider.
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;

namespace Aperture.Runtime
{
    public sealed class ExcelTarget
    {
        public string WorkbookId = "test";
        public string WorkbookName = "test";
    }

    public sealed class PiEvent : EventArgs
    {
        public string Type;
        public JObject Json;
    }

    public sealed class PiDiagnosticEventArgs : EventArgs
    {
        public string Message;
    }

    public sealed class PiSession : IDisposable
    {
        public static TaskCompletionSource<PiSession> StartGate;
        public static int Starts, Prompts;
        public TaskCompletionSource<bool> StopGate;
        public bool Disposed;
        public bool IsRunning { get { return !Disposed; } }

        // Subscriptions are deliberately inert: these tests control pending process
        // transitions rather than simulating Pi's full event protocol.
        public event EventHandler<PiEvent> EventReceived { add { } remove { } }
        public event EventHandler<PiDiagnosticEventArgs> StandardError { add { } remove { } }

        public static Task<PiSession> StartAsync(object options, ExcelTarget target, CancellationToken cancellation)
        {
            Starts++;
            // Intentionally return a late process even after cancellation, exercising
            // the pane's defensive ownership cleanup rather than the runtime's checks.
            return StartGate.Task;
        }

        public Task SendPromptAsync(string prompt) { Prompts++; return Task.CompletedTask; }
        public Task AbortAsync() { return Task.CompletedTask; }
        public Task<JObject> GetStateAsync() { return Task.FromResult(JObject.Parse("{data:{isStreaming:false}}")); }
        public Task StopAsync() { return StopGate == null ? Task.CompletedTask : StopGate.Task; }
        public void Dispose() { Disposed = true; }
    }
}

namespace Aperture.Excel
{
    internal sealed class WorkbookBinding
    {
        internal Aperture.Runtime.ExcelTarget Target = new Aperture.Runtime.ExcelTarget();
        internal void Validate() { }
    }

    internal sealed class PaneSettings
    {
        internal static PaneSettings Load() { return new PaneSettings(); }
        internal object ToOptions() { return null; }
    }

    internal sealed class SettingsDialog : Form
    {
        internal PaneSettings Settings;
        internal SettingsDialog(PaneSettings settings) { Settings = settings; }
    }

    internal static class Diagnostics
    {
        internal static void Write(string eventName, string detail = null) { }
    }
}
