"""MCP protocol + live workbook probe; root owns disposable Excel lifecycle.

Read-only by default. --write-smoke authorizes A1:B4 writes in the already bound
test workbook. Never creates, saves, closes, or quits Excel. Run from repo root.
"""
import argparse
import asyncio
import json
import os
import sys
from pathlib import Path

from fastmcp import Client


def unpack(result):
    if result.is_error:
        raise RuntimeError(str(result))
    if result.structured_content:
        return result.structured_content
    for block in result.content:
        if getattr(block, "type", None) == "text":
            return json.loads(block.text)
    raise RuntimeError("No JSON result")


async def main(args):
    root = Path(__file__).resolve().parents[2]
    environment = dict(os.environ)
    environment["PYTHONPATH"] = str(root / "src")
    config = {"mcpServers": {"excel": {"command": sys.executable,
              "args": ["-m", "aperture_mcp", "--context", str(Path(args.context).resolve())],
              "env": environment}}}
    async with Client(config) as client:
        tools = await client.list_tools()
        async def call(name, payload=None):
            return unpack(await client.call_tool(name, payload or {}))
        target = await call("excel_target")
        ids = {key: target[key] for key in ("excelInstanceId", "workbookId")}
        sheet = args.sheet or target["worksheets"][0]["name"]
        result = {"toolCount": len(tools), "target": target, "read": await call("excel_read_range", {**ids, "sheet": sheet, "address": "A1:B4"})}
        if args.write_smoke:
            if target["workbook"]["saved"]:
                raise RuntimeError("Write smoke expects a deliberately unsaved disposable workbook.")
            await call("excel_write_values", {**ids, "sheet": sheet, "startCell": "A1", "values": [[20], [22]]})
            await call("excel_write_formula", {**ids, "sheet": sheet, "cell": "B1", "formula": "=SUM(A1:A2)"})
            await call("excel_recalculate", {**ids, "sheet": sheet})
            data = await call("excel_read_range", {**ids, "sheet": sheet, "address": "A1:B2"})
            assert data["values"][0][1] == 42, data
            assert data["formulas"][0][1] == "=SUM(A1:A2)", data
            after = await call("excel_target")
            assert not after["workbook"]["saved"]
            for property_name in ("Calculation", "ScreenUpdating", "EnableEvents", "DisplayAlerts"):
                assert target["applicationState"][property_name] == after["applicationState"][property_name]
            # Wrong identity must fail before COM mutation.
            wrong = await client.call_tool("excel_write_values", {**ids, "workbookId": "not-the-book", "sheet": sheet, "startCell": "A1", "values": [[999]]}, raise_on_error=False)
            assert wrong.is_error
            result["writeReadCalculateReadback"] = data
            result["wrongTargetRejected"] = True
            result["applicationStateRestored"] = after["applicationState"]
        print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--context", required=True)
    parser.add_argument("--sheet")
    parser.add_argument("--write-smoke", action="store_true")
    asyncio.run(main(parser.parse_args()))
