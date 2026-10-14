"""Technical integration smoke; owns only a new disposable Excel process/book.

This verifies add-in loading and the real Pi/MCP plumbing, not human UI acceptance.
No provider credentials are needed and no workbook is saved.
"""
from __future__ import annotations

import argparse
import gc
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import traceback
import uuid
import zipfile

import pythoncom
import win32api
import win32com.client
import win32event
import win32process
import winreg
import win32gui


ROOT = Path(__file__).resolve().parents[2]


def pump_for(seconds: float):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        pythoncom.PumpWaitingMessages()
        time.sleep(0.05)


def visible_windows_for_process(pid: int) -> list[int]:
    windows = []
    def collect(hwnd, _):
        if (win32process.GetWindowThreadProcessId(hwnd)[1] == pid
                and win32gui.IsWindowVisible(hwnd)):
            windows.append(hwnd)
    win32gui.EnumWindows(collect, None)
    return windows


def pane_snapshot(automation, expected_hwnds: set[int]) -> dict:
    status = json.loads(str(automation.StatusJson()))
    panes = {pane["windowHwnd"]: pane for pane in status["panes"]}
    assert status.get("loaded") is True, status
    assert status["paneCount"] == len(expected_hwnds), status
    assert set(panes) == expected_hwnds, status
    for pane in panes.values():
        assert pane.get("paneId") and pane.get("boundWorkbookId"), pane
        assert pane.get("bindingValid") is True, pane
        assert pane.get("bindingValidationError") is None, pane
    return status


def pane_identities(status: dict) -> dict:
    return {pane["windowHwnd"]: (pane["paneId"], pane["boundWorkbookId"])
            for pane in status["panes"]}


def verify_owned_process_exit(handle, pid: int, creation: str, quit_requested: bool,
                              zero_books_confirmed: bool) -> dict:
    """The retained handle cannot be redirected by PID reuse. Never kill by name."""
    exited = win32event.WaitForSingleObject(handle, 5000) == win32event.WAIT_OBJECT_0
    result = {"processId": pid, "creationTime": creation,
              "zeroBooksConfirmedBeforeQuit": zero_books_confirmed,
              "quitRequested": quit_requested, "normalExit": exited,
              "forcedTermination": False}
    if not exited and quit_requested and zero_books_confirmed:
        visible = visible_windows_for_process(pid)
        result["visibleWindowsAfterQuit"] = visible
        if not visible:
            # Only this test-created process, after successful empty-book Quit.
            # A ghost Office process may keep COM/VSTO references after UI shutdown.
            win32api.TerminateProcess(handle, 0)
            result["forcedTermination"] = True
            exited = win32event.WaitForSingleObject(handle, 5000) == win32event.WAIT_OBJECT_0
    result["exited"] = exited
    if exited:
        result["exitCode"] = win32process.GetExitCodeProcess(handle)
    return result


def launch_normal_excel(folder: Path):
    """Normal Excel startup loads VSTO; /automation startup omits user add-ins."""
    template = folder / "bootstrap.xlsx"
    parts = {
        "[Content_Types].xml": '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>',
        "_rels/.rels": '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>',
        "xl/workbook.xml": '<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Bootstrap" sheetId="1" r:id="rId1"/></sheets></workbook>',
        "xl/_rels/workbook.xml.rels": '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>',
        "xl/worksheets/sheet1.xml": '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData/></worksheet>',
    }
    with zipfile.ZipFile(template, "w", zipfile.ZIP_DEFLATED) as archive:
        for name, content in parts.items():
            archive.writestr(name, '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' + content)
    with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\excel.exe") as key:
        executable = winreg.QueryValue(key, None)
    process = subprocess.Popen([executable, "/x", str(template)])
    sys.path.insert(0, str(ROOT / "src"))
    from aperture_mcp.binding import _from_window
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        windows = []
        def collect(hwnd, _):
            if win32gui.GetClassName(hwnd) == "XLMAIN" and win32process.GetWindowThreadProcessId(hwnd)[1] == process.pid:
                windows.append(hwnd)
        win32gui.EnumWindows(collect, None)
        for hwnd in windows:
            try:
                application = _from_window(hwnd)
                if application is not None and application.Workbooks.Count:
                    # The bootstrap file is ours; keep test edits in a fresh unsaved workbook.
                    return application, process, template.name
            except Exception:
                pass
        if process.poll() is not None:
            raise RuntimeError(f"Normal Excel process exited during startup: {process.returncode}")
        pythoncom.PumpWaitingMessages()
        time.sleep(0.1)
    # Never kill an interactive Office process after a failed attachment. Leave it for inspection.
    raise RuntimeError(f"Could not attach to our normal Excel startup (PID {process.pid}); inspect its window.")


def run_child(arguments: list[str], timeout: float = 100) -> dict:
    env = os.environ.copy()
    env["PYTHONPATH"] = str(ROOT / "src")
    env["PYTHONIOENCODING"] = "utf-8"
    # Output is small and collected with a helper thread to avoid pipe deadlock.
    import threading
    process = subprocess.Popen(arguments, cwd=ROOT, env=env, stdout=subprocess.PIPE,
                               stderr=subprocess.PIPE, text=True, encoding="utf-8", errors="replace")
    captured: list[tuple[str, str]] = []
    reader = threading.Thread(target=lambda: captured.append(process.communicate()), daemon=True)
    reader.start()
    deadline = time.monotonic() + timeout
    while reader.is_alive():
        pythoncom.PumpWaitingMessages()
        if time.monotonic() >= deadline:
            process.kill()  # only the test child we created; never terminate Excel by name
            reader.join(5)
            raise TimeoutError(f"Technical smoke child exceeded {timeout}s: {Path(arguments[0]).name}")
        reader.join(0.05)
    stdout, stderr = captured[0]
    result = {"exitCode": process.returncode, "stdout": stdout.strip(), "stderr": stderr.strip()}
    if process.returncode:
        raise RuntimeError(json.dumps(result, ensure_ascii=False))
    return result


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--skip-pi", action="store_true", help="Run only VSTO and real Excel MCP checks")
    parser.add_argument("--skip-vsto", action="store_true", help="Debug backend checks without add-in registration")
    args = parser.parse_args()
    output_dir = ROOT / ".local" / "smoke" / uuid.uuid4().hex
    output_dir.mkdir(parents=True)
    report: dict = {"runId": output_dir.name, "checks": {}}
    application = workbook = control_workbook = chart = addin = automation = None
    owned_handle = None
    quit_requested = zero_books_confirmed = False
    owned_creation = None
    stable_panes = None
    owned_process = False
    before_pids = set(win32process.EnumProcesses())
    pythoncom.CoInitialize()
    try:
        if args.skip_vsto:
            application = win32com.client.DispatchEx("Excel.Application")
            bootstrap_name = None
        else:
            application, excel_process, bootstrap_name = launch_normal_excel(output_dir)
        pid = win32process.GetWindowThreadProcessId(int(application.Hwnd))[1]
        if pid in before_pids:
            raise RuntimeError("Excel activation did not create a new process; refusing to use an existing one.")
        # Capture a process handle while we have proven ownership. Every later
        # wait/termination uses this handle, not a fresh PID lookup.
        owned_handle = win32api.OpenProcess(0x101001, False, pid)  # SYNCHRONIZE | QUERY_LIMITED_INFORMATION | TERMINATE
        owned_creation = str(win32process.GetProcessTimes(owned_handle)["CreationTime"])
        owned_process = True
        application.Visible = True
        application.DisplayAlerts = False
        workbook = application.Workbooks.Add()
        workbook.Worksheets(1).Name = "ApertureSmoke"
        # Regression: a chart sheet is a Sheet but has no ListObjects. The
        # required MCP overview must still work for this perfectly valid book.
        chart = workbook.Charts.Add(After=workbook.Sheets(workbook.Sheets.Count))
        chart.Name = "ApertureChart"
        workbook.Worksheets(1).Activate()
        if bootstrap_name:
            application.Workbooks(bootstrap_name).Close(SaveChanges=False)
        target = {
            "excelInstanceId": f"excel-{pid}-{int(application.Hwnd)}",
            "workbookId": uuid.uuid4().hex,
            "excelProcessId": pid,
            "excelHwnd": int(application.Hwnd),
            "workbookName": str(workbook.Name),
            "workbookFullName": str(workbook.FullName),
            "workbookHwnd": int(workbook.Windows(1).Hwnd),
        }
        target_path = output_dir / "target.json"
        target_path.write_text(json.dumps(target, ensure_ascii=False, indent=2), encoding="utf-8")
        report["checks"]["disposableExcel"] = {"processId": pid, "unsaved": str(workbook.Path) == ""}

        if not args.skip_vsto:
            application.COMAddIns.Update()
            report["checks"]["registeredAddins"] = [str(application.COMAddIns.Item(i).ProgId)
                                                    for i in range(1, application.COMAddIns.Count + 1)]
            addin = application.COMAddIns.Item("Aperture.Excel")
            addin.Connect = True
            if not addin.Connect:
                raise RuntimeError("Aperture.Excel COMAddIn did not connect.")
            automation = addin.Object
            if automation is None:
                raise RuntimeError("VSTO loaded without its diagnostics automation service.")
            automation.ShowPane()
            status = json.loads(str(automation.StatusJson()))
            report["checks"]["vsto"] = status
            assert status.get("loaded") is True, status
            assert status.get("paneCount", 0) >= 1, status
            assert status.get("excelProcessId") == pid, status
            print("VSTO connected and task pane created.", flush=True)

        # The captured workbook must not follow ActiveWorkbook. Both books and
        # their entire process belong to this test, so no user workbook is used.
        control_workbook = application.Workbooks.Add()
        control_sheet = control_workbook.Worksheets(1)
        control_sheet.Name = "ApertureControl"
        control_sheet.Range("A1").Value2 = 987654
        control_sheet.Range("B1").Value2 = "must stay untouched"
        control_sheet.Range("A2").Formula2 = "=A1+1"
        control_sheet.Calculate()
        control_values = control_sheet.Range("A1:B4").Value2
        control_formulas = control_sheet.Range("A1:B4").Formula2
        control_workbook.Activate()
        control_hwnd = int(control_workbook.Windows(1).Hwnd)
        assert str(application.ActiveWorkbook.Name) == str(control_workbook.Name)
        assert control_hwnd != target["workbookHwnd"]

        if not args.skip_vsto:
            # Let the already-closed bootstrap pane be swept, then observe three
            # full 1.5-second cleanup ticks. Recreating panes silently loses chat.
            pump_for(1.8)
            expected_hwnds = {target["workbookHwnd"], control_hwnd}
            snapshots = [pane_snapshot(automation, expected_hwnds)]
            stable_panes = pane_identities(snapshots[0])
            for _ in range(3):
                workbook.Activate()
                control_workbook.Activate()
                pump_for(1.6)
                snapshot = pane_snapshot(automation, expected_hwnds)
                assert pane_identities(snapshot) == stable_panes, snapshot
                snapshots.append(snapshot)
            report["checks"]["vstoPaneStability"] = {
                "cleanupTicksObserved": 3, "snapshots": snapshots,
                "paneAndWorkbookIdsStable": True, "allBindingsValid": True,
            }

        report["checks"]["excelMcp"] = run_child([
            sys.executable, str(ROOT / "tools" / "ExcelMcpSmoke" / "smoke.py"),
            "--context", str(target_path), "--write-smoke",
        ])
        mcp_result = json.loads(report["checks"]["excelMcp"]["stdout"])
        assert "ApertureChart" in mcp_result["target"]["workbook"]["sheets"], mcp_result["target"]
        assert "ApertureSmoke" in [sheet["name"] for sheet in mcp_result["target"]["worksheets"]]
        assert "ApertureChart" not in [sheet["name"] for sheet in mcp_result["target"]["worksheets"]]
        assert workbook.Worksheets("ApertureSmoke").Range("B1").Value2 == 42
        assert control_sheet.Range("A1:B4").Value2 == control_values
        assert control_sheet.Range("A1:B4").Formula2 == control_formulas
        assert str(application.ActiveWorkbook.Name) == str(control_workbook.Name)
        report["checks"]["activeWorkbookIsolation"] = {
            "capturedWorkbook": target["workbookName"],
            "activeWorkbook": str(control_workbook.Name),
            "targetResult": 42,
            "controlValuesAndFormulasUnchanged": True,
            "activeWorkbookUnchanged": True,
        }
        report["checks"]["chartSheetOverview"] = True
        control_sheet = None
        print("Live Excel MCP read/write/calculate/readback passed.", flush=True)
        if not args.skip_pi:
            executable = ROOT / "tools" / "PiSmoke" / "bin" / "Debug" / "net48" / "PiSmoke.exe"
            if not executable.is_file():
                raise FileNotFoundError(f"Build PiSmoke first: {executable}")
            report["checks"]["piRpc"] = run_child([
                str(executable), "--root", str(ROOT), "--context", str(target_path),
            ])
            print("Pi RPC and MCP initialization passed.", flush=True)
        if not args.skip_vsto:
            control_workbook.Close(SaveChanges=False)
            control_workbook = None
            pump_for(1.8)
            after_close = pane_snapshot(automation, {target["workbookHwnd"]})
            assert pane_identities(after_close)[target["workbookHwnd"]] == stable_panes[target["workbookHwnd"]]
            report["checks"]["closedWorkbookPaneCleanup"] = {
                "closedWindowHwnd": control_hwnd, "remainingPaneStable": True,
                "status": after_close,
            }
        report["success"] = True
    except Exception as error:
        report["success"] = False
        report["error"] = str(error)
        report["traceback"] = traceback.format_exc()
    finally:
        cleanup_errors = []
        automation = addin = chart = None
        control_sheet = None
        if owned_process:
            if control_workbook is not None:
                try:
                    control_workbook.Close(SaveChanges=False)
                except Exception as error:
                    cleanup_errors.append(f"Could not close our control book: {error}")
                control_workbook = None
            if workbook is not None:
                try:
                    workbook.Close(SaveChanges=False)
                except Exception as error:
                    cleanup_errors.append(f"Could not close our test book: {error}")
                workbook = None
            if application is not None:
                try:
                    if application.Workbooks.Count == 0:
                        zero_books_confirmed = True
                        application.Quit()
                        quit_requested = True
                    else:
                        cleanup_errors.append("Excel now contains other books; it was left open.")
                except Exception as error:
                    cleanup_errors.append(f"Could not stop our Excel process: {error}")
        application = None
        gc.collect()
        pythoncom.CoUninitialize()
        if owned_handle is not None:
            try:
                exit_status = verify_owned_process_exit(owned_handle, pid, owned_creation,
                                                        quit_requested, zero_books_confirmed)
                report["checks"]["ownedExcelProcessExit"] = exit_status
                if not exit_status["exited"]:
                    cleanup_errors.append("Owned Excel process did not exit; fallback was not safe or did not complete.")
                elif exit_status["forcedTermination"]:
                    report["forcedCleanupNote"] = "The empty test-created Excel process required exact-handle termination after Quit and COM release; normal process exit was not proved."
            except Exception as error:
                cleanup_errors.append(f"Could not verify our Excel process exit: {error}")
            finally:
                owned_handle.Close()
        if cleanup_errors:
            report["cleanupNotes"] = cleanup_errors
            report["success"] = False
        report_path = output_dir / "report.json"
        report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2))
    print(f"Report: {report_path}")
    return 0 if report["success"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
