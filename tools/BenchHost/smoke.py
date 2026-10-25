"""BenchHost acceptance smoke; owns only a new disposable Excel process and its own book.

1. DispatchEx starts a new visible Excel; a small book is saved under .local/smoke
   and reopened from disk, as the benchmark runner does with its copy.
2. make_target captures target.json for that book by Excel PID.
3. BenchHost (--bench) runs a real model with an edit prompt; the run records, the
   workbook result and the absence of leftover Pi/MCP processes are checked.
4. A second BenchHost run with a long prompt and --timeout-s 20 must end as `limit`.
5. The book is closed without saving and our Excel is quit.

Costs a few model requests. Never touches a user workbook or another Excel process.
"""
from __future__ import annotations

import argparse
import gc
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import threading
import time
import traceback
import uuid

import pythoncom
import win32api
import win32com.client
import win32process


ROOT = Path(__file__).resolve().parents[2]
BENCH_HOST = ROOT / "tools" / "BenchHost" / "bin" / "Debug" / "net48" / "BenchHost.exe"
EDIT_PROMPT = "Добавь в книгу лист «Аудит» и запиши в его ячейку A1 слово «тест». Больше ничего в книге не меняй."
LONG_PROMPT = (
    "Это проверка длинного ответа. Сначала прочитай диапазон A1:C4 на листе «Данные». "
    "Затем, ничего не меняя в книге, напиши прямо в чате очень подробный аналитический отчёт "
    "объёмом не меньше 4000 слов: разбери каждую статью и каждый год, возможные причины изменений, "
    "риски, допущения, десять сценариев развития и подробные рекомендации по каждому сценарию."
)
RUN_FILES = ("harness_info.json", "trajectory.jsonl", "usage.json", "done.json", "pi_events.jsonl")


def _local_smoke():
    spec = importlib.util.spec_from_file_location("aperture_local_smoke", ROOT / "tools" / "LocalSmoke" / "run.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


local_smoke = _local_smoke()


def file_sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def workbook_snapshot(workbook) -> dict:
    sheets = {}
    for index in range(1, workbook.Worksheets.Count + 1):
        sheet = workbook.Worksheets(index)
        used = sheet.UsedRange
        sheets[str(sheet.Name)] = {"address": str(used.Address), "values": used.Value2, "formulas": used.Formula2}
    return {"order": [str(workbook.Sheets(i).Name) for i in range(1, workbook.Sheets.Count + 1)], "sheets": sheets}


def owned_children(marker: str) -> list[dict]:
    """node/python processes whose command line names this run directory (Pi and its MCP child)."""
    wmi = win32com.client.GetObject(r"winmgmts:root\cimv2")
    query = "SELECT ProcessId, Name, CommandLine FROM Win32_Process WHERE Name='node.exe' OR Name='python.exe'"
    found = []
    for process in wmi.ExecQuery(query):
        command = process.CommandLine or ""
        if marker.casefold() in command.casefold():
            found.append({"pid": int(process.ProcessId), "name": str(process.Name)})
    return found


def run_bench_host(arguments: list[str], run_dir: Path, timeout: float) -> dict:
    """Runs BenchHost, pumping COM messages and sampling its Pi/MCP children while it works."""
    process = subprocess.Popen(arguments, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               text=True, encoding="utf-8", errors="replace")
    captured: list[tuple[str, str]] = []
    reader = threading.Thread(target=lambda: captured.append(process.communicate()), daemon=True)
    reader.start()
    seen: dict[int, str] = {}
    deadline = time.monotonic() + timeout
    next_sample = 0.0
    while reader.is_alive():
        pythoncom.PumpWaitingMessages()
        if time.monotonic() >= next_sample:
            for child in owned_children(str(run_dir)):
                seen[child["pid"]] = child["name"]
            next_sample = time.monotonic() + 1.0
        if time.monotonic() >= deadline:
            process.kill()  # only the BenchHost we started; its job object then ends Pi and MCP
            reader.join(5)
            raise TimeoutError(f"BenchHost exceeded the smoke deadline of {timeout}s")
        reader.join(0.05)
    stdout, stderr = captured[0]
    time.sleep(0.5)
    leftovers = owned_children(str(run_dir))
    return {"exitCode": process.returncode, "stdout": stdout.strip(), "stderr": stderr.strip()[-4000:],
            "childrenSeenDuringRun": sorted(set(seen.values())), "childPidsSeen": sorted(seen),
            "leftoverChildren": leftovers}


def read_run(run_dir: Path) -> dict:
    missing = [name for name in RUN_FILES if not (run_dir / name).is_file()]
    assert not missing, f"Missing run files in {run_dir}: {missing}"
    trajectory = [json.loads(line) for line in (run_dir / "trajectory.jsonl").read_text(encoding="utf-8").splitlines() if line.strip()]
    for line in (run_dir / "pi_events.jsonl").read_text(encoding="utf-8").splitlines():
        json.loads(line)
    return {
        "harness": json.loads((run_dir / "harness_info.json").read_text(encoding="utf-8")),
        "usage": json.loads((run_dir / "usage.json").read_text(encoding="utf-8")),
        "done": json.loads((run_dir / "done.json").read_text(encoding="utf-8")),
        "trajectory": trajectory,
    }


def summarize(run: dict, result: dict) -> dict:
    calls = [r for r in run["trajectory"] if r["type"] == "llm_call"]
    tools = [r for r in run["trajectory"] if r["type"] == "tool_call"]
    return {
        "exitCode": result["exitCode"], "state": run["done"]["state"], "error": run["done"]["error"],
        "llmCalls": len(calls), "llmCallsWithUsage": sum(1 for r in calls if r["usage"]),
        "modelsReturned": run["usage"]["models_returned"], "toolCalls": [r["tool"] for r in tools],
        "upstreamProviders": sorted({r["provider"] for r in calls if r.get("provider")}),
        "tokens": run["usage"]["tokens"], "cost": run["usage"]["cost"], "costSource": run["usage"]["cost_source"],
        "costPiEstimate": run["usage"]["cost_pi_estimate"], "costCurrency": run["usage"]["cost_currency"],
        "thinkingLevel": run["harness"]["thinking_level"], "toolCount": len(run["harness"]["tools"]),
        "systemPromptTemplateSha256": (run["harness"]["system_prompt"] or {}).get("template_sha256"),
        "childrenSeenDuringRun": result["childrenSeenDuringRun"], "leftoverChildren": result["leftoverChildren"],
        "finalMessage": run["done"]["final_message"][:500],
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--provider", default="routerai")
    parser.add_argument("--model", default="deepseek/deepseek-v4.1-flash")
    parser.add_argument("--api-key-env", default="ROUTERAI_API_KEY")
    parser.add_argument("--cost-currency", default="RUB")
    parser.add_argument("--thinking")
    parser.add_argument("--edit-timeout-s", type=int, default=300)
    parser.add_argument("--limit-timeout-s", type=int, default=20)
    parser.add_argument("--skip-timeout", action="store_true", help="Run only the edit scenario")
    args = parser.parse_args()
    if not BENCH_HOST.is_file():
        raise SystemExit(f"Build BenchHost first: {BENCH_HOST}")

    output_dir = ROOT / ".local" / "smoke" / ("bench-" + uuid.uuid4().hex[:12])
    output_dir.mkdir(parents=True)
    report: dict = {"outputDir": str(output_dir), "provider": args.provider, "model": args.model, "checks": {}}
    application = workbook = None
    owned_handle = owned_creation = None
    pid = None
    quit_requested = zero_books_confirmed = False
    before_pids = set(win32process.EnumProcesses())
    pythoncom.CoInitialize()
    try:
        application = win32com.client.DispatchEx("Excel.Application")
        pid = win32process.GetWindowThreadProcessId(int(application.Hwnd))[1]
        if pid in before_pids:
            raise RuntimeError("Excel activation did not create a new process; refusing to use an existing one.")
        owned_handle = win32api.OpenProcess(0x101001, False, pid)  # SYNCHRONIZE | QUERY_LIMITED_INFORMATION | TERMINATE
        owned_creation = str(win32process.GetProcessTimes(owned_handle)["CreationTime"])
        application.Visible = True
        application.DisplayAlerts = False

        book_path = output_dir / "bench_smoke.xlsx"
        created = application.Workbooks.Add()
        sheet = created.Worksheets(1)
        sheet.Name = "Данные"
        sheet.Range("A1:C3").Value2 = (("Статья", 2025, 2026), ("Выручка", 100, 120), ("Расходы", 60, 70))
        sheet.Range("A4").Value2 = "Прибыль"
        sheet.Range("B4").Formula2 = "=B2-B3"
        sheet.Range("C4").Formula2 = "=C2-C3"
        created.SaveAs(str(book_path), FileFormat=51)
        created.Close(SaveChanges=False)
        sheet = created = None
        workbook = application.Workbooks.Open(str(book_path))
        disk_hash = file_sha256(book_path)
        before = workbook_snapshot(workbook)
        report["checks"]["disposableExcel"] = {"processId": pid, "book": str(book_path)}

        target_path = output_dir / "target.json"
        env = dict(os.environ, PYTHONPATH=str(ROOT / "src"), PYTHONIOENCODING="utf-8")
        made = subprocess.run([sys.executable, "-m", "aperture_mcp.make_target", "--pid", str(pid),
                               "--workbook", str(book_path), "--out", str(target_path)],
                              cwd=ROOT, env=env, capture_output=True, text=True, encoding="utf-8", timeout=60)
        assert made.returncode == 0, made.stderr
        target = json.loads(target_path.read_text(encoding="utf-8"))
        assert target["excelProcessId"] == pid and target["workbookFullName"].casefold() == str(book_path).casefold(), target
        report["checks"]["makeTarget"] = {"workbookName": target["workbookName"], "excelProcessId": pid}

        def bench(name: str, prompt: str, timeout_s: int) -> tuple[Path, dict, dict]:
            run_dir = output_dir / name
            prompt_file = output_dir / f"{name}.prompt.md"
            prompt_file.write_text(prompt, encoding="utf-8")
            command = [str(BENCH_HOST), "--root", str(ROOT), "--bench", "--context", str(target_path),
                       "--prompt-file", str(prompt_file), "--run-dir", str(run_dir), "--timeout-s", str(timeout_s),
                       "--provider", args.provider, "--model", args.model]
            if args.api_key_env:
                command += ["--api-key-env", args.api_key_env]
            if args.cost_currency:
                command += ["--cost-currency", args.cost_currency]
            if args.thinking:
                command += ["--thinking", args.thinking]
            result = run_bench_host(command, run_dir, timeout_s + 90)
            run = read_run(run_dir)
            return run_dir, result, run

        _, result, run = bench("edit", EDIT_PROMPT, args.edit_timeout_s)
        summary = summarize(run, result)
        report["checks"]["edit"] = summary
        assert result["exitCode"] == 0 and run["done"]["state"] == "done", summary
        calls = [r for r in run["trajectory"] if r["type"] == "llm_call"]
        assert any(r["usage"] and r["usage"]["prompt"] > 0 and r["usage"]["completion"] > 0 for r in calls), "No llm_call carried usage"
        assert any(r["type"] == "tool_call" for r in run["trajectory"]), "No tool_call was recorded"
        assert run["usage"]["tokens"] and run["usage"]["llm_calls_with_usage"] > 0, run["usage"]
        assert {"node.exe", "python.exe"} <= set(result["childrenSeenDuringRun"]), "Pi/MCP children were never observed; the leftover check would be vacuous"
        assert not result["leftoverChildren"], f"Leftover processes: {result['leftoverChildren']}"
        after = workbook_snapshot(workbook)
        assert "Аудит" in after["sheets"], after["order"]
        audit = workbook.Worksheets("Аудит")
        assert audit.Range("A1").Value2 == "тест", audit.Range("A1").Value2
        assert str(audit.UsedRange.Address) == "$A$1" and application.WorksheetFunction.CountA(audit.Cells) == 1, str(audit.UsedRange.Address)
        audit = None
        assert [name for name in after["order"] if name != "Аудит"] == before["order"], after["order"]
        for name, data in before["sheets"].items():
            assert after["sheets"][name] == data, f"Sheet {name} changed"
        assert file_sha256(book_path) == disk_hash, "The book on disk changed: the agent must not save"
        report["checks"]["editWorkbook"] = {"auditA1": "тест", "otherSheetsUnchanged": True, "notSaved": True}
        print("BenchHost edit run passed.", flush=True)

        if not args.skip_timeout:
            snapshot = workbook_snapshot(workbook)
            _, result, run = bench("limit", LONG_PROMPT, args.limit_timeout_s)
            summary = summarize(run, result)
            report["checks"]["timeout"] = summary
            assert run["done"]["state"] == "limit" and result["exitCode"] == 2, summary
            assert run["done"]["pi_process_exited"] is True, run["done"]
            assert {"node.exe", "python.exe"} <= set(result["childrenSeenDuringRun"]), "Pi/MCP children were never observed"
            assert not result["leftoverChildren"], f"Leftover processes: {result['leftoverChildren']}"
            assert workbook_snapshot(workbook) == snapshot, "The long read-only prompt changed the book"
            assert workbook.Worksheets("Данные").Range("B4").Value2 == 40, "Excel did not answer after the aborted run"
            print("BenchHost timeout run passed.", flush=True)
        report["success"] = True
    except Exception as error:
        report["success"] = False
        report["error"] = str(error)
        report["traceback"] = traceback.format_exc()
    finally:
        cleanup_errors = []
        if workbook is not None:
            try:
                workbook.Close(SaveChanges=False)
            except Exception as error:
                cleanup_errors.append(f"Could not close our test book: {error}")
            workbook = None
        if application is not None and owned_handle is not None:
            try:
                if application.Workbooks.Count == 0:
                    zero_books_confirmed = True
                    application.Quit()
                    quit_requested = True
                else:
                    cleanup_errors.append("Our Excel now contains other books; it was left open.")
            except Exception as error:
                cleanup_errors.append(f"Could not stop our Excel process: {error}")
        application = None
        gc.collect()
        pythoncom.CoUninitialize()
        if owned_handle is not None:
            try:
                exit_status = local_smoke.verify_owned_process_exit(owned_handle, pid, owned_creation,
                                                                    quit_requested, zero_books_confirmed)
                report["checks"]["ownedExcelProcessExit"] = exit_status
                if not exit_status["exited"]:
                    cleanup_errors.append("Owned Excel process did not exit.")
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
