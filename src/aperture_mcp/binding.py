"""Immutable target selection and a single STA owner for all Excel COM calls.

Domain operations are imported from the pinned ThepExcelMCP submodule. This
module deliberately replaces its auto-launch and active-workbook routing.
"""
from __future__ import annotations

import ctypes
import gc
import json
import queue
import threading
import uuid
from concurrent.futures import Future, TimeoutError
from dataclasses import dataclass
from pathlib import Path

import pythoncom
import win32com.client
import win32gui
import win32process
from fastmcp.exceptions import ToolError
from thepexcel_mcp.session import ExcelSession


@dataclass(frozen=True)
class Target:
    excelInstanceId: str
    workbookId: str
    excelProcessId: int
    excelHwnd: int
    workbookName: str
    workbookFullName: str
    workbookHwnd: int

    @classmethod
    def load(cls, path: str) -> "Target":
        data = json.loads(Path(path).read_text(encoding="utf-8-sig"))
        target = cls(**{key: data[key] for key in cls.__dataclass_fields__})
        if not all(isinstance(getattr(target, key), str) and getattr(target, key)
                   for key in ("excelInstanceId", "workbookId", "workbookName", "workbookFullName")):
            raise ValueError("Target IDs and workbook names must be non-empty strings")
        if not all(type(getattr(target, key)) is int and getattr(target, key) > 0
                   for key in ("excelProcessId", "excelHwnd", "workbookHwnd")):
            raise ValueError("Target PID and window handles must be positive integers")
        return target


def _without_frames(exc: BaseException) -> BaseException:
    """Drop traceback frames of an exception chain: their locals can hold Excel COM objects."""
    pending, seen = [exc], set()
    while pending:
        current = pending.pop()
        if current is None or id(current) in seen:
            continue
        seen.add(id(current))
        current.__traceback__ = None
        pending += [current.__cause__, current.__context__]
    return exc


def window_pid(hwnd: int) -> int:
    if not win32gui.IsWindow(hwnd):
        raise ToolError("Captured Excel window no longer exists. Start a new chat binding.")
    return win32process.GetWindowThreadProcessId(hwnd)[1]


def workbook_matches(wb, target: Target) -> bool:
    """Names alone cannot identify an unsaved book or a reopened replacement."""
    return (
        str(wb.Name).casefold() == target.workbookName.casefold()
        and str(wb.FullName).casefold() == target.workbookFullName.casefold()
        and any(int(wb.Windows(i).Hwnd) == target.workbookHwnd
                for i in range(1, wb.Windows.Count + 1))
    )


def _from_window(hwnd: int):
    """Attach through Excel's native object model without activating a COM server.

    Excel exposes the Window object on its EXCEL7 child via OBJID_NATIVEOM.
    GetActiveObject can only see the first registered instance, so this fallback
    matters for unsaved books in a second Excel process.
    """
    children = []
    win32gui.EnumChildWindows(hwnd, lambda child, _: children.append(child), None)
    iid = (ctypes.c_ubyte * 16).from_buffer_copy(uuid.UUID(str(pythoncom.IID_IDispatch)).bytes_le)
    oleacc = ctypes.OleDLL("oleacc")
    access = oleacc.AccessibleObjectFromWindow
    access.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p)]
    access.restype = ctypes.c_long
    for child in children:
        if win32gui.GetClassName(child) != "EXCEL7":
            continue
        pointer = ctypes.c_void_p()
        if access(child, 0xFFFFFFF0, ctypes.byref(iid), ctypes.byref(pointer)) != 0 or not pointer.value:
            continue
        # ObjectFromAddress AddRefs the IDispatch. Balance oleacc's original ref.
        dispatch = pythoncom.ObjectFromAddress(pointer.value, pythoncom.IID_IDispatch)
        vtable = ctypes.cast(pointer, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
        release = ctypes.WINFUNCTYPE(ctypes.c_ulong, ctypes.c_void_p)(vtable[2])
        release(pointer)
        return win32com.client.Dispatch(dispatch).Application
    return None


def attach_application(target: Target):
    if window_pid(target.excelHwnd) != target.excelProcessId:
        raise ToolError("Captured Excel window belongs to a different process.")
    if window_pid(target.workbookHwnd) != target.excelProcessId:
        raise ToolError("Captured workbook window belongs to a different process.")
    candidates = []
    try:
        candidates.append(win32com.client.GetActiveObject("Excel.Application"))
    except Exception:
        pass
    for candidate in candidates:
        try:
            if window_pid(int(candidate.Hwnd)) == target.excelProcessId:
                return candidate
        except Exception:
            pass
    try:
        candidate = _from_window(target.workbookHwnd)
        if candidate is not None and window_pid(int(candidate.Hwnd)) == target.excelProcessId:
            return candidate
    except Exception as exc:
        raise ToolError(f"Cannot attach to the captured Excel window: {exc}") from exc
    # ROT is an additional read-only attachment path; never open a disk file.
    rot = pythoncom.GetRunningObjectTable()
    for moniker in rot.EnumRunning():
        try:
            obj = win32com.client.Dispatch(rot.GetObject(moniker).QueryInterface(pythoncom.IID_IDispatch))
            app = obj.Application
            if window_pid(int(app.Hwnd)) == target.excelProcessId:
                return app
        except Exception:
            continue
    raise ToolError("Cannot attach to the specified live Excel instance. No new Excel process was started.")


class BoundSession(ExcelSession):
    def __init__(self, target: Target, timeout: float = 30):
        self.target = target
        self.timeout = timeout
        self._app = None
        self._workbook = None
        self._queue = queue.Queue()
        self._gate = threading.Lock()
        self._pending = None
        self._uncertain_write = False
        self._worker = threading.Thread(target=self._work, name="aperture-excel-sta", daemon=True)
        self._worker.start()

    def _work(self):
        pythoncom.CoInitialize()
        try:
            while True:
                future, fn, mutation = self._queue.get()
                result = error = None
                try:
                    self.get_workbook()
                    # Curated mutations are already bounded bulk operations.
                    # Never change global Application flags here: this process
                    # can be killed by the parent job before a finally runs.
                    result = fn()
                except Exception as exc:
                    error = _without_frames(exc)
                finally:
                    # Release before reporting, so no reference outlives a returned result.
                    del fn
                    self._release_excel()
                if error is None:
                    future.set_result(result)
                else:
                    future.set_exception(error)
                result = error = None
        finally:
            pythoncom.CoUninitialize()

    def _release_excel(self):
        """Hold no Excel COM references between operations.

        The parent job kills this process when the chat or Excel closes. References
        of a killed client stay registered in Excel, which then keeps running hidden
        after the user closes it. Identity is re-checked on the next operation.
        """
        self._workbook = None
        self._app = None
        gc.collect()

    def call(self, fn, *, mutation=False):
        with self._gate:
            if self._pending is not None:
                if not self._pending.done() or self._uncertain_write:
                    raise ToolError("Previous Excel operation is unresolved. Call excel_operation_status; do not retry a write.")
                self._pending = None
            future = Future()
            self._pending = future
            self._queue.put((future, fn, mutation))
            try:
                result = future.result(timeout=self.timeout)
                self._pending = None
                return result
            except TimeoutError as exc:
                self._uncertain_write = mutation
                raise ToolError("Excel operation timed out; it may still complete. No retry was made. Call excel_operation_status before further work.") from exc
            except Exception as exc:
                self._pending = None
                if mutation:
                    raise ToolError(f"Excel write failed and may have partially applied: {exc}. Read back affected cells before retrying; rollback is not guaranteed.") from exc
                raise

    def operation_status(self):
        with self._gate:
            if self._pending is None:
                return {"status": "idle"}
            if not self._pending.done():
                return {"status": "running", "writeOutcomeUnknown": self._uncertain_write}
            future = self._pending
            self._pending = None
            self._uncertain_write = False
            try:
                return {"status": "completed", "result": future.result(), "instruction": "Read back the affected range before deciding whether another write is needed."}
            except Exception as exc:
                return {"status": "failed", "error": str(exc), "instruction": "An error does not guarantee rollback. Read back the affected range; do not blindly retry."}

    def run_com(self, fn, *args, **kwargs):
        if threading.current_thread() is self._worker:
            return fn(*args, **kwargs)
        return self.call(lambda: fn(*args, **kwargs))

    def get_app(self):
        if self._app is None:
            self._app = attach_application(self.target)
        if window_pid(self.target.excelHwnd) != self.target.excelProcessId:
            raise ToolError("Excel binding expired. Start a new chat.")
        if window_pid(int(self._app.Hwnd)) != self.target.excelProcessId:
            raise ToolError("Excel application identity changed.")
        return self._app

    def get_workbook(self, workbook=None):
        if workbook is not None and workbook != self.target.workbookId:
            raise ToolError("Only the workbookId captured for this chat is accepted.")
        app = self.get_app()
        if self._workbook is None:
            # Resolved per operation by name, full path and the captured window handle:
            # a closed/reopened book gets a new window and a Save As changes the path,
            # so neither is ever accepted as a replacement.
            try:
                matches = [app.Workbooks(i) for i in range(1, app.Workbooks.Count + 1)
                           if workbook_matches(app.Workbooks(i), self.target)]
            except Exception as exc:
                raise ToolError("Workbook binding expired. Start a new chat; no replacement was selected.") from exc
            if len(matches) != 1:
                raise ToolError("Captured workbook is not uniquely open under its captured name, path and window: "
                                "it was closed or renamed (for example by Save As). No other workbook was selected. "
                                "Tell the user; after a rename the sidebar rebinds the same book on their next message.")
            self._workbook = matches[0]
        return self._workbook

    def get_sheet(self, name, workbook=None):
        if not name:
            raise ToolError("An explicit worksheet name is required.")
        return super().get_sheet(name, workbook)
