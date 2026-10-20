r"""viz_mcp -- MCP-сервер (stdio, JSON-RPC 2.0) поверх vizcore.

Агент (DSH/Claude/Cursor) подключает его как MCP-сервер и получает инструменты:
    viz_callers, viz_callees, viz_impact, viz_impact_diff, viz_cycles,
    viz_mutators, viz_deadcode, viz_dangling, viz_concurrency, viz_hotpath,
    viz_layers, viz_coverage, viz_plandrift, viz_stats, viz_symbol

Запуск:
    python viz_mcp.py                     (repo по умолчанию -- ../bebebe-main)
    python viz_mcp.py --repo "D:\...\bebebe-main"
    python viz_mcp.py --selftest          (проверка протокола без клиента)

Протокол: по одному JSON-объекту на строку в stdin, ответ -- строкой в stdout.
"""

from __future__ import annotations

import argparse
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_REPO = os.environ.get("VIZ_REPO") or os.path.abspath(os.path.join(HERE, "..", "bebebe-main"))

sys.path.insert(0, HERE)
import vizcore  # noqa: E402

PROTOCOL_VERSION = "2024-11-05"
SERVER_INFO = {"name": "viz-mcp", "version": "1.0.0"}

_IDX = None
_REPO = DEFAULT_REPO


def index():
    global _IDX
    if _IDX is None:
        _IDX = vizcore.ProjectIndex.build(_REPO, use_cache=True)
    return _IDX


def _tool(name, desc, props=None, required=None):
    return {
        "name": name,
        "description": desc,
        "inputSchema": {
            "type": "object",
            "properties": props or {},
            "required": required or [],
            "additionalProperties": False,
        },
    }


TOOLS = [
    _tool("viz_callers", "Кто вызывает символ (метод/класс) в проекте.",
          {"symbol": {"type": "string", "description": "имя метода или Class.Method"}}, ["symbol"]),
    _tool("viz_callees", "Что вызывает символ: исходящие зависимости файла(ов), где он объявлен.",
          {"symbol": {"type": "string"}}, ["symbol"]),
    _tool("viz_impact", "Что затронет правка файла: прямые и транзитивные зависимые + разбивка по слоям.",
          {"file": {"type": "string"}, "transitive": {"type": "boolean", "default": True},
           "depth": {"type": "integer", "default": 6}}, ["file"]),
    _tool("viz_impact_diff", "Impact по текущему git diff: какие файлы затронет незакоммиченное изменение.",
          {"base": {"type": "string", "default": "HEAD"}}),
    _tool("viz_cycles", "Циклы зависимостей (компоненты сильной связности файлового графа).",
          {"scope": {"type": "string", "description": "префикс пути, напр. src/simulation"}}),
    _tool("viz_mutators", "Все места, где пишется в состояние (поле или Class.field).",
          {"state": {"type": "string"}}, ["state"]),
    _tool("viz_deadcode", "Файлы и типы без входящих ссылок; сцены, никем не используемые."),
    _tool("viz_dangling", "Висячие ссылки: .tscn -> несуществующие ресурсы/скрипты, сигналы без метода."),
    _tool("viz_concurrency", "Карта конкурентности: статические синглтоны, lock/Interlocked, потоки, доля разделяемых записей."),
    _tool("viz_hotpath", "Методы, достижимые из entry-point'ов кадра, с оценкой стоимости (циклы/аллокации).",
          {"depth": {"type": "integer", "default": 3}}),
    _tool("viz_layers", "Нарушения слоёв: зависимости снизу вверх (core -> ui и подобные)."),
    _tool("viz_coverage", "Покрытие src-файлов тестами; непокрытые горячие файлы."),
    _tool("viz_plandrift", "Дрейф PLAN.md: ссылки на несуществующие файлы и строки за пределами файла.",
          {"plan": {"type": "string", "default": "PLAN.md"}}),
    _tool("viz_stats", "Сводка по индексу: файлы, символы, рёбра, top fan-in/fan-out."),
    _tool("viz_symbol", "Символ по файлу и номеру строки.",
          {"file": {"type": "string"}, "line": {"type": "integer"}}, ["file", "line"]),
]


def call_tool(name: str, a: dict):
    idx = index()
    if name == "viz_callers":
        return idx.callers(a["symbol"])
    if name == "viz_callees":
        return idx.callees(a["symbol"])
    if name == "viz_impact":
        return idx.impact_of_file(a["file"], transitive=a.get("transitive", True),
                                  max_depth=int(a.get("depth", 6)))
    if name == "viz_impact_diff":
        return idx.impact_of_diff(base=a.get("base", "HEAD"))
    if name == "viz_cycles":
        return idx.cycles(scope=a.get("scope"))
    if name == "viz_mutators":
        return idx.mutators(a["state"])
    if name == "viz_deadcode":
        return idx.dead_code()
    if name == "viz_dangling":
        return idx.dangling()
    if name == "viz_concurrency":
        return idx.concurrency()
    if name == "viz_hotpath":
        return idx.hot_path(depth=int(a.get("depth", 3)))
    if name == "viz_layers":
        return idx.layer_violations()
    if name == "viz_coverage":
        return idx.coverage()
    if name == "viz_plandrift":
        return idx.plan_drift(plan_path=a.get("plan", "PLAN.md"))
    if name == "viz_stats":
        return idx.stats()
    if name == "viz_symbol":
        return idx.symbol_at(a["file"], int(a["line"]))
    raise KeyError(name)


def handle(req: dict) -> dict | None:
    method = req.get("method")
    rid = req.get("id")
    is_notification = "id" not in req

    def ok(result):
        return None if is_notification else {"jsonrpc": "2.0", "id": rid, "result": result}

    def err(code, message):
        return None if is_notification else {
            "jsonrpc": "2.0", "id": rid, "error": {"code": code, "message": message}}

    try:
        if method == "initialize":
            return ok({
                "protocolVersion": PROTOCOL_VERSION,
                "capabilities": {"tools": {"listChanged": False}},
                "serverInfo": SERVER_INFO,
            })
        if method in ("notifications/initialized", "initialized"):
            return None
        if method == "ping":
            return ok({})
        if method == "tools/list":
            return ok({"tools": TOOLS})
        if method == "tools/call":
            params = req.get("params") or {}
            tool = params.get("name")
            args = params.get("arguments") or {}
            try:
                payload = call_tool(tool, args)
                return ok({"content": [{"type": "text",
                                        "text": json.dumps(payload, ensure_ascii=False, default=str)}],
                           "isError": False})
            except KeyError:
                return ok({"content": [{"type": "text", "text": json.dumps({"error": f"unknown tool {tool}"})}],
                           "isError": True})
            except Exception as e:  # noqa: BLE001
                return ok({"content": [{"type": "text",
                                        "text": json.dumps({"error": f"{type(e).__name__}: {e}"}, ensure_ascii=False)}],
                           "isError": True})
        return err(-32601, f"method not found: {method}")
    except Exception as e:  # noqa: BLE001
        return err(-32603, f"{type(e).__name__}: {e}")


def serve() -> int:
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
        except ValueError as e:
            sys.stdout.write(json.dumps({"jsonrpc": "2.0", "id": None,
                                         "error": {"code": -32700, "message": f"parse error: {e}"}}) + "\n")
            sys.stdout.flush()
            continue
        resp = handle(req if isinstance(req, dict) else {})
        if resp is not None:
            sys.stdout.write(json.dumps(resp, ensure_ascii=False) + "\n")
            sys.stdout.flush()
    return 0


def selftest() -> int:
    reqs = [
        {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {}},
        {"jsonrpc": "2.0", "method": "notifications/initialized"},
        {"jsonrpc": "2.0", "id": 2, "method": "tools/list"},
        {"jsonrpc": "2.0", "id": 3, "method": "tools/call",
         "params": {"name": "viz_stats", "arguments": {}}},
        {"jsonrpc": "2.0", "id": 4, "method": "tools/call",
         "params": {"name": "viz_impact", "arguments": {"file": "src/simulation/jobs/JobSpatialPyramid.cs"}}},
        {"jsonrpc": "2.0", "id": 5, "method": "tools/call",
         "params": {"name": "viz_hotpath", "arguments": {"depth": 1}}},
    ]
    outputs = []
    for r in reqs:
        resp = handle(r)
        if resp is not None:
            outputs.append(resp)
    if "id" in reqs[1]:
        print("SELFTEST FAIL: notification got a response")
        return 1
    if len(outputs) != 5:
        print(f"SELFTEST FAIL: expected 5 responses, got {len(outputs)}")
        return 1
    tools = outputs[1]["result"]["tools"]
    if len(tools) != len(TOOLS):
        print("SELFTEST FAIL: tools/list mismatch")
        return 1
    if outputs[2].get("result", {}).get("isError"):
        print("SELFTEST FAIL: viz_stats error")
        return 1
    stats = json.loads(outputs[2]["result"]["content"][0]["text"])
    impact = json.loads(outputs[3]["result"]["content"][0]["text"])
    hot = json.loads(outputs[4]["result"]["content"][0]["text"])
    print(f"SELFTEST OK: tools={len(tools)} files={stats['files']} "
          f"impact_files={impact['files_count']} hot={hot['hot_count']}")
    return 0


def main(argv=None) -> int:
    global _REPO
    ap = argparse.ArgumentParser(prog="viz_mcp", description="MCP-сервер индекса зависимостей")
    ap.add_argument("--repo", default=DEFAULT_REPO)
    ap.add_argument("--selftest", action="store_true")
    args = ap.parse_args(argv)
    _REPO = args.repo
    if args.selftest:
        return selftest()
    return serve()


if __name__ == "__main__":
    sys.exit(main())
