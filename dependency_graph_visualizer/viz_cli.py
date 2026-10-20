"""viz_cli -- JSON-интерфейс к индексу зависимостей для агента.

Примеры:
    python viz_cli.py stats
    python viz_cli.py impact --file src/simulation/jobs/JobSpatialPyramid.cs
    python viz_cli.py impact-diff --base HEAD~1
    python viz_cli.py callers --symbol RegisterJob
    python viz_cli.py hotpath --depth 2
    python viz_cli.py mutators --state _globalCdCount

Коды возврата: 0 успех, 2 ошибка аргументов, 3 внутренняя ошибка.
JSON идёт в stdout, ошибки -- в stderr.
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_REPO = os.environ.get("VIZ_REPO") or os.path.abspath(os.path.join(HERE, "..", "bebebe-main"))

sys.path.insert(0, HERE)
import vizcore  # noqa: E402

_IDX = {}


def get_index(repo: str, rebuild: bool = False):
    if rebuild or repo not in _IDX:
        _IDX[repo] = vizcore.ProjectIndex.build(repo, use_cache=not rebuild)
    return _IDX[repo]


def emit(obj, pretty: bool) -> None:
    if pretty:
        print(json.dumps(obj, ensure_ascii=False, indent=2, default=str))
    else:
        print(json.dumps(obj, ensure_ascii=False, default=str))


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(prog="viz_cli", description="Индекс зависимостей C#/GD/сцен (JSON)")
    p.add_argument("--repo", default=DEFAULT_REPO, help="корень анализируемого проекта")
    p.add_argument("--pretty", action="store_true", help="человекочитаемый JSON")
    p.add_argument("--rebuild", action="store_true", help="игнорировать кэш")
    # те же флаги на уровне подкоманд, чтобы принимать их в любом месте строки
    common = argparse.ArgumentParser(add_help=False)
    common.add_argument("--repo", default=argparse.SUPPRESS, help=argparse.SUPPRESS)
    common.add_argument("--pretty", action="store_true", default=argparse.SUPPRESS,
                        help=argparse.SUPPRESS)
    common.add_argument("--rebuild", action="store_true", default=argparse.SUPPRESS,
                        help=argparse.SUPPRESS)
    sub = p.add_subparsers(dest="cmd", required=True)

    def add(name, help_text):
        return sub.add_parser(name, help=help_text, parents=[common])

    add("stats", "сводка по индексу")

    sp = add("files", "список файлов")
    sp.add_argument("--ext", choices=[".cs", ".gd", ".tscn"], default=None)

    sp = add("callers", "кто вызывает символ")
    sp.add_argument("--symbol", required=True)

    sp = add("callees", "что вызывает символ")
    sp.add_argument("--symbol", required=True)

    sp = add("impact", "что затронет правка файла")
    sp.add_argument("--file", required=True)
    sp.add_argument("--no-transitive", action="store_true")
    sp.add_argument("--depth", type=int, default=3)

    sp = add("impact-diff", "impact по текущему diff")
    sp.add_argument("--base", default="HEAD")

    sp = add("cycles", "циклы зависимостей (SCC)")
    sp.add_argument("--scope", default=None, help="префикс пути, напр. src/simulation")

    sp = add("mutators", "кто пишет в состояние")
    sp.add_argument("--state", required=True, help="поле или Class.field")

    for name in ("deadcode", "dangling", "concurrency", "layers", "coverage"):
        add(name, f"анализ: {name}")

    sp = add("hotpath", "горячий путь кадра и цена внутри")
    sp.add_argument("--depth", type=int, default=3)

    sp = add("plandrift", "дрейф PLAN.md относительно кода")
    sp.add_argument("--plan", default="PLAN.md")

    sp = add("symbol", "символ по файлу и строке")
    sp.add_argument("--file", required=True)
    sp.add_argument("--line", type=int, required=True)

    sp = add("watch", "следить за изменениями и печатать impact")
    sp.add_argument("--seconds", type=int, default=0, help="0 = бесконечно")
    sp.add_argument("--interval", type=int, default=5)
    return p


def cmd_watch(idx, args, pretty) -> int:
    deadline = time.time() + args.seconds if args.seconds else None
    last = None
    print(json.dumps({"watching": idx.repo, "interval_s": args.interval}), flush=True)
    while True:
        try:
            proc = subprocess.run(
                ["git", "-C", idx.repo, "status", "--porcelain"],
                capture_output=True, text=True, timeout=60,
            )
            cur = proc.stdout
        except (OSError, subprocess.SubprocessError) as e:
            print(json.dumps({"error": str(e)}), flush=True)
            cur = last
        if cur is not None and cur != last:
            last = cur
            changed = [l[3:].strip().strip('"') for l in cur.splitlines() if l.strip()]
            fresh = vizcore.ProjectIndex.build(idx.repo, use_cache=False)
            global _IDX
            _IDX[idx.repo] = fresh
            affected = sorted({a for f in changed for a in fresh.impact_of_file(f)["transitive"]})
            emit({"changed": changed, "affected_files": affected,
                  "affected_count": len(affected),
                  "cycles": len(fresh.cycles())}, pretty)
        if deadline and time.time() >= deadline:
            return 0
        time.sleep(max(1, args.interval))


def main(argv=None) -> int:
    args = build_parser().parse_args(argv)
    try:
        if args.cmd == "watch":
            return cmd_watch(get_index(args.repo, args.rebuild), args, args.pretty)
        idx = get_index(args.repo, args.rebuild)
        c = args.cmd
        if c == "stats":
            out = idx.stats()
        elif c == "files":
            out = idx.file_list(kind=args.ext)
        elif c == "callers":
            out = idx.callers(args.symbol)
        elif c == "callees":
            out = idx.callees(args.symbol)
        elif c == "impact":
            out = idx.impact_of_file(args.file, transitive=not args.no_transitive, max_depth=args.depth)
        elif c == "impact-diff":
            out = idx.impact_of_diff(base=args.base)
        elif c == "cycles":
            out = idx.cycles(scope=args.scope)
        elif c == "mutators":
            out = idx.mutators(args.state)
        elif c == "deadcode":
            out = idx.dead_code()
        elif c == "dangling":
            out = idx.dangling()
        elif c == "concurrency":
            out = idx.concurrency()
        elif c == "layers":
            out = idx.layer_violations()
        elif c == "coverage":
            out = idx.coverage()
        elif c == "hotpath":
            out = idx.hot_path(depth=args.depth)
        elif c == "plandrift":
            out = idx.plan_drift(plan_path=args.plan)
        elif c == "symbol":
            out = idx.symbol_at(args.file, args.line)
        else:
            print(json.dumps({"error": f"unknown command {c}"}), file=sys.stderr)
            return 2
        emit(out, args.pretty)
        return 0
    except SystemExit:
        raise
    except Exception as e:  # noqa: BLE001
        print(json.dumps({"error": f"{type(e).__name__}: {e}"}, ensure_ascii=False), file=sys.stderr)
        return 3


if __name__ == "__main__":
    sys.exit(main())
