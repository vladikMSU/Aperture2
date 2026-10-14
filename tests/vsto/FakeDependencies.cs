// Test-only substitutes for the pane's collaborators. The harness compiles the real
// ChatPaneControl.cs against these types; it never loads VSTO, COM, Pi or a provider.
using System;
using System.Collections.Generic;
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
        public static int Starts, Prompts, LastPromptThreadId;
        public TaskCompletionSource<bool> StopGate;
        public TaskCompletionSource<bool> PromptGate;
        public TaskCompletionSource<bool> AbortGate;
        public bool Disposed;
        public string PromptDisposition = "started";
        public bool StateStreaming;
        public bool IsRunning { get { return !Disposed; } }

        public event EventHandler<PiEvent> EventReceived;
        public event EventHandler<PiDiagnosticEventArgs> StandardError;

        public void EmitEvent(JObject json)
        {
            EventReceived?.Invoke(this, new PiEvent { Type = (string)json["type"], Json = json });
        }
        public void EmitDiagnostic(string message)
        {
            StandardError?.Invoke(this, new PiDiagnosticEventArgs { Message = message });
        }
        public Action CaptureEventDelivery(JObject json)
        {
            var handlers = EventReceived;
            return () => handlers?.Invoke(this, new PiEvent { Type = (string)json["type"], Json = json });
        }

        public static Task<PiSession> StartAsync(object options, ExcelTarget target, CancellationToken cancellation)
        {
            Starts++;
            // Intentionally return a late process even after cancellation, exercising
            // the pane's defensive ownership cleanup rather than the runtime's checks.
            return StartGate.Task;
        }

        public async Task<JObject> SendPromptAsync(string prompt)
        {
            LastPromptThreadId = Thread.CurrentThread.ManagedThreadId;
            Prompts++;
            if (PromptGate != null) await PromptGate.Task.ConfigureAwait(false);
            return new JObject { ["success"] = true, ["data"] = new JObject { ["disposition"] = PromptDisposition } };
        }
        public async Task<JObject> AbortAsync()
        {
            if (AbortGate != null) await AbortGate.Task.ConfigureAwait(false);
            return JObject.Parse("{success:true}");
        }
        public Task<JObject> GetStateAsync() { return Task.FromResult(new JObject { ["data"] = new JObject { ["isStreaming"] = StateStreaming } }); }
        public Task StopAsync() { return StopGate == null ? Task.CompletedTask : StopGate.Task; }
        public void Dispose() { Disposed = true; }
    }
}

namespace Aperture.Excel
{
    internal sealed class ChatPaneCommandEventArgs : EventArgs
    {
        internal string Command { get; set; }
        internal string Text { get; set; }
    }

    // WebView2 is intentionally replaced by an owner-thread-checked view. The
    // controller is real; the tests do not require browser downloads or Excel.
    internal sealed class ChatPaneView : UserControl
    {
        private readonly int ownerThread = Thread.CurrentThread.ManagedThreadId;
        internal string BindingText, StatusText;
        internal bool Busy, CanStop, PromptCleared, StatusIsError;
        internal readonly List<string> Transcript = new List<string>();
        internal readonly List<JObject> Activity = new List<JObject>();
        internal event EventHandler<ChatPaneCommandEventArgs> Command;
        internal bool IsReady { get { return true; } }
        internal string InitializationError { get { return null; } }

        private void VerifyThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != ownerThread)
                throw new InvalidOperationException("ChatPaneView method was called outside its owning UI thread.");
            var handle = Handle;
        }

        internal void SetBinding(string text) { VerifyThread(); BindingText = text; }
        internal void SetStatus(string text, bool error) { VerifyThread(); StatusText = text; StatusIsError = error; }
        internal void SetBusy(bool busy, bool canStop = true) { VerifyThread(); Busy = busy; CanStop = canStop; }
        internal void AppendMessage(string who, string text) { VerifyThread(); Transcript.Add(who + ": " + text); }
        internal void ApplyActivity(JObject rawPiEvent) { VerifyThread(); Activity.Add((JObject)rawPiEvent.DeepClone()); }
        internal void ClearTranscript() { VerifyThread(); Transcript.Clear(); Activity.Clear(); }
        internal void ClearPrompt() { VerifyThread(); PromptCleared = true; }
        internal void EmitCommand(string command, string text = null)
        {
            VerifyThread();
            Command?.Invoke(this, new ChatPaneCommandEventArgs { Command = command, Text = text });
        }
    }

    internal sealed class WorkbookBinding
    {
        private readonly int uiThreadId = Thread.CurrentThread.ManagedThreadId;
        internal string ValidationFailure;
        internal Aperture.Runtime.ExcelTarget Target = new Aperture.Runtime.ExcelTarget();
        internal void Validate()
        {
            if (Thread.CurrentThread.ManagedThreadId != uiThreadId)
                throw new InvalidOperationException("Workbook validation left the owning UI thread.");
            if (ValidationFailure != null) throw new InvalidOperationException(ValidationFailure);
        }
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
