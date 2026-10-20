"""build_badge -- быстрый ответ на вопрос "дерево зелёное?".

Пишет build_status.json рядом с проектом и дописывает строку в build_history.jsonl.
Godot-тесты запускаются только по флагу --with-tests и только из обычного терминала:
из-под файловой песочницы агента движок падает с signal 11.

    python tools/build_badge.py                 # сборка, записать статус
    python tools/build_badge.py --status        # прочитать статус + возраст
    python tools/build_badge.py --loop --every 300
    python tools/build_badge.py --with-tests    # + headless-тесты (обычный терминал)

Коды возврата: 0 зелено, 1 ошибки сборки/тестов, 2 внутренняя ошибка.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.abspath(os.path.join(HERE, ".."))
STATUS = os.path.join(PROJECT, "build_status.json")
HISTORY = os.path.join(PROJECT, "build_history.jsonl")
TESTS = [
    "tests/run_zone_model_test.gd",
    "tests/run_zone_router_test.gd",
    "tests/run_zone_agents_test.gd",
]


def _git(*args: str) -> str:
    try:
        out = subprocess.run(["git", "-C", PROJECT, *args],
                             capture_output=True, text=True, timeout=60)
        return out.stdout.strip()
    except (OSError, subprocess.SubprocessError):
        return ""


def run_build() -> dict:
    t0 = time.perf_counter()
    try:
        proc = subprocess.run(
            ["dotnet", "build", "Game.csproj", "-v", "q", "-nologo"],
            cwd=PROJECT, capture_output=True, text=True, timeout=1800,
        )
        out = (proc.stdout or "") + (proc.stderr or "")
        rc = proc.returncode
    except (OSError, subprocess.SubprocessError) as e:
        return {"build_ok": False, "errors": [f"не удалось запустить dotnet: {e}"],
                "warnings_count": 0, "duration_s": round(time.perf_counter() - t0, 1)}
    errors = [ln.strip() for ln in out.splitlines() if "error CS" in ln]
    warns = [ln for ln in out.splitlines() if "warning CS" in ln]
    ok = rc == 0 and not errors
    return {"build_ok": ok, "errors": errors[:50], "warnings_count": len(warns),
            "duration_s": round(time.perf_counter() - t0, 1)}


def run_tests() -> dict:
    godot = os.environ.get("GODOT_PATH", "godot")
    results = []
    for t in TESTS:
        t0 = time.perf_counter()
        try:
            proc = subprocess.run(
                [godot, "--headless", "--path", PROJECT, "--script", "res://" + t],
                capture_output=True, text=True, timeout=900,
            )
            rc = proc.returncode
            tail = ((proc.stdout or "") + (proc.stderr or "")).strip().splitlines()[-3:]
        except (OSError, subprocess.SubprocessError) as e:
            rc, tail = 127, [str(e)]
        results.append({"test": t, "ok": rc == 0, "code": rc,
                        "duration_s": round(time.perf_counter() - t0, 1),
                        "tail": tail})
    return {"tests": results, "tests_ok": all(r["ok"] for r in results)}


def write_status(payload: dict) -> None:
    payload["timestamp"] = dt.datetime.now().astimezone().isoformat(timespec="seconds")
    payload["git_commit"] = _git("rev-parse", "--short", "HEAD")
    payload["branch"] = _git("rev-parse", "--abbrev-ref", "HEAD")
    payload["dirty"] = bool(_git("status", "--porcelain"))
    payload["host"] = os.environ.get("COMPUTERNAME", "unknown")
    with open(STATUS, "w", encoding="utf-8") as f:
        json.dump(payload, f, ensure_ascii=False, indent=2)
    with open(HISTORY, "a", encoding="utf-8") as f:
        f.write(json.dumps(payload, ensure_ascii=False) + "\n")


def print_status() -> int:
    if not os.path.exists(STATUS):
        print(json.dumps({"error": "build_status.json отсутствует — запустите без флагов"}))
        return 2
    with open(STATUS, "r", encoding="utf-8") as f:
        st = json.load(f)
    try:
        age = (dt.datetime.now().astimezone()
               - dt.datetime.fromisoformat(st["timestamp"])).total_seconds()
        st["age_s"] = round(age, 0)
    except (KeyError, ValueError):
        pass
    print(json.dumps(st, ensure_ascii=False, indent=2))
    tests_ok = st.get("tests_ok")
    return 0 if st.get("build_ok") and tests_ok is not False else 1


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(prog="build_badge", description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--status", action="store_true", help="показать последний статус")
    ap.add_argument("--loop", action="store_true", help="крутиться в фоне")
    ap.add_argument("--every", type=int, default=300, help="интервал цикла, сек")
    ap.add_argument("--with-tests", action="store_true",
                    help="дополнительно прогнать headless-тесты Godot "
                         "(только из обычного терминала, не из-под песочницы агента)")
    args = ap.parse_args(argv)

    if args.status:
        return print_status()

    def cycle() -> int:
        payload = run_build()
        if args.with_tests:
            payload.update(run_tests())
        else:
            payload["tests_ok"] = None
        write_status(payload)
        print(json.dumps({"build_ok": payload.get("build_ok"),
                          "errors": len(payload.get("errors") or []),
                          "warnings": payload.get("warnings_count"),
                          "tests_ok": payload.get("tests_ok"),
                          "duration_s": payload.get("duration_s"),
                          "commit": payload.get("git_commit")}, ensure_ascii=False))
        return 0 if payload.get("build_ok") else 1

    try:
        if not args.loop:
            return cycle()
        while True:
            try:
                cycle()
            except Exception as e:  # noqa: BLE001
                print(json.dumps({"error": str(e)}), flush=True)
            time.sleep(max(5, args.every))
    except KeyboardInterrupt:
        return 0
    except Exception as e:  # noqa: BLE001
        print(json.dumps({"error": f"{type(e).__name__}: {e}"}), file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
