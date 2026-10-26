using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Aperture.Runtime;
using ExcelInterop = Microsoft.Office.Interop.Excel;

namespace Aperture.Excel
{
    internal sealed class WorkbookBinding
    {
        private readonly ExcelInterop.Workbook workbook;
        private readonly WindowLifetime windowLifetime;
        private readonly int uiThreadId;
        internal ExcelTarget Target { get; private set; }

        internal WorkbookBinding(ExcelInterop.Application application, ExcelInterop.Workbook workbook,
            ExcelInterop.Window window, string instanceId, WindowLifetime windowLifetime)
        {
            this.workbook = workbook;
            this.windowLifetime = windowLifetime;
            uiThreadId = Thread.CurrentThread.ManagedThreadId;
            Target = new ExcelTarget
            {
                ExcelInstanceId = instanceId,
                WorkbookId = Guid.NewGuid().ToString("N"),
                ExcelProcessId = Process.GetCurrentProcess().Id,
                ExcelHwnd = application.Hwnd,
                WorkbookHwnd = window.Hwnd,
                WorkbookName = workbook.Name,
                WorkbookFullName = workbook.FullName
            };
        }

        // The same captured book and window under a new name: the IDs stay, as the live book does.
        private WorkbookBinding(WorkbookBinding previous, string name, string fullName)
        {
            workbook = previous.workbook;
            windowLifetime = previous.windowLifetime;
            uiThreadId = previous.uiThreadId;
            var target = previous.Target;
            Target = new ExcelTarget
            {
                ExcelInstanceId = target.ExcelInstanceId,
                WorkbookId = target.WorkbookId,
                ExcelProcessId = target.ExcelProcessId,
                ExcelHwnd = target.ExcelHwnd,
                WorkbookHwnd = target.WorkbookHwnd,
                WorkbookName = name,
                WorkbookFullName = fullName
            };
        }

        internal void Validate()
        {
            RequireUiThread();
            if (!windowLifetime.IsCurrent)
                throw new InvalidOperationException("Окно книги этого чата закрыто. Откройте Aperture2 в окне нужной книги.");
            // Query the original captured Workbook, never re-resolve it through an
            // active book/name or compare IUnknown across Excel's changing proxies.
            if (!String.Equals(workbook.FullName, Target.WorkbookFullName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Имя или путь книги изменились во время запроса. Отправьте сообщение ещё раз: чат продолжится с книгой под новым именем.");
            if (!HasCapturedWindow())
                throw new InvalidOperationException("Окно книги этого чата закрыто. Откройте Aperture2 в окне нужной книги.");
        }

        /// <summary>
        /// After the first save or Save As the captured book keeps its object and window but gets a new
        /// name and path. Returns a binding of that same book with the same IDs and its new name, or this
        /// binding when nothing was renamed. A closed window or book is never followed: Validate reports it.
        /// </summary>
        internal WorkbookBinding FollowRename()
        {
            RequireUiThread();
            if (!windowLifetime.IsCurrent) return this;
            try
            {
                // Only the captured Workbook object itself, never a lookup by name or by the active window.
                var fullName = workbook.FullName;
                if (String.Equals(fullName, Target.WorkbookFullName, StringComparison.OrdinalIgnoreCase) || !HasCapturedWindow()) return this;
                return new WorkbookBinding(this, workbook.Name, fullName);
            }
            catch (COMException) { return this; }
            catch (InvalidComObjectException) { return this; }
        }

        private bool HasCapturedWindow()
        {
            var windows = workbook.Windows;
            for (var index = 1; index <= windows.Count; index++)
                if (windows[index].Hwnd == Target.WorkbookHwnd) return true;
            return false;
        }

        private void RequireUiThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != uiThreadId)
                throw new InvalidOperationException("Workbook validation must run on the Excel UI thread.");
        }
    }
}
