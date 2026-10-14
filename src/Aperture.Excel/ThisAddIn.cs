using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using ExcelInterop = Microsoft.Office.Interop.Excel;
using Office = Microsoft.Office.Core;

namespace Aperture.Excel
{
    public partial class ThisAddIn
    {
        private readonly string instanceId = Guid.NewGuid().ToString("N");
        private readonly Dictionary<int, PaneEntry> panes = new Dictionary<int, PaneEntry>();
        private AddInAutomation automation;
        private bool loaded;
        private int? runningWindow;
        private System.Windows.Forms.Timer paneCleanupTimer;

        private void ThisAddIn_Startup(object sender, EventArgs args)
        {
            Application.WindowActivate += WindowActivated;
            loaded = true;
            paneCleanupTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            paneCleanupTimer.Tick += PruneClosedPanes;
            paneCleanupTimer.Start();
            Diagnostics.Write("addin_loaded", "version=0.1.0");
            try { EnsureActivePane(false); }
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
                var entry = EnsureActivePane(false);
                if (entry != null) entry.Pane.Visible = !entry.Pane.Visible;
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show(ex.Message, "Aperture2", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
            }
        }

        internal void ShowPane()
        {
            EnsureActivePane(true);
        }

        private PaneEntry EnsureActivePane(bool show)
        {
            var window = Application.ActiveWindow;
            if (window == null)
            {
                if (show) throw new InvalidOperationException("Откройте книгу Excel, чтобы показать панель Aperture2.");
                return null;
            }
            var entry = EnsurePane(window);
            if (show) entry.Pane.Visible = true;
            return entry;
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
            return automation ?? (automation = new AddInAutomation(this));
        }

        internal string StatusJson()
        {
            return JsonConvert.SerializeObject(new
            {
                loaded,
                version = "0.1.0",
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

    [ComVisible(true)]
    [Guid("5A264C27-63D5-4ECC-934A-32B3471B0B6E")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface IAddInAutomation
    {
        string StatusJson();
        void ShowPane();
    }

    [ComVisible(true)]
    [Guid("168017DD-4C4A-48A8-9B3C-C1461F0D5983")]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class AddInAutomation : StandardOleMarshalObject, IAddInAutomation
    {
        private readonly ThisAddIn addIn;
        internal AddInAutomation(ThisAddIn addIn) { this.addIn = addIn; }
        public string StatusJson() { return addIn.StatusJson(); }
        public void ShowPane() { addIn.ShowPane(); }
    }
}
