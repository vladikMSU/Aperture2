"""Capture target.json for a workbook that is already open in one live Excel process.

Attaches only to the Excel process with the given PID, through its own windows
(OBJID_NATIVEOM) or, as a read-only fallback, the running object table. Never
starts Excel, never opens a file from disk, never activates, saves or closes a
book. No COM reference outlives the call.

    python -m aperture_mcp.make_target --pid <EXCEL pid> --workbook <full path> --out target.json
"""
from __future__ import annotations

import argparse
import gc
import json
import os
import sys
import time
import uuid
from dataclasses import asdict
from pathlib import Path

import pythoncom
import pywintypes
import win32com.client
import win32gui
import win32process

from .binding import Target, _from_window, window_pid, workbook_matches


def excel_main_windows(pid: int) -> list[int]:
    windows = []

    def collect(hwnd, _):
        if win32gui.GetClassName(hwnd) == "XLMAIN" and win32process.GetWindowThreadProcessId(hwnd)[1] == pid:
            windows.append(hwnd)
    win32gui.EnumWindows(collect, None)
    return windows


def application_for_process(pid: int):
    """The Application object of exactly this Excel process, or LookupError."""
    for hwnd in excel_main_windows(pid):
        try:
            application = _from_window(hwnd)
            if application is not None and window_pid(int(application.Hwnd)) == pid:
                return application
        except Exception:
            continue
    # Same read-only fallback as binding.attach_application, filtered by PID.
    rot = pythoncom.GetRunningObjectTable()
    for moniker in rot.EnumRunning():
        try:
            obj = win32com.client.Dispatch(rot.GetObject(moniker).QueryInterface(pythoncom.IID_IDispatch))
            application = obj.Application
            if window_pid(int(application.Hwnd)) == pid:
                return application
        except Exception:
            continue
    raise LookupError(f"No attachable Excel application in process {pid}. No Excel was started.")


def _same_path(left: str, right: str) -> bool:
    return os.path.normcase(os.path.normpath(left)) == os.path.normcase(os.path.normpath(right))


def find_workbook(application, workbook: str):
    """A full path matches FullName; a bare name matches only an unsaved book (FullName == Name)."""
    by_path = os.sep in workbook or (os.altsep or os.sep) in workbook
    if by_path:
        workbook = os.path.abspath(workbook)
    books = [application.Workbooks(i) for i in range(1, application.Workbooks.Count + 1)]
    matches = [book for book in books
               if (_same_path(str(book.FullName), workbook) if by_path
                   else str(book.FullName).casefold() == workbook.casefold())]
    if len(matches) != 1:
        open_books = [str(book.FullName) for book in books]
        raise LookupError(f"Workbook {workbook!r} is open {len(matches)} times in this Excel process; "
                          f"open books: {open_books}. Pass the full path of an already open book.")
    return matches[0]


def build_target(application, workbook) -> dict:
    """Fields of binding.Target with fresh identifiers for this run."""
    excel_hwnd = int(application.Hwnd)
    return {
        "excelInstanceId": uuid.uuid4().hex,
        "workbookId": uuid.uuid4().hex,
        "excelProcessId": window_pid(excel_hwnd),
        "excelHwnd": excel_hwnd,
        "workbookName": str(workbook.Name),
        "workbookFullName": str(workbook.FullName),
        "workbookHwnd": int(workbook.Windows(1).Hwnd),
    }


def make_target(pid: int, workbook: str, timeout: float = 15.0) -> dict:
    """Retries while Excel is busy (for example right after a book was opened)."""
    deadline = time.monotonic() + timeout
    pythoncom.CoInitialize()
    application = book = None
    try:
        while True:
            try:
                application = application_for_process(pid)
                book = find_workbook(application, workbook)
                data = build_target(application, book)
                target = Target(**data)
                if target.excelProcessId != pid or window_pid(target.workbookHwnd) != pid:
                    raise LookupError("The workbook window does not belong to the requested Excel process.")
                if not workbook_matches(book, target):
                    raise LookupError("The captured workbook identity could not be confirmed.")
                return asdict(target)
            except (LookupError, pywintypes.com_error) as error:
                application = book = None
                if time.monotonic() >= deadline:
                    raise LookupError(str(error)) from None
                pythoncom.PumpWaitingMessages()
                time.sleep(0.25)
    finally:
        application = book = None
        gc.collect()
        pythoncom.CoUninitialize()


def main() -> int:
    parser = argparse.ArgumentParser(description="Write target.json for a book already open in a live Excel process")
    parser.add_argument("--pid", type=int, required=True, help="PID of the EXCEL.EXE that has the book open")
    parser.add_argument("--workbook", required=True, help="Full path of the open book (a bare name only for an unsaved book)")
    parser.add_argument("--out", required=True, help="Where to write target.json")
    parser.add_argument("--timeout-s", type=float, default=15.0, help="How long to wait for a busy Excel")
    args = parser.parse_args()
    try:
        target = make_target(args.pid, args.workbook, args.timeout_s)
    except LookupError as error:
        print(str(error), file=sys.stderr)
        return 1
    out = Path(args.out).resolve()
    out.parent.mkdir(parents=True, exist_ok=True)
    temporary = out.with_name(out.name + ".tmp")
    temporary.write_text(json.dumps(target, ensure_ascii=False, indent=2), encoding="utf-8")
    os.replace(temporary, out)
    print(json.dumps(target, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
