using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Aperture.Runtime;
using Newtonsoft.Json;
using ExcelInterop = Microsoft.Office.Interop.Excel;
using Office = Microsoft.Office.Core;

namespace Aperture.Excel
{
    public partial class ThisAddIn
    {
        private readonly string instanceId = Guid.NewGuid().ToString("N");
        private readonly Dictionary<int, PaneEntry> panes = new Dictionary<int, PaneEntry>();
        private AgentAutomation automation;
        private bool loaded;
        private int? runningWindow;
        private ScriptedRun scriptedRun;
        private int scriptedWindow;
        private System.Windows.Forms.Timer paneCleanupTimer;

        private void ThisAddIn_Startup(object sender, EventArgs args)
        {
            Application.WindowActivate += WindowActivated;
            loaded = true;
            paneCleanupTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            paneCleanupTimer.Tick += PruneClosedPanes;
            paneCleanupTimer.Start();
            Diagnostics.Write("addin_loaded", "version=0.1.0");
            try { EnsureActivePane(); }
            catch (Exception ex) { Diagnostics.Write("initial_pane_deferred", ex.GetType().Name); }
        }

        private void WindowActivated(ExcelInterop.Workbook workbook, ExcelInterop.Window window)
        {
            try { EnsurePane(window); }
            catch (Exception ex) { Diagnostics.Write("pane_create_failed", ex.GetType().Name); }
        }

        internal void TogglePane()
        {
            try
            {
                var entry = EnsureActivePane();
                if (entry != null) entry.Pane.Visible = !entry.Pane.Visible;
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show(ex.Message, "Aperture2", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
            }
        }

        private PaneEntry EnsureActivePane()
        {
            var window = Application.ActiveWindow;
            return window == null ? null : EnsurePane(window);
        }

        private PaneEntry EnsurePane(ExcelInterop.Window window)
        {
            var hwnd = window.Hwnd;
            PaneEntry entry;
            if (panes.TryGetValue(hwnd, out entry))
            {
                if (!entry.Control.IsDisposed && entry.Lifetime.IsCurrent) return entry;
                RemovePane(hwnd, entry);
            }
            var lifetime = new WindowLifetime(hwnd, instanceId);
            var control = new ChatPaneControl(() => CaptureActiveWorkbook(window, lifetime),
                () => AcquireTurn(hwnd), () => ReleaseTurn(hwnd));
            var taskPane = CustomTaskPanes.Add(control, "Aperture2", window);
            taskPane.DockPosition = Office.MsoCTPDockPosition.msoCTPDockPositionRight;
            taskPane.Width = 410;
            taskPane.Visible = true;
            entry = new PaneEntry { Pane = taskPane, Control = control, Lifetime = lifetime, PaneId = Guid.NewGuid().ToString("N") };
            panes[hwnd] = entry;
            Diagnostics.Write("pane_created", "window=" + hwnd);
            return entry;
        }

        private void PruneClosedPanes(object sender, EventArgs args)
        {
            if (!loaded || panes.Count == 0) return;
            try
            {
                // WinForms Timer runs on Excel's UI thread. Read the final window collection
                // after close, not WorkbookBeforeClose, whose close can still be cancelled.
                var windows = Application.Windows;
                var open = new HashSet<int>();
                for (var index = 1; index <= windows.Count; index++)
                {
                    var window = windows[index];
                    open.Add(window.Hwnd);
                }
                foreach (var pair in panes.ToArray())
                {
                    if (pair.Value.Control.IsDisposed || !open.Contains(pair.Key) ||
                        !pair.Value.Lifetime.IsCurrent) RemovePane(pair.Key, pair.Value);
                }
            }
            catch (COMException) { } // An edit/modal dialog can postpone this sweep until the next UI tick.
            catch (InvalidComObjectException) { }
        }

        private void RemovePane(int hwnd, PaneEntry entry)
        {
            panes.Remove(hwnd);
            ReleaseTurn(hwnd);
            entry.Control.Dispose(); // Cancels pending startup and terminates only this pane's Pi/MCP tree.
            entry.Lifetime.Dispose();
            try { CustomTaskPanes.Remove(entry.Pane); }
            catch (ObjectDisposedException) { }
            catch (COMException) { }
            catch (ArgumentException) { } // Excel may have already removed the host pane.
            Diagnostics.Write("pane_removed", "window=" + hwnd);
        }

        private bool AcquireTurn(int hwnd)
        {
            if (runningWindow.HasValue && runningWindow.Value != hwnd) return false;
            runningWindow = hwnd;
            return true;
        }

        private void ReleaseTurn(int hwnd)
        {
            if (runningWindow == hwnd) runningWindow = null;
        }

        private WorkbookBinding CaptureActiveWorkbook(ExcelInterop.Window paneWindow, WindowLifetime lifetime)
        {
            // A pane belongs to its Excel window. Do not capture another window's ActiveWorkbook.
            var books = Application.Workbooks;
            for (var bookIndex = 1; bookIndex <= books.Count; bookIndex++)
            {
                var book = books[bookIndex];
                var windows = book.Windows;
                for (var windowIndex = 1; windowIndex <= windows.Count; windowIndex++)
                    if (windows[windowIndex].Hwnd == paneWindow.Hwnd)
                        return new WorkbookBinding(Application, book, paneWindow, instanceId, lifetime);
            }
            throw new InvalidOperationException("Окно больше не связано с открытой книгой. Откройте панель в нужной книге.");
        }

        protected override Office.IRibbonExtensibility CreateRibbonExtensibilityObject()
        {
            return new ApertureRibbon();
        }

        protected override object RequestComAddInAutomationService()
        {
            return automation ?? (automation = new AgentAutomation(this));
        }

        // Automation calls arrive on Excel's UI thread (AgentAutomation is a StandardOleMarshalObject).
        internal void StartScripted(string workbookName, string prompt, string provider, string model)
        {
            if (String.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Пустой промпт.");
            if (!String.IsNullOrWhiteSpace(provider) && String.IsNullOrWhiteSpace(model))
                throw new ArgumentException("Вместе с провайдером нужна модель.");
            var window = FindWorkbookWindow(workbookName);
            var entry = EnsurePane(window);
            entry.Pane.Visible = true;
            var run = new ScriptedRun();
            entry.Control.StartScripted(prompt, provider?.Trim(), model?.Trim(), run);
            scriptedRun = run;
            scriptedWindow = window.Hwnd;
            Diagnostics.Write("scripted_start", "window=" + scriptedWindow);
        }

        private ExcelInterop.Window FindWorkbookWindow(string workbookName)
        {
            var name = (workbookName ?? "").Trim();
            var books = Application.Workbooks;
            for (var index = 1; index <= books.Count && name.Length != 0; index++)
            {
                var book = books[index];
                if (String.Equals(book.Name, name, StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(book.FullName, name, StringComparison.OrdinalIgnoreCase)) return book.Windows[1];
            }
            throw new InvalidOperationException("Книга «" + name + "» не открыта в этом экземпляре Excel.");
        }

        internal string ScriptedStateJson()
        {
            return JsonConvert.SerializeObject(new
            {
                state = scriptedRun == null ? "idle" : scriptedRun.State,
                log_path = scriptedRun?.LogPath,
                error = scriptedRun?.Error
            });
        }

        internal void CancelScripted()
        {
            PaneEntry entry;
            if (scriptedRun != null && scriptedRun.State == "running" && panes.TryGetValue(scriptedWindow, out entry))
                entry.Control.Stop();
        }

        internal string InfoJson()
        {
            // Aperture.Runtime carries the built commit after "+" (see its csproj).
            var runtimeVersion = typeof(PiSession).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            var plus = runtimeVersion.IndexOf('+');
            return JsonConvert.SerializeObject(new
            {
                name = "Aperture2",
                version = typeof(ThisAddIn).Assembly.GetName().Version.ToString(3),
                commit = plus < 0 ? null : runtimeVersion.Substring(plus + 1),
                build = typeof(ThisAddIn).Assembly.ManifestModule.ModuleVersionId.ToString("N"),
                systemPromptSha256 = PiSession.SystemPromptSha256,
                repositoryRoot = PaneSettings.Load().RepositoryRoot,
                loaded,
                excelProcessId = Process.GetCurrentProcess().Id,
                excelInstanceId = instanceId,
                paneCount = panes.Values.Count(p => !p.Control.IsDisposed),
                panes = panes.Select(p =>
                {
                    var validationError = p.Value.Control.BindingValidationError();
                    return new { windowHwnd = p.Key, paneId = p.Value.PaneId, boundWorkbookId = p.Value.Control.BoundWorkbookId,
                        sessionRunning = p.Value.Control.SessionRunning, bindingValid = validationError == null,
                        bindingValidationError = validationError, viewReady = p.Value.Control.ViewReady,
                        viewInitializationError = p.Value.Control.ViewInitializationError };
                }).ToArray()
            });
        }

        private void ThisAddIn_Shutdown(object sender, EventArgs args)
        {
            loaded = false;
            if (paneCleanupTimer != null)
            {
                paneCleanupTimer.Stop();
                paneCleanupTimer.Tick -= PruneClosedPanes;
                paneCleanupTimer.Dispose();
                paneCleanupTimer = null;
            }
            Application.WindowActivate -= WindowActivated;
            foreach (var entry in panes.Values) { entry.Control.Dispose(); entry.Lifetime.Dispose(); }
            panes.Clear();
            Diagnostics.Write("addin_shutdown");
        }

        private void InternalStartup()
        {
            Startup += ThisAddIn_Startup;
            Shutdown += ThisAddIn_Shutdown;
        }

        private sealed class PaneEntry
        {
            internal Microsoft.Office.Tools.CustomTaskPane Pane;
            internal ChatPaneControl Control;
            internal WindowLifetime Lifetime;
            internal string PaneId;
        }
    }
}
