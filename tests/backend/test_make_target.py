from types import SimpleNamespace

import pytest

from aperture_mcp import make_target as module
from aperture_mcp.binding import Target


class Collection:
    def __init__(self, values):
        self.values, self.Count = values, len(values)

    def __call__(self, index):
        return self.values[index - 1]


def book(name, full_name, hwnd):
    return SimpleNamespace(Name=name, FullName=full_name, Windows=Collection([SimpleNamespace(Hwnd=hwnd)]))


def application(*books):
    return SimpleNamespace(Hwnd=456, Workbooks=Collection(list(books)))


def test_full_path_selects_only_that_open_book():
    wanted = book("Model.xlsx", r"C:\Bench\Run\Model.xlsx", 789)
    app = application(book("Model.xlsx", r"C:\Other\Model.xlsx", 111), wanted)
    assert module.find_workbook(app, r"c:\bench\run\MODEL.xlsx") is wanted
    with pytest.raises(LookupError, match="0 times"):
        module.find_workbook(app, r"C:\Bench\Run\Missing.xlsx")


def test_bare_name_matches_only_an_unsaved_book():
    unsaved = book("Book2", "Book2", 222)
    app = application(book("Book1.xlsx", r"C:\Data\Book1.xlsx", 111), unsaved)
    assert module.find_workbook(app, "Book2") is unsaved
    # A saved book needs its full path; the name alone is ambiguous across folders.
    with pytest.raises(LookupError):
        module.find_workbook(app, "Book1.xlsx")


def test_target_has_binding_fields_and_fresh_ids(monkeypatch):
    monkeypatch.setattr(module, "window_pid", lambda hwnd: 123)
    app = application()
    wb = book("Model.xlsx", r"C:\Bench\Model.xlsx", 789)
    first, second = module.build_target(app, wb), module.build_target(app, wb)
    assert set(first) == set(Target.__dataclass_fields__)
    assert first["excelProcessId"] == 123 and first["excelHwnd"] == 456 and first["workbookHwnd"] == 789
    assert first["workbookFullName"] == r"C:\Bench\Model.xlsx"
    assert first["excelInstanceId"] != second["excelInstanceId"] and first["workbookId"] != second["workbookId"]
