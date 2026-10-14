import asyncio
import json
import threading
from types import SimpleNamespace

import pytest
from fastmcp.exceptions import ToolError

from aperture_mcp.binding import BoundSession, Target, workbook_matches
from aperture_mcp.server import create_server, validate_address, validate_formula, validate_sheet_name, validate_values, workbook_overview


def make_target():
    return Target("instance-1", "book-1", 123, 456, "Book1", "Book1", 789)


class Windows:
    Count = 1

    def __call__(self, index):
        return SimpleNamespace(Hwnd=789)


def test_binding_requires_window_identity_even_for_same_unsaved_name():
    wb = SimpleNamespace(Name="Book1", FullName="Book1", Windows=Windows())
    assert workbook_matches(wb, make_target())
    other = Target("i", "b", 123, 456, "Book1", "Book1", 999)
    assert not workbook_matches(wb, other)


def test_context_requires_strong_identity(tmp_path):
    path = tmp_path / "target.json"
    path.write_text(json.dumps({"excelInstanceId": "i"}), encoding="utf-8")
    with pytest.raises(KeyError):
        Target.load(str(path))


def test_curated_writes_never_set_global_application_state():
    class RejectGlobalChanges:
        def __setattr__(self, name, value):
            raise AssertionError("A write attempted to mutate application global: " + name)
    session = BoundSession(make_target())
    session._app = RejectGlobalChanges()
    session.get_workbook = lambda: object()
    assert session.call(lambda: {"written": 1}, mutation=True) == {"written": 1}
    with pytest.raises(ToolError, match="may have partially applied"):
        session.call(lambda: (_ for _ in ()).throw(RuntimeError("write failed")), mutation=True)
    create_server(make_target(), session=session)
    from thepexcel_mcp.domains import format as formatting
    with formatting.excel_guard(session._app):
        pass  # The curated formatting path also must never change DisplayAlerts.


def test_chart_sheets_do_not_break_workbook_overview():
    class Collection:
        def __init__(self, values):
            self.values, self.Count = values, len(values)
        def __call__(self, index):
            return self.values[index - 1]
    sheet = SimpleNamespace(Name="Model", ListObjects=SimpleNamespace(Count=2))
    chart = SimpleNamespace(Name="Chart")  # Deliberately has no ListObjects.
    wb = SimpleNamespace(Name="Book1", FullName="Book1", Saved=False,
                         Sheets=Collection([sheet, chart]), Worksheets=Collection([sheet]),
                         Queries=SimpleNamespace(Count=0), Names=SimpleNamespace(Count=1))
    result = workbook_overview(wb)
    assert result["sheets"] == ["Model", "Chart"]
    assert result["table_count"] == 2


def test_invalid_sheet_names_are_rejected_before_add():
    for name in ("'Leading", "Trailing'", "History", "history", "Bad/Name", "Bad\x00Name", ""):
        with pytest.raises(ToolError):
            validate_sheet_name(name)
    validate_sheet_name("Audit report")


def test_addresses_and_literal_values_cannot_redirect_or_smuggle_formulas():
    for address in ("[Other.xlsx]Sheet1!A1", "Sheet2!A1", "A:A", "XFE1", "A0", "B2:A1"):
        with pytest.raises(ToolError):
            validate_address(address)
    assert validate_address("$A$1:B2") == (1, 1, 2, 2)
    for values in ([["=SUM(A1:A2)"]], [[1, 2], [3]], [[{"x": 1}]]):
        with pytest.raises(ToolError):
            validate_values(values)
    validate_values([[21], [-21]])
    for formula in ("=PY(1)", "=WEBSERVICE(\"https://example.org\")", "=cmd|' /C calc'!A0"):
        with pytest.raises(ToolError):
            validate_formula(formula)
    validate_formula("=SUM(A1:A2)")


def test_timeout_does_not_retry_and_requires_reconciliation():
    # No live COM needed: worker binding is replaced by a fake app. The queue,
    # timeout and serialization remain the actual implementation.
    session = BoundSession(make_target(), timeout=0.02)
    app = SimpleNamespace(ScreenUpdating=True, EnableEvents=True, Calculation=-4105)
    session._app = app
    session.get_workbook = lambda: object()
    release = threading.Event()
    calls = []
    def slow_write():
        calls.append(1)
        release.wait(2)
        return {"written": 1}
    with pytest.raises(ToolError, match="timed out"):
        session.call(slow_write, mutation=True)
    with pytest.raises(ToolError, match="unresolved"):
        session.call(lambda: "second write", mutation=True)
    assert session.operation_status()["status"] == "running"
    release.set()
    session._pending.result(timeout=2)
    assert session.operation_status()["status"] == "completed"
    assert calls == [1]
    assert vars(app) == dict(ScreenUpdating=True, EnableEvents=True, Calculation=-4105)


def test_operations_hold_no_excel_references_between_calls():
    # The parent job kills this process when the chat or Excel closes. A killed client
    # that still holds COM references keeps the closed Excel running hidden.
    import weakref

    class ComObject:
        pass

    session = BoundSession(make_target())
    held = []

    def resolve(workbook=None):
        session._app = session._app or ComObject()
        session._workbook = session._workbook or ComObject()
        held.extend([weakref.ref(session._app), weakref.ref(session._workbook)])
        return session._workbook

    session.get_workbook = resolve
    assert session.call(lambda: session.get_workbook() is not None) is True
    assert session._app is None and session._workbook is None
    assert held and all(ref() is None for ref in held)

    def failing():
        sheet = ComObject()  # referenced only by this frame, i.e. by the traceback
        held.append(weakref.ref(sheet))
        raise RuntimeError("COM call failed")

    with pytest.raises(RuntimeError, match="COM call failed"):
        session.call(failing)
    assert all(ref() is None for ref in held)


def test_catalog_and_wrong_target_cannot_reach_backend():
    from fastmcp import Client
    class UnusedSession:
        calls = 0
        def call(self, *args, **kwargs):
            self.calls += 1
            raise AssertionError("must not enter COM")
    session = UnusedSession()
    server = create_server(make_target(), session=session)
    async def check():
        async with Client(server) as client:
            names = {tool.name for tool in await client.list_tools()}
            assert names == {"excel_target", "excel_read_range", "excel_write_values", "excel_write_formula",
                             "excel_recalculate", "excel_find", "excel_names", "excel_add_sheet", "excel_format",
                             "excel_operation_status"}
            result = await client.call_tool("excel_write_values", {"excelInstanceId": "wrong", "workbookId": "book-1",
                            "sheet": "Sheet1", "startCell": "A1", "values": [[1]]}, raise_on_error=False)
            assert result.is_error
            assert session.calls == 0
    asyncio.run(check())
