"""Smoke of the add-in's COM automation (GetInfo/Start/GetState/Cancel); owns only its own Excels.

Two visible Excel processes open a small book saved under .local/smoke, as a script would:
started with DispatchEx (default, like the benchmark runner) or, with --launch normal, as
"excel /x <book>". On this machine Excel started through COM loaded the add-in build of the main
checkout regardless of the registered manifest, while "excel /x" loads the registered one, so a
worktree build is checked with --launch normal. No pane opens by itself: Excel B opens its pane
with Start and an immediate Cancel, which stops the startup of Pi before the prompt is sent, and
keeps it open to check that two panes share the WebView2 profile. Excel A runs a real model request
through Start on a window whose pane was never opened, then a second request stopped with Cancel.
The provider is paid: prompts stay short.
Books are closed without saving, both Excels quit, and the smoke checks that no Pi/MCP process
of these Excels remains. Other Excel processes are never touched. Report: .local/smoke/automation-*.
"""
from __future__ import annotations

import argparse
import gc
import json
from pathlib import Path
import subprocess
import sys
import time
import traceback
import uuid
import winreg

import pythoncom
import pywintypes
import win32api
import win32com.client
import win32event
import win32gui
import win32process


ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src"))
from aperture_mcp.binding import _from_window  # noqa: E402
PROMPT = "добавь лист Тест и запиши в A1 слово тест"
# Long enough to be stopped while Pi still works; Cancel comes after its first tool call.
CANCEL_PROMPT = ("Прочитай ячейки A1:A3 листа Данные, каждую отдельным вызовом инструмента, "
                 "а потом напиши подробный разбор каждой ячейки на 1500 слов.")
SYNCHRONIZE_QUERY_TERMINATE = 0x101001


def pump(seconds: float):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        pythoncom.PumpWaitingMessages()
        time.sleep(0.05)


def processes() -> dict[int, dict]:
    wmi = win32com.client.GetObject(r"winmgmts:root\cimv2")
    result = {}
    for item in wmi.ExecQuery("SELECT ProcessId, ParentProcessId, Name, CreationDate FROM Win32_Process"):
        result[int(item.ProcessId)] = {"ppid": int(item.ParentProcessId), "name": str(item.Name).lower(),
                                       "created": str(item.CreationDate)}
    return result


def descendants(roots: set[int]) -> dict[int, dict]:
    """Processes started under our Excels. A child is newer than its parent, which rules out PID reuse."""
    table = processes()
    found = {pid: table[pid] for pid in roots if pid in table}
    changed = True
    while changed:
        changed = False
        for pid, item in table.items():
            parent = found.get(item["ppid"])
            if pid not in found and parent and item["created"] >= parent["created"]:
                found[pid] = item
                changed = True
    return {pid: item for pid, item in found.items() if pid not in roots}


SEEN: dict[int, tuple[str, str]] = {}  # every Pi/MCP process observed under our Excels: pid -> (name, created)


def agent_processes(roots: set[int]) -> dict[int, str]:
    found = {pid: item for pid, item in descendants(roots).items() if item["name"] in ("node.exe", "python.exe")}
    SEEN.update({pid: (item["name"], item["created"]) for pid, item in found.items()})
    return {pid: item["name"] for pid, item in found.items()}


def still_alive(pids) -> dict[int, str]:
    """Seen Pi/MCP processes that still exist; an orphan keeps its PID and creation time."""
    table = processes()
    return {pid: SEEN[pid][0] for pid in pids if pid in table and table[pid]["created"] == SEEN[pid][1]}


def foreign_pane_browsers(roots: set[int]) -> list[dict]:
    """WebView2 browsers of the shared Aperture2 profile started by other Excels, with their extra switches.
    Panes of Excels whose WebView2 options differ cannot share one browser (0x8007139F)."""
    wmi = win32com.client.GetObject(r"winmgmts:root\cimv2")
    found = []
    for item in wmi.ExecQuery("SELECT ProcessId, ParentProcessId, CommandLine FROM Win32_Process WHERE Name = 'msedgewebview2.exe'"):
        line = str(item.CommandLine or "")
        if "--type=" in line or r"\aperture2\webview2" not in line.lower() or int(item.ParentProcessId) in roots:
            continue
        switches = [part for part in line.split() if part.startswith("--remote-debugging") or part.startswith("--auto-open")]
        found.append({"processId": int(item.ProcessId), "excelProcessId": int(item.ParentProcessId), "switches": switches})
    return found


def dialogs(pid: int) -> list[str]:
    found = []
    def collect(hwnd, _):
        if (win32gui.IsWindowVisible(hwnd) and win32gui.GetClassName(hwnd) == "#32770"
                and win32process.GetWindowThreadProcessId(hwnd)[1] == pid):
            found.append(win32gui.GetWindowText(hwnd))
    win32gui.EnumWindows(collect, None)
    return found


def com_error_text(error: pywintypes.com_error) -> str:
    info = error.excepinfo
    return str(info[2]) if info and info[2] else str(error)


def make_books(paths: list[Path], before: set[int]) -> dict:
    """Small books saved by a short-lived Excel of our own, then opened from disk like a runner's copy."""
    app = win32com.client.DispatchEx("Excel.Application")
    pid = win32process.GetWindowThreadProcessId(int(app.Hwnd))[1]
    if pid in before:
        raise RuntimeError("DispatchEx returned an existing Excel process; refusing to use it.")
    handle = win32api.OpenProcess(SYNCHRONIZE_QUERY_TERMINATE, False, pid)
    app.DisplayAlerts = False
    for path in paths:
        book = app.Workbooks.Add()
        sheet = book.Worksheets(1)
        sheet.Name = "Данные"
        sheet.Range("A1:B3").Value = (("Выручка", 120), ("Затраты", 80), ("Прибыль", None))
        sheet.Range("B3").Formula = "=B1-B2"
        book.SaveAs(str(path), FileFormat=51)
        book.Close(SaveChanges=False)
    sheet = book = None
    app.Quit()
    app = None
    gc.collect()
    exited = win32event.WaitForSingleObject(handle, 30000) == win32event.WAIT_OBJECT_0
    return {"processId": pid, "normalExit": exited}


def launch_normal(path: Path, before: set[int]):
    """excel /x <book>: a separate interactive Excel that loads add-ins from their registration."""
    with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\excel.exe") as key:
        executable = winreg.QueryValue(key, None)
    process = subprocess.Popen([executable, "/x", str(path)])
    if process.pid in before:
        raise RuntimeError("Excel reused an existing process; refusing to use it.")
    deadline = time.monotonic() + 45
    while time.monotonic() < deadline:
        windows = []
        win32gui.EnumWindows(lambda hwnd, _: windows.append(hwnd) if win32gui.GetClassName(hwnd) == "XLMAIN"
                             and win32process.GetWindowThreadProcessId(hwnd)[1] == process.pid else None, None)
        for hwnd in windows:
            try:
                app = _from_window(hwnd)
                if app is not None and app.Workbooks.Count:
                    return app, process.pid
            except Exception:
                pass
        pump(0.2)
    raise RuntimeError(f"Could not attach to our Excel (PID {process.pid}); it was left open for inspection.")


class OwnedExcel:
    """A new Excel process of our own with its book; refuses to adopt an existing one."""

    def __init__(self, path: Path, label: str, before: set[int], launch: str):
        self.label = label
        if launch == "normal":
            self.app, self.pid = launch_normal(path, before)
        else:
            self.app = win32com.client.DispatchEx("Excel.Application")
            self.pid = win32process.GetWindowThreadProcessId(int(self.app.Hwnd))[1]
        if self.pid in before:
            raise RuntimeError("Excel activation returned an existing process; refusing to use it.")
        self.handle = win32api.OpenProcess(SYNCHRONIZE_QUERY_TERMINATE, False, self.pid)
        self.app.Visible = True
        self.app.DisplayAlerts = False
        self.book = self.app.Workbooks(path.name) if launch == "normal" else self.app.Workbooks.Open(str(path))
        self.path = path
        self.addin = self.automation = None
        self.connect_required = None

    def connect(self):
        names = [str(self.app.COMAddIns.Item(i).ProgId) for i in range(1, self.app.COMAddIns.Count + 1)]
        if "Aperture.Excel" not in names:
            raise RuntimeError(f"Aperture.Excel is not registered for Excel (COMAddIns: {names}). Build Aperture2.sln first.")
        self.addin = self.app.COMAddIns.Item("Aperture.Excel")
        # Excel started for automation does not load COM add-ins on its own.
        self.connect_required = not bool(self.addin.Connect)
        if self.connect_required:
            self.addin.Connect = True
        deadline = time.monotonic() + 20
        while self.automation is None and time.monotonic() < deadline:
            self.automation = self.addin.Object
            if self.automation is None:
                pump(0.2)
        if self.automation is None:
            raise RuntimeError(f"Excel {self.label}: the add-in connected without an automation object.")

    def info(self) -> dict:
        return json.loads(str(self.automation.GetInfo()))

    def state(self) -> dict:
        return json.loads(str(self.automation.GetState()))

    def wait_pane(self, timeout: float = 45) -> dict:
        deadline = time.monotonic() + timeout
        while True:
            info = self.info()
            panes = [p for p in info["panes"] if p["viewReady"] or p["viewInitializationError"]]
            if panes or time.monotonic() > deadline:
                return info
            pump(0.3)

    def open_pane_without_request(self, provider: str, model: str) -> dict:
        """Start creates and shows the pane; Cancel right away stops the startup of Pi before the
        prompt is sent. Pi writes its history only with the first message, so none may exist."""
        self.automation.Start(str(self.book.Name), "Ответь одним словом: готово.", provider, model)
        self.automation.Cancel()
        deadline = time.monotonic() + 30
        state = self.state()
        while state["state"] == "running" and time.monotonic() < deadline:
            pump(0.2)
            state = self.state()
        history = state.get("log_path")
        result = {"state": state, "historyWritten": bool(history and Path(history).exists())}
        assert state["state"] == "cancelled" and not result["historyWritten"], result
        return result

    def quit(self) -> dict:
        result = {"processId": self.pid, "quitRequested": False}
        try:
            if self.book is not None:
                self.book.Close(SaveChanges=False)
            self.book = None
            result["booksLeft"] = int(self.app.Workbooks.Count)
            if result["booksLeft"] == 0:
                self.app.Quit()
                result["quitRequested"] = True
        except Exception as error:
            result["error"] = str(error)
        self.automation = self.addin = self.app = None
        gc.collect()
        started = time.monotonic()
        while time.monotonic() - started < 30:
            pythoncom.PumpWaitingMessages()
            if win32event.WaitForSingleObject(self.handle, 100) == win32event.WAIT_OBJECT_0:
                break
        result["normalExit"] = win32event.WaitForSingleObject(self.handle, 0) == win32event.WAIT_OBJECT_0
        result["exitSeconds"] = round(time.monotonic() - started, 1)
        if not result["normalExit"] and result["quitRequested"]:
            # Only our own process, by its retained handle, never by name.
            win32api.TerminateProcess(self.handle, 0)
            result["forcedTermination"] = True
        return result


def read_log(path: str | None) -> dict:
    if not path or not Path(path).is_file():
        return {"exists": False, "path": path}
    assistants, tool_calls, tool_results, usage, cost, models = 0, [], 0, [], 0.0, set()
    for line in Path(path).read_text(encoding="utf-8").splitlines():
        entry = json.loads(line)
        message = entry.get("message") if entry.get("type") == "message" else None
        if not message:
            continue
        if message.get("role") == "assistant":
            assistants += 1
            models.add(f'{message.get("provider")}/{message.get("model")}')
            if isinstance(message.get("usage"), dict):
                u = message["usage"]
                usage.append({k: u.get(k) for k in ("input", "output", "cacheRead", "cacheWrite", "totalTokens")})
                cost += float((u.get("cost") or {}).get("total") or 0)
            tool_calls += [part.get("name") for part in message.get("content") or [] if part.get("type") == "toolCall"]
        elif message.get("role") == "toolResult":
            tool_results += 1
    return {"exists": True, "path": path, "assistantMessages": assistants, "usage": usage, "toolCalls": tool_calls,
            "toolResults": tool_results, "models": sorted(models), "piCostEstimate": round(cost, 6)}


def poll(excel: OwnedExcel, timeout: float, until, report: dict) -> dict:
    started = time.monotonic()
    slowest = 0.0
    while True:
        before = time.perf_counter()
        state = excel.state()
        slowest = max(slowest, time.perf_counter() - before)
        report["agentProcessesSeen"].update({str(pid): name for pid, name in agent_processes({excel.pid}).items()})
        report["dialogsSeen"] += dialogs(excel.pid)
        if until(state) or time.monotonic() - started > timeout:
            report["slowestGetStateSeconds"] = round(max(report.get("slowestGetStateSeconds", 0), slowest), 4)
            return state
        pump(1.0)


def expect_refusal(action, needle: str) -> str:
    try:
        action()
    except pywintypes.com_error as error:
        text = com_error_text(error)
        assert needle in text, text
        return text
    raise AssertionError(f"Start was accepted; expected a refusal containing «{needle}»")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--provider", default="routerai")
    parser.add_argument("--model", default="deepseek/deepseek-v4.1-flash")
    parser.add_argument("--launch", choices=("dispatch", "normal"), default="dispatch")
    args = parser.parse_args()
    run_dir = ROOT / ".local" / "smoke" / f"automation-{uuid.uuid4().hex[:12]}"
    run_dir.mkdir(parents=True)
    report: dict = {"runDir": str(run_dir), "provider": args.provider, "model": args.model, "launch": args.launch, "checks": {}}
    checks = report["checks"]
    before = set(win32process.EnumProcesses())
    other = excel = None
    roots: set[int] = set()
    pythoncom.CoInitialize()
    try:
        # B stands for an Excel whose pane is already open while another one runs requests.
        other_path, smoke_path = run_dir / "other.xlsx", run_dir / "smoke.xlsx"
        checks["makeBooks"] = make_books([other_path, smoke_path], before)
        other = OwnedExcel(other_path, "other", before, args.launch)
        roots.add(other.pid)
        other.connect()
        pump(2)
        checks["otherPanesBeforeRequest"] = other.info()["paneCount"]
        assert checks["otherPanesBeforeRequest"] == 0, "A pane opened by itself in Excel B"
        checks["otherPaneOpened"] = other.open_pane_without_request(args.provider, args.model)
        other_info = other.wait_pane()
        panes = report["panes"] = {"foreignBrowsers": foreign_pane_browsers(roots), "other": other_info["panes"]}
        checks["otherConnectRequired"] = other.connect_required

        excel = OwnedExcel(smoke_path, "smoke", before | {other.pid}, args.launch)
        roots.add(excel.pid)
        excel.connect()
        pump(2)
        info = excel.info()
        checks["getInfo"] = {k: v for k, v in info.items() if k != "panes"}
        checks["connectRequired"] = excel.connect_required
        assert info["name"] == "Aperture2" and info["loaded"] and info["excelProcessId"] == excel.pid, info
        assert info["systemPromptSha256"] and info["commit"], info
        # Run 1 starts on a window whose pane was never opened: Start creates it.
        assert info["paneCount"] == 0, "A pane opened by itself before Start"
        idle = excel.state()
        checks["stateBeforeStart"] = idle
        assert idle == {"state": "idle", "log_path": None, "error": None}, idle
        checks["refusedUnknownBook"] = expect_refusal(
            lambda: excel.automation.Start("нет-такой-книги.xlsx", PROMPT, args.provider, args.model), "не открыта")

        # Run 1: a real request that edits the book.
        book_name = str(excel.book.Name)
        started, called = time.monotonic(), time.perf_counter()
        excel.automation.Start(book_name, PROMPT, args.provider, args.model)
        run = {"startReturnedSeconds": round(time.perf_counter() - called, 4), "agentProcessesSeen": {}, "dialogsSeen": []}
        checks["run"] = run
        run["refusedWhileBusy"] = expect_refusal(
            lambda: excel.automation.Start(book_name, "второй запрос", args.provider, args.model), "занят")
        run["stateAfterStart"] = excel.state()["state"]
        run["paneCreatedByStart"] = [{k: p[k] for k in ("windowHwnd", "paneId", "boundWorkbookId")} for p in excel.info()["panes"]]
        assert len(run["paneCreatedByStart"]) == 1, run["paneCreatedByStart"]
        final = poll(excel, 300, lambda s: s["state"] != "running", run)
        run["final"] = final
        run["seconds"] = round(time.monotonic() - started, 1)
        assert final["state"] == "done", final
        sheets = [str(excel.book.Sheets(i).Name) for i in range(1, excel.book.Sheets.Count + 1)]
        value = excel.book.Worksheets("Тест").Range("A1").Value if "Тест" in sheets else None
        run["sheets"], run["testA1"] = sheets, value
        assert str(value).strip().lower() == "тест", (sheets, value)
        data = excel.book.Worksheets("Данные").Range("A1:B3").Value
        assert data == (("Выручка", 120.0), ("Затраты", 80.0), ("Прибыль", 40.0)), data
        run["log"] = read_log(final["log_path"])
        assert run["log"]["exists"] and run["log"]["usage"] and run["log"]["toolCalls"], run["log"]
        assert all((u["input"] or 0) > 0 and (u["output"] or 0) > 0 for u in run["log"]["usage"]), run["log"]
        assert run["agentProcessesSeen"], "No Pi/MCP process was observed under our Excel during the request"
        assert not run["dialogsSeen"], run["dialogsSeen"]
        panes["smokeAfterRun"] = excel.info()["panes"]

        # Run 2: the same as pressing Stop.
        started = time.monotonic()
        excel.automation.Start(book_name, CANCEL_PROMPT, args.provider, args.model)
        cancel = {"agentProcessesSeen": {}, "dialogsSeen": []}
        checks["cancel"] = cancel
        def tool_called(state):
            log = read_log(state["log_path"])
            return state["state"] != "running" or (log["exists"] and log["toolCalls"])
        state = poll(excel, 120, tool_called, cancel)
        assert state["state"] == "running", state
        cancel["toolCallsBeforeCancel"] = read_log(state["log_path"])["toolCalls"]
        cancel["requestedAfterSeconds"] = round(time.monotonic() - started, 1)
        requested = time.monotonic()
        excel.automation.Cancel()
        final = poll(excel, 30, lambda s: s["state"] != "running", cancel)
        cancel["final"] = final
        cancel["cancelSeconds"] = round(time.monotonic() - requested, 1)
        assert final["state"] == "cancelled" and cancel["cancelSeconds"] <= 10, cancel
        settled = read_log(final["log_path"])
        pump(5)
        later = read_log(final["log_path"])
        cancel["log"] = later
        assert later["toolCalls"] == settled["toolCalls"] and later["assistantMessages"] == settled["assistantMessages"], \
            "The agent kept working after Cancel"
        cancel["agentProcessesAfterCancel"] = agent_processes({excel.pid})
        # Two panes at once: B's pane while A worked and after A quit. Reported apart from the
        # automation checks: a WebView2 conflict breaks the view, not the chat controller.
        panes["otherWhileRunning"] = other.info()["panes"]
        smoke_agents = set(SEEN) - set(agent_processes({other.pid}))
        checks["quitSmoke"] = excel.quit()
        excel = None
        checks["agentProcessesAfterQuitSmoke"] = still_alive(smoke_agents)
        assert not checks["agentProcessesAfterQuitSmoke"], checks["agentProcessesAfterQuitSmoke"]
        pump(3)
        panes["otherAfterQuitSmoke"] = other.info()["panes"]
        views = [panes["other"], panes["smokeAfterRun"], panes["otherWhileRunning"], panes["otherAfterQuitSmoke"]]
        panes["ok"] = all(p and p[0]["viewReady"] and p[0]["viewInitializationError"] is None for p in views)
        report["success"] = True
    except Exception as error:
        report["success"] = False
        report["error"] = str(error)
        report["traceback"] = traceback.format_exc()
    finally:
        for owned, key in ((excel, "quitSmoke"), (other, "quitOther")):
            if owned is not None:
                checks[key] = owned.quit()
        excel = other = None
        gc.collect()
        pump(2)
        agent_processes(roots)
        checks["agentProcessesSeen"] = {pid: name for pid, (name, _) in SEEN.items()}
        checks["agentProcessesAfterQuit"] = still_alive(SEEN)
        checks["excelProcessesLeft"] = sorted(pid for pid in roots if pid in set(win32process.EnumProcesses()))
        problems = []
        for key in ("quitSmoke", "quitOther"):
            quit_report = checks.get(key)
            if quit_report and not quit_report.get("normalExit"):
                problems.append(f"{key}: Excel did not exit after Quit")
        if checks["agentProcessesAfterQuit"]:
            problems.append("Pi/MCP processes remained after Quit")
        if problems:
            report["success"] = False
            report["cleanupProblems"] = problems
        pythoncom.CoUninitialize()
        (run_dir / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2))
    print(f"Report: {run_dir / 'report.json'}")
    return 0 if report["success"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
