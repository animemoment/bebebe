"""live.py -- клиент и лаунчер LiveBridge: управление РЕАЛЬНОЙ игрой Godot.

Запускает игру окном (видно на экране), шлёт команды в addons/live_bridge/live_bridge.gd
(порт 9097) и складывает скриншоты в shots/live/*.png -- их можно открыть и посмотреть.

    python tools/live.py launch                 # запустить игру окном
    python tools/live.py status                 # состояние: сцена, fps, окно, узлы
    python tools/live.py look                   # скрин + зум + список UI
    python tools/live.py shot --name eye        # один скрин на диск
    python tools/live.py click --x 960 --y 540
    python tools/live.py start                  # нажать "Играть" в меню
    python tools/live.py wait-until --expr "Engine.get_frames_per_second() > 30"
    python tools/live.py perf
    python tools/live.py stop                   # корректно выйти

Все команды печатают JSON в stdout. Код возврата: 0 успех, 1 ошибка bridge, 2 аргументы.
"""

from __future__ import annotations

import argparse
import json
import os
import socket
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.abspath(os.path.join(HERE, ".."))
STATE_FILE = os.path.join(PROJECT, ".live_bridge.json")
DEFAULT_PORT = 9097
GODOT_CANDIDATES = [
    os.environ.get("GODOT_PATH", ""),
    r"D:\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64.exe",
    r"C:\Program Files\Godot\Godot_v4.7-stable_mono_win64.exe",
]


def find_godot() -> str:
    for c in GODOT_CANDIDATES:
        if c and os.path.isfile(c):
            return c
    raise SystemExit("не найден Godot: задайте переменную GODOT_PATH")


def read_state() -> dict:
    try:
        with open(STATE_FILE, "r", encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return {}


def write_state(d: dict) -> None:
    with open(STATE_FILE, "w", encoding="utf-8") as f:
        json.dump(d, f, ensure_ascii=False, indent=2)


def port_from_args(args) -> int:
    if getattr(args, "port", None):
        return args.port
    return int(read_state().get("port", DEFAULT_PORT))


def request(cmd: str, args: dict | None = None, port: int = DEFAULT_PORT,
            timeout: float = 120.0) -> dict:
    payload = {"id": int(time.time() * 1000) % 1_000_000, "cmd": cmd, "args": args or {}}
    try:
        with socket.create_connection(("127.0.0.1", port), timeout=5) as sock:
            sock.settimeout(timeout)
            sock.sendall((json.dumps(payload) + "\n").encode("utf-8"))
            buf = b""
            while b"\n" not in buf:
                chunk = sock.recv(65536)
                if not chunk:
                    break
                buf += chunk
    except (OSError, socket.timeout) as e:
        return {"ok": False, "error": f"bridge_unreachable: {e}", "port": port}
    line = buf.split(b"\n")[0].decode("utf-8", "replace").strip()
    if not line:
        return {"ok": False, "error": "empty_response", "port": port}
    try:
        return json.loads(line)
    except ValueError:
        return {"ok": False, "error": "bad_response", "raw": line[:400]}


def wait_port(port: int, seconds: float) -> bool:
    deadline = time.time() + seconds
    while time.time() < deadline:
        r = request("ping", {}, port, timeout=3)
        if r.get("ok"):
            return True
        time.sleep(0.4)
    return False


# ------------------------------------------------------------------- commands
def cmd_launch(args) -> int:
    port = port_from_args(args)
    if wait_port(port, 0.5):
        print(json.dumps({"ok": True, "already_running": True, "port": port}))
        return 0
    exe = args.exe or find_godot()
    argv = [exe, "--path", args.project]
    if args.position:
        argv += ["--position", args.position]
    if args.resolution:
        argv += ["--resolution", args.resolution]
    argv += ["--", f"--live-port={port}"]
    log_path = os.path.join(PROJECT, "shots", "live", "godot_stdout.log")
    os.makedirs(os.path.dirname(log_path), exist_ok=True)
    logf = open(log_path, "w", encoding="utf-8", errors="replace")
    proc = subprocess.Popen(argv, cwd=args.project, stdout=logf, stderr=subprocess.STDOUT,
                            creationflags=getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0))
    write_state({"pid": proc.pid, "port": port, "project": args.project,
                 "exe": exe, "log": log_path, "argv": argv})
    ok = wait_port(port, args.wait)
    print(json.dumps({"ok": ok, "pid": proc.pid, "port": port,
                      "argv": argv, "log": log_path,
                      "alive": proc.poll() is None,
                      "error": "" if ok else "bridge did not answer in time"}, ensure_ascii=False))
    return 0 if ok else 1


def cmd_stop(args) -> int:
    port = port_from_args(args)
    r = request("quit", {}, port, timeout=10)
    print(json.dumps(r, ensure_ascii=False))
    st = read_state()
    pid = st.get("pid")
    time.sleep(1.0)
    if pid:
        try:
            alive = subprocess.run(["tasklist", "/FI", f"PID eq {pid}"],
                                   capture_output=True, text=True, timeout=20)
            if str(pid) not in alive.stdout:
                write_state({})
                return 0
        except (OSError, subprocess.SubprocessError):
            pass
    return 0 if r.get("ok") else 1


def simple(cmd: str):
    def run(args) -> int:
        payload = {}
        for field in ("x", "y", "scale", "name", "zoom", "ms", "expr", "timeout",
                      "code", "text", "code_key", "path", "on", "wx", "wy", "depth"):
            if hasattr(args, field) and getattr(args, field) is not None:
                payload[field] = getattr(args, field)
        if hasattr(args, "region") and args.region:
            payload["region"] = [int(v) for v in args.region.split(",")]
        if hasattr(args, "center_tile") and args.center_tile:
            payload["center_tile"] = [int(v) for v in args.center_tile.split(",")]
        if cmd == "key" and "code_key" in payload:
            payload["keycode"] = payload.pop("code_key")
        if cmd == "hud":
            payload = {"on": bool(args.on)}
        if cmd == "wait_until":
            payload["timeout_ms"] = int(args.timeout or 5000)
            payload["expr"] = args.expr
        r = request(cmd, payload, port_from_args(args))
        print(json.dumps(r, ensure_ascii=False, indent=1, default=str))
        return 0 if r.get("ok") else 1
    return run


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(prog="live", description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--port", type=int, default=None)
    p.add_argument("--project", default=PROJECT)
    sub = p.add_subparsers(dest="cmd", required=True)

    sp = sub.add_parser("launch", help="запустить игру окном")
    sp.add_argument("--position", default="80,80", help="позиция окна X,Y")
    sp.add_argument("--resolution", default="1280x720")
    sp.add_argument("--wait", type=float, default=45.0)
    sp.add_argument("--exe", default=None)
    sp.set_defaults(fn=cmd_launch)

    sub.add_parser("stop", help="корректно выйти").set_defaults(fn=cmd_stop)

    sp = sub.add_parser("status", help="состояние игры")
    sp.set_defaults(fn=simple("state"))

    sp = sub.add_parser("state", help="состояние игры")
    sp.set_defaults(fn=simple("state"))

    sp = sub.add_parser("perf", help="fps и счётчики кадра")
    sp.set_defaults(fn=simple("perf"))

    sp = sub.add_parser("shot", help="скриншот на диск")
    sp.add_argument("--name", default="shot")
    sp.add_argument("--scale", type=int, default=1)
    sp.add_argument("--region", default=None, help="x,y,w,h")
    sp.set_defaults(fn=simple("shot"))

    sp = sub.add_parser("look", help="скрин + зум + UI одним вызовом")
    sp.add_argument("--zoom", type=float, default=2.0)
    sp.add_argument("--center-tile", default=None, help="tx,ty")
    sp.set_defaults(fn=simple("look"))

    sp = sub.add_parser("ui", help="видимые элементы интерфейса")
    sp.set_defaults(fn=simple("ui"))

    sp = sub.add_parser("tree", help="дерево узлов")
    sp.add_argument("--path", default="/root")
    sp.add_argument("--depth", type=int, default=2)
    sp.set_defaults(fn=simple("tree"))

    sp = sub.add_parser("eval", help="одно выражение")
    sp.add_argument("--code", required=True)
    sp.set_defaults(fn=simple("eval"))

    sp = sub.add_parser("run", help="блок GDScript с return")
    sp.add_argument("--code", required=True)
    sp.set_defaults(fn=simple("run"))

    sp = sub.add_parser("click", help="клик по экранным координатам")
    sp.add_argument("--x", type=int, required=True)
    sp.add_argument("--y", type=int, required=True)
    sp.set_defaults(fn=simple("click"))

    sp = sub.add_parser("move", help="движение мыши")
    sp.add_argument("--x", type=int, required=True)
    sp.add_argument("--y", type=int, required=True)
    sp.set_defaults(fn=simple("move"))

    sp = sub.add_parser("key", help="нажатие клавиши по keycode")
    sp.add_argument("--code", type=int, required=True, dest="code_key")
    sp.set_defaults(fn=simple("key"))

    sp = sub.add_parser("text", help="ввод текста")
    sp.add_argument("--text", required=True)
    sp.set_defaults(fn=simple("text"))

    sp = sub.add_parser("wait", help="подождать мс")
    sp.add_argument("--ms", type=int, default=500)
    sp.set_defaults(fn=simple("wait"))

    sp = sub.add_parser("wait-until", help="ждать выполнения выражения")
    sp.add_argument("--expr", required=True)
    sp.add_argument("--timeout", type=int, default=5000)
    sp.set_defaults(fn=simple("wait_until"))

    sp = sub.add_parser("start", help="нажать 'Играть' в меню")
    sp.set_defaults(fn=simple("start_game"))

    sp = sub.add_parser("scene", help="сменить сцену")
    sp.add_argument("--path", required=True)
    sp.set_defaults(fn=simple("scene"))

    sp = sub.add_parser("hud", help="оверлей действий агента")
    sp.add_argument("--on", type=int, default=1)
    sp.set_defaults(fn=simple("hud"))

    sp = sub.add_parser("tile", help="игровая клетка -> экранные координаты")
    sp.add_argument("--x", type=int, required=True)
    sp.add_argument("--y", type=int, required=True)
    sp.set_defaults(fn=simple("tile_to_screen"))
    return p


def main(argv=None) -> int:
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass
    args = build_parser().parse_args(argv)
    try:
        return args.fn(args)
    except SystemExit:
        raise
    except Exception as e:  # noqa: BLE001
        print(json.dumps({"ok": False, "error": f"{type(e).__name__}: {e}"}, ensure_ascii=False),
              file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
