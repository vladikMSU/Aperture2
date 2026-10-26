using System;

namespace Aperture.Excel
{
    // A chat request started through the COM automation, as GetState reports it.
    // The pane and the automation touch it on Excel's UI thread only.
    internal sealed class ScriptedRun
    {
        internal string State { get; private set; } = "running";
        internal string LogPath { get; set; }
        internal string Error { get; private set; }

        // status is the pane's own end of a request: done, stopped or error.
        internal void End(string status, string error)
        {
            if (State != "running") return;
            State = status == "done" ? "done" : status == "stopped" ? "cancelled" : "failed";
            if (State == "failed") Error = String.IsNullOrWhiteSpace(error) ? "Запрос завершился ошибкой" : error;
        }
    }
}
