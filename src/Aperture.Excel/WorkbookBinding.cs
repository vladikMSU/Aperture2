using System;
using System.Diagnostics;
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

        internal void Validate()
        {
            if (Thread.CurrentThread.ManagedThreadId != uiThreadId)
                throw new InvalidOperationException("Workbook validation must run on the Excel UI thread.");

            if (!windowLifetime.IsCurrent)
                throw new InvalidOperationException("Окно привязанной книги закрыто. Повторно привяжите книгу.");
            // Query the original captured Workbook, never re-resolve it through an
            // active book/name or compare IUnknown across Excel's changing proxies.
            if (!String.Equals(workbook.FullName, Target.WorkbookFullName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Имя или путь книги изменились. Повторно привяжите книгу для новой сессии.");
            var windows = workbook.Windows;
            var matchingWindow = false;
            for (var index = 1; index <= windows.Count; index++)
                if (windows[index].Hwnd == Target.WorkbookHwnd) matchingWindow = true;
            if (!matchingWindow)
                throw new InvalidOperationException("Окно привязанной книги закрыто. Повторно привяжите книгу.");
        }

    }
}
