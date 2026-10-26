using System;
using System.Runtime.InteropServices;

namespace Aperture.Excel
{
    // Remote control of the ordinary chat for scripts: Application.COMAddIns("Aperture.Excel").Object.
    // Start types a prompt into a new chat of the book's pane and presses Send; Cancel presses Stop.
    [ComVisible(true)]
    [Guid("9C0E4B31-6F2A-4D8B-A1E5-3B7C2D9F4E60")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface IAgentAutomation
    {
        /// <summary>JSON: name, version, commit and build, system prompt hash, loaded panes.</summary>
        string GetInfo();
        /// <summary>Opens the pane of this book, starts a new chat with the provider and model (empty: the saved
        /// settings) and sends the prompt. Returns at once; a refusal is a COMException with the reason.</summary>
        void Start(string workbookName, string prompt, string provider, string model);
        /// <summary>JSON {"state": "idle|running|done|failed|cancelled", "log_path": Pi JSONL of the chat, "error"}.</summary>
        string GetState();
        /// <summary>Same as the Stop button; the request ends within about 10 seconds.</summary>
        void Cancel();
    }

    // StandardOleMarshalObject delivers calls from other processes on Excel's UI thread.
    [ComVisible(true)]
    [Guid("4E8D2A67-1B3C-4F9E-8D20-6A5B7C1E3F98")]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class AgentAutomation : StandardOleMarshalObject, IAgentAutomation
    {
        private readonly ThisAddIn addIn;
        internal AgentAutomation(ThisAddIn addIn) { this.addIn = addIn; }

        public string GetInfo() { return addIn.InfoJson(); }
        public string GetState() { return addIn.ScriptedStateJson(); }
        public void Cancel() { addIn.CancelScripted(); }

        public void Start(string workbookName, string prompt, string provider, string model)
        {
            try { addIn.StartScripted(workbookName, prompt, provider, model); }
            catch (Exception ex)
            {
                Diagnostics.Write("scripted_start_refused", ex.GetType().Name);
                throw new COMException(ex.Message);
            }
        }
    }
}
