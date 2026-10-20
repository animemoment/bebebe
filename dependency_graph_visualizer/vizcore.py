"""vizcore -- индекс зависимостей C#/GDScript/сцен для агентских запросов.

Только stdlib. Строит граф один раз, кэширует в .viz_cache.json, отдаёт
структурные ответы (dict/list), пригодные для JSON.

Публичный API:
    idx = ProjectIndex.build(repo, ignore=None, use_cache=True)
    idx.callers(symbol)                 -> [{file,line,owner,method}]
    idx.callees(symbol)                 -> [{file,line,owner,method}]
    idx.impact_of_file(path, transitive=True, max_depth=6) -> dict
    idx.impact_of_diff(base="HEAD")     -> dict
    idx.cycles(scope=None)              -> [{id,kind,nodes}]
    idx.mutators(state)                 -> [{file,line,owner,expr}]
    idx.dead_code()                     -> dict
    idx.dangling()                      -> dict
    idx.concurrency()                   -> dict
    idx.hot_path(depth=3)               -> dict
    idx.layer_violations()              -> dict
    idx.coverage()                      -> dict
    idx.plan_drift(plan_path="PLAN.md") -> dict
    idx.symbol_at(file, line)           -> dict|None
    idx.stats()                         -> dict
"""

from __future__ import annotations

import hashlib
import json
import os
import re
import subprocess
from collections import Counter, defaultdict

EXTS = {".cs", ".gd", ".tscn"}
CACHE_NAME = ".viz_cache.json"
CACHE_VERSION = 6

DEFAULT_IGNORE = [
    ".git/*", ".godot/*", "bin/*", "obj/*", "node_modules/*", "*/.venv/*",
    "__pycache__/*", "*/.vs/*", "*.import", "*.tmp", "*.bak", "*.exe",
    "dependency_graph_visualizer/*", "_tools/*", "shots/*", "media/*",
]

LAYER_RULES = [
    ("src/simulation", "simulation"),
    ("src/core", "core"),
    ("src/ui", "ui"),
    ("src/util", "util"),
    ("src/sdk", "sdk"),
    ("src/", "src"),
    ("autoload", "autoload"),
    ("addons", "addons"),
    ("tests", "tests"),
    ("scenes", "scenes"),
    ("tools", "tools"),
    ("docs", "docs"),
]

# Чем выше число, тем выше слой. Зависимость ВВЕРХ (на большее число) -- нарушение.
LAYER_ORDER = {
    "core": 0, "util": 1, "sdk": 2, "src": 3, "simulation": 3, "other": 3,
    "autoload": 4, "ui": 5, "scenes": 6,
    "addons": 9, "tests": 9, "tools": 9, "docs": 9,
}

ENTRY_METHODS = {
    "_Ready", "_Process", "_PhysicsProcess", "_Input", "_UnhandledInput",
    "_Draw", "_EnterTree", "_ExitTree", "_Notification", "_GuiInput",
    "_ready", "_process", "_physics_process", "_input", "_unhandled_input",
    "_draw", "_notification", "_gui_input", "Run", "Tick", "Update",
    "Dispatch", "OnProcess", "ProcessFrame", "Step",
}

CS_KEYWORDS = {
    "if", "for", "foreach", "while", "switch", "catch", "using", "lock",
    "return", "new", "sizeof", "typeof", "nameof", "base", "this", "throw",
    "else", "do", "try", "when", "is", "as", "in", "out", "ref", "fixed",
}

RE_CS_TYPE = re.compile(r"\b(?:class|struct|record|interface|enum)\s+([A-Za-z_]\w*)")
RE_CS_METHOD = re.compile(
    r"^[ \t]*(?:\[[^\]]*\][ \t]*)*"
    r"(?:(?:public|private|protected|internal|static|virtual|override|abstract|"
    r"sealed|partial|async|new|extern|unsafe|readonly|const)\s+)+"
    r"[\w<>\[\],\.\?]+\s+([A-Za-z_]\w*)\s*\(([^;{)]*)\)\s*(?:\{|=>)?",
    re.MULTILINE,
)
RE_GD_FUNC = re.compile(r"^[ \t]*(?:static\s+)?func\s+([A-Za-z_]\w*)\s*\(([^)]*)\)", re.MULTILINE)
RE_GD_CLASS = re.compile(r"^[ \t]*class_name\s+([A-Za-z_]\w*)", re.MULTILINE)
RE_CALL = re.compile(r"\b([A-Za-z_]\w*)\s*\(")
RE_FIELD_WRITE = re.compile(
    r"\b([A-Za-z_]\w*)\s*\.\s*([A-Za-z_]\w*)\s*(?:=(?!=)|\+=|-=|\*=|/=|\+\+|--)"
)
RE_INTERLOCKED = re.compile(r"Interlocked\.\w+\(\s*ref\s+([A-Za-z_]\w*)\s*\.\s*([A-Za-z_]\w*)")
RE_INTERLOCKED_BARE = re.compile(r"Interlocked\.\w+\(\s*ref\s+([A-Za-z_]\w*)\b")
RE_BARE_ASSIGN = re.compile(r"^\s*([A-Za-z_]\w*)\s*(?:=(?!=)|\+=|-=|\*=|/=|\+\+|--)")
RE_TS_EXT = re.compile(r'\[ext_resource[^\]]*?path="res://([^"]+)"')
RE_TS_CONN = re.compile(r'\[connection[^\]]*?method="([^"]+)"')
RE_TS_NODE = re.compile(r'\[node\s+name="([^"]+)"')
RE_LOOP = re.compile(r"\b(?:for|foreach|while)\b")
RE_ALLOC = re.compile(r"\bnew\s+[A-Za-z_]|\bnew\s*\[|\.ToList\(|\.ToArray\(|\.Select\(|\.Where\(")
RE_DECL = re.compile(r"\b(?:class|struct|record|interface)\s+([A-Za-z_]\w*)")

CONCURRENCY_MARKERS = {
    "static_singleton": re.compile(r"\bstatic\s+(?:readonly\s+)?\w[\w<>\[\]]*\s+Instance\b"),
    "lock": re.compile(r"\block\s*\("),
    "interlocked": re.compile(r"\bInterlocked\.\w+"),
    "concurrent_collection": re.compile(r"\bConcurrent(?:Queue|Dictionary|Bag|Stack)\b"),
    "thread": re.compile(r"\bnew\s+Thread\b|\bThreadPool\.|\bTask\.Run\b|\bThread\("),
    "volatile": re.compile(r"\bvolatile\b"),
    "jobsystem": re.compile(r"\bJobSystem\b|\bParallel\."),
}

PLAN_REF = re.compile(r"\b((?:src|tests|scenes|autoload|addons|tools|docs)/[\w/\-\.]+\.(?:cs|gd|tscn))(?::(\d+))?")


def classify_layer(rel: str) -> str:
    rel = rel.replace("\\", "/")
    for prefix, layer in LAYER_RULES:
        if rel.startswith(prefix):
            return layer
    return "other"


def _match_ignore(rel: str, patterns) -> bool:
    import fnmatch

    for pat in patterns:
        if fnmatch.fnmatch(rel, pat) or fnmatch.fnmatch(rel, pat.rstrip("/*") + "/*"):
            return True
        if pat.endswith("/*") and rel.startswith(pat[:-1]):
            return True
    return False


def _brace_body(text: str, start: int) -> tuple[int, int]:
    """Возвращает (body_start, body_end) для C#-блока, начиная с позиции '{'."""
    i = text.find("{", start)
    if i < 0:
        return (-1, -1)
    depth = 0
    j = i
    in_str = False
    in_chr = False
    while j < len(text):
        ch = text[j]
        if in_str:
            if ch == "\\":
                j += 2
                continue
            if ch == '"':
                in_str = False
        elif in_chr:
            if ch == "\\":
                j += 2
                continue
            if ch == "'":
                in_chr = False
        elif ch == '"':
            in_str = True
        elif ch == "'":
            in_chr = True
        elif ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
            if depth == 0:
                return (i, j + 1)
        j += 1
    return (i, len(text))


def _gd_body(text: str, line_no: int) -> tuple[int, int]:
    """Тело GDScript-функции: до строки с тем же или меньшим отступом."""
    lines = text.splitlines()
    if line_no >= len(lines):
        return (-1, -1)
    base = len(lines[line_no]) - len(lines[line_no].lstrip())
    start = line_no + 1
    end = start
    for i in range(start, len(lines)):
        raw = lines[i]
        if not raw.strip():
            end = i + 1
            continue
        ind = len(raw) - len(raw.lstrip())
        if ind <= base:
            break
        end = i + 1
    return (start, end)


def _line_of(text: str, pos: int) -> int:
    return text.count("\n", 0, pos) + 1


class ProjectIndex:
    def __init__(self, repo: str):
        self.repo = os.path.abspath(repo)
        self.files = {}          # rel -> meta
        self.symbols = defaultdict(set)   # name -> {rel}
        self.symbol_lines = defaultdict(list)  # name -> [(rel, line, owner)]
        self.edges = defaultdict(Counter)      # rel -> {rel: count}
        self.rev = defaultdict(set)            # rel -> {rel}
        self.errors = []
        self.scenes = {}

    # ------------------------------------------------------------------ build
    @classmethod
    def build(cls, repo: str, ignore=None, use_cache: bool = True) -> "ProjectIndex":
        idx = cls(repo)
        scan = idx._scan(ignore)
        if use_cache:
            cached = idx._load_cache(scan)
            if cached:
                idx._restore(cached)
                return idx
        idx._parse_all(scan)
        idx._build_graph()
        idx._save_cache(scan)
        return idx

    def _scan(self, ignore):
        patterns = list(DEFAULT_IGNORE) + list(ignore or [])
        out = []
        for root, dirs, names in os.walk(self.repo):
            dirs[:] = [d for d in dirs if d not in (".git", ".godot", "bin", "obj", "node_modules", ".venv", "__pycache__", ".vs")]
            for nm in names:
                ext = os.path.splitext(nm)[1].lower()
                if ext not in EXTS:
                    continue
                full = os.path.join(root, nm)
                rel = os.path.relpath(full, self.repo).replace("\\", "/")
                if _match_ignore(rel, patterns):
                    continue
                try:
                    st = os.stat(full)
                except OSError:
                    continue
                out.append({"rel": rel, "size": st.st_size, "mtime": int(st.st_mtime)})
        out.sort(key=lambda r: r["rel"])
        return out

    @staticmethod
    def _cache_key(scan) -> str:
        h = hashlib.sha1()
        h.update(f"v{CACHE_VERSION}".encode())
        for r in scan:
            h.update(f"{r['rel']}:{r['size']}:{r['mtime']};".encode())
        return h.hexdigest()

    def _cache_path(self) -> str:
        return os.path.join(self.repo, CACHE_NAME)

    def _load_cache(self, scan):
        try:
            with open(self._cache_path(), "r", encoding="utf-8") as f:
                data = json.load(f)
        except (OSError, ValueError):
            return None
        if data.get("key") != self._cache_key(scan):
            return None
        return data

    def _save_cache(self, scan):
        payload = {
            "key": self._cache_key(scan),
            "version": CACHE_VERSION,
            "files": self.files,
            "symbols": {k: sorted(v) for k, v in self.symbols.items()},
            "symbol_lines": {k: v for k, v in self.symbol_lines.items()},
            "edges": {k: dict(v) for k, v in self.edges.items()},
            "scenes": self.scenes,
            "errors": self.errors[:50],
        }
        try:
            with open(self._cache_path(), "w", encoding="utf-8") as f:
                json.dump(payload, f, ensure_ascii=False)
        except OSError:
            pass

    def _restore(self, data):
        self.files = {k: v for k, v in data.get("files", {}).items()}
        self.symbols = defaultdict(set)
        for k, v in data.get("symbols", {}).items():
            self.symbols[k] = set(v)
        self.symbol_lines = defaultdict(list)
        for k, v in data.get("symbol_lines", {}).items():
            self.symbol_lines[k] = [tuple(x) for x in v]
        self.edges = defaultdict(Counter)
        for k, v in data.get("edges", {}).items():
            self.edges[k] = Counter(v)
        self.rev = defaultdict(set)
        for src, targets in self.edges.items():
            for dst in targets:
                self.rev[dst].add(src)
        self.scenes = data.get("scenes", {})
        self.errors = data.get("errors", [])

    def _parse_all(self, scan):
        for item in scan:
            rel = item["rel"]
            full = os.path.join(self.repo, rel)
            try:
                with open(full, "r", encoding="utf-8", errors="replace") as f:
                    text = f.read()
            except OSError as e:
                self.errors.append(f"{rel}: {e}")
                continue
            ext = os.path.splitext(rel)[1].lower()
            try:
                if ext == ".cs":
                    meta = self._parse_cs(rel, text)
                elif ext == ".gd":
                    meta = self._parse_gd(rel, text)
                else:
                    meta = self._parse_tscn(rel, text)
            except Exception as e:  # noqa: BLE001 -- битый файл не должен валить индекс
                self.errors.append(f"{rel}: {type(e).__name__}: {e}")
                continue
            meta["layer"] = classify_layer(rel)
            meta["size"] = item["size"]
            self.files[rel] = meta
            for sym in meta.get("types", []):
                self.symbols[sym].add(rel)
            for m in meta.get("methods", []):
                self.symbols[m["name"]].add(rel)
                self.symbol_lines[m["name"]].append((rel, m["line"], m.get("owner", "")))

    # ---------------------------------------------------------------- parsers
    def _parse_cs(self, rel, text):
        types = RE_CS_TYPE.findall(text)
        methods = []
        for m in RE_CS_METHOD.finditer(text):
            name, params = m.group(1), m.group(2) or ""
            if name in CS_KEYWORDS or name in ("get", "set", "add", "remove"):
                continue
            b0, b1 = _brace_body(text, m.end())
            body = text[b0:b1] if b0 >= 0 else ""
            owner = self._owner_at(text, m.start(), types)
            methods.append({
                "name": name,
                "line": _line_of(text, m.start()),
                "owner": owner,
                "params": params.strip(),
                "loops": len(RE_LOOP.findall(body)),
                "allocs": len(RE_ALLOC.findall(body)),
                "size": len(body),
            })
        calls = set()
        for c in RE_CALL.finditer(text):
            nm = c.group(1)
            if nm in CS_KEYWORDS:
                continue
            calls.add(nm)
        writes = [(m0.group(1), m0.group(2), _line_of(text, m0.start()))
                  for m0 in RE_FIELD_WRITE.finditer(text)]
        writes += [(m0.group(1), m0.group(2), _line_of(text, m0.start()))
                   for m0 in RE_INTERLOCKED.finditer(text)]
        for i, line in enumerate(text.splitlines(), 1):
            mb = RE_BARE_ASSIGN.match(line)
            if mb:
                writes.append(("", mb.group(1), i))
            for mn in RE_INTERLOCKED_BARE.finditer(line):
                writes.append(("", mn.group(1), i))
        markers = {}
        for key, rx in CONCURRENCY_MARKERS.items():
            n = len(rx.findall(text))
            if n:
                markers[key] = n
        using = re.findall(r"^\s*using\s+([\w\.]+)\s*;", text, re.MULTILINE)
        return {
            "ext": ".cs", "types": types, "methods": methods,
            "calls": sorted(calls), "writes": writes, "markers": markers,
            "imports": using, "lines": text.count("\n") + 1,
        }

    def _parse_gd(self, rel, text):
        types = RE_GD_CLASS.findall(text) or [os.path.splitext(os.path.basename(rel))[0]]
        methods = []
        lines = text.splitlines()
        for m in RE_GD_FUNC.finditer(text):
            name = m.group(1)
            line = _line_of(text, m.start())
            b0, b1 = _gd_body(text, line - 1)
            body = "\n".join(lines[b0:b1]) if b0 >= 0 else ""
            methods.append({
                "name": name, "line": line, "owner": types[0] if types else "",
                "params": (m.group(2) or "").strip(),
                "loops": len(RE_LOOP.findall(body)),
                "allocs": len(RE_ALLOC.findall(body)),
                "size": len(body),
            })
        calls = {c.group(1) for c in RE_CALL.finditer(text) if c.group(1) not in CS_KEYWORDS}
        writes = [(m0.group(1), m0.group(2), _line_of(text, m0.start()))
                  for m0 in RE_FIELD_WRITE.finditer(text)]
        for i, line in enumerate(lines, 1):
            mb = RE_BARE_ASSIGN.match(line)
            if mb:
                writes.append(("", mb.group(1), i))
        markers = {}
        for key, rx in CONCURRENCY_MARKERS.items():
            n = len(rx.findall(text))
            if n:
                markers[key] = n
        return {
            "ext": ".gd", "types": types, "methods": methods, "calls": sorted(calls),
            "writes": writes, "markers": markers,
            "imports": re.findall(r'^\s*(?:const|var)\s+\w+\s*=\s*preload\("([^"]+)"\)', text, re.MULTILINE),
            "lines": len(lines),
        }

    def _parse_tscn(self, rel, text):
        refs = RE_TS_EXT.findall(text)
        methods = []
        for m in RE_TS_CONN.finditer(text):
            methods.append({"name": m.group(1), "line": _line_of(text, m.start()),
                            "owner": "", "params": "", "loops": 0, "allocs": 0, "size": 0})
        nodes = RE_TS_NODE.findall(text)
        self.scenes[rel] = {"refs": refs, "nodes": len(nodes)}
        return {
            "ext": ".tscn", "types": [], "methods": methods, "calls": [],
            "writes": [], "markers": {}, "imports": refs, "lines": text.count("\n") + 1,
            "nodes": len(nodes),
        }

    @staticmethod
    def _owner_at(text: str, pos: int, types) -> str:
        head = text[:pos]
        found = ""
        for m in RE_DECL.finditer(head):
            found = m.group(1)
        return found

    # ------------------------------------------------------------------ graph
    def _build_graph(self):
        # Символ, объявленный в нескольких файлах (Update, Get, ...), не создаёт
        # ребра: по имени нельзя понять, чей именно метод вызван. Строгий режим
        # даёт меньше связей, но каждая -- настоящая.
        GENERIC_LIMIT = 1
        for rel, meta in self.files.items():
            targets = Counter()
            for nm in meta.get("calls", []):
                holders = self.symbols.get(nm)
                if not holders or len(holders) > GENERIC_LIMIT:
                    continue
                for tgt in holders:
                    if tgt != rel:
                        targets[tgt] += 1
            if meta.get("ext") == ".tscn":
                base_dir = os.path.dirname(rel)
                for ref in meta.get("imports", []):
                    ref = ref.replace("\\", "/").lstrip("/")
                    cand = ref if ref in self.files else os.path.normpath(
                        os.path.join(base_dir, ref)).replace("\\", "/")
                    if cand in self.files and cand != rel:
                        targets[cand] += 1
            tgt_map = {}
            for t, cnt in targets.items():
                tgt_map[t] = cnt
                self.rev[t].add(rel)
            self.edges[rel] = Counter(tgt_map)

    # ---------------------------------------------------------------- queries
    def _sym_files(self, symbol: str):
        files = set(self.symbols.get(symbol, ()))
        if not files:
            short = symbol.split(".")[-1].split("(")[0]
            files = set(self.symbols.get(short, ()))
        return files

    def callers(self, symbol: str, kind: str | None = None) -> list:
        target_files = self._sym_files(symbol)
        short = symbol.split(".")[-1].split("(")[0]
        out = []
        for rel, meta in self.files.items():
            if short in set(meta.get("calls", [])):
                owner = ""
                for m in meta.get("methods", []):
                    if m["name"] == short:
                        owner = m.get("owner", "")
                        break
                out.append({"file": rel, "line": self._first_call_line(rel, short),
                            "owner": owner, "method": short,
                            "kind": "definition" if rel in target_files else "call_site"})
        if kind:
            out = [r for r in out if r["kind"] == kind]
        out.sort(key=lambda r: (r["file"], r["line"]))
        return out

    def callees(self, symbol: str) -> list:
        rels = self._sym_files(symbol)
        out = []
        for rel in rels:
            for tgt, cnt in (self.edges.get(rel) or {}).items():
                out.append({"file": tgt, "from": rel, "refs": cnt,
                            "layer": self.files.get(tgt, {}).get("layer", "other")})
        out.sort(key=lambda r: -r["refs"])
        return out

    def _first_call_line(self, rel, name):
        try:
            with open(os.path.join(self.repo, rel), "r", encoding="utf-8", errors="replace") as f:
                for i, line in enumerate(f, 1):
                    if re.search(r"\b" + re.escape(name) + r"\s*\(", line):
                        return i
        except OSError:
            pass
        return 0

    def impact_of_file(self, path: str, transitive: bool = True, max_depth: int = 3) -> dict:
        path = path.replace("\\", "/").lstrip("./")
        if path not in self.files:
            for rel in self.files:
                if rel.endswith(path):
                    path = rel
                    break
        direct = sorted(self.rev.get(path, set())) if path in self.files else []
        seen = {path}
        frontier = list(direct)
        levels = {path: 0}
        for f in direct:
            levels[f] = 1
        depth = 1
        while transitive and frontier and depth < max_depth:
            nxt = []
            for f in frontier:
                for src in self.rev.get(f, ()):  # noqa: PLC0206
                    if src not in seen:
                        seen.add(src)
                        levels[src] = depth + 1
                        nxt.append(src)
            frontier = nxt
            depth += 1
        affected = seen - {path}
        by_layer = Counter(self.files.get(f, {}).get("layer", "other") for f in affected)
        scc_size = 0
        for c in self.cycles():
            if path in c["nodes"]:
                scc_size = max(scc_size, c["size"])
                break
        return {
            "changed": [path],
            "direct": direct,
            "transitive": sorted(affected),
            "files_count": len(affected),
            "max_depth_reached": max(levels.values()) if levels else 0,
            "by_layer": dict(by_layer),
            "in_cycle": scc_size > 1,
            "scc_size": scc_size,
            "note": ("файл входит в цикл сильной связности: транзитивный охват завышен"
                     if scc_size > 1 else ""),
            "tests_touching": sorted(f for f in affected if f.startswith("tests/")),
        }

    def impact_of_diff(self, repo_path: str | None = None, base: str = "HEAD") -> dict:
        repo = repo_path or self.repo
        try:
            proc = subprocess.run(
                ["git", "-C", repo, "diff", "--name-only", base],
                capture_output=True, text=True, timeout=120,
            )
        except (OSError, subprocess.SubprocessError) as e:
            return {"error": f"git failed: {e}", "changed": []}
        changed = [p.strip().replace("\\", "/") for p in proc.stdout.splitlines() if p.strip()]
        changed = [p for p in changed if os.path.splitext(p)[1].lower() in EXTS]
        merged = {"changed": changed, "per_file": {}, "transitive": [], "files_count": 0}
        seen = set()
        for p in changed:
            r = self.impact_of_file(p)
            merged["per_file"][p] = r
            seen.update(r["transitive"])
            seen.update(r["direct"])
        merged["transitive"] = sorted(seen)
        merged["files_count"] = len(seen)
        merged["by_layer"] = dict(Counter(self.files.get(f, {}).get("layer", "other") for f in seen))
        return merged

    def cycles(self, scope: str | None = None) -> list:
        nodes = set(self.files)
        if scope:
            nodes = {n for n in nodes if n.startswith(scope)}
        index = {}
        low = {}
        on_stack = {}
        stack = []
        result = []
        counter = [0]

        def strongconnect(v):
            work = [(v, iter(self.edges.get(v, ())))]
            index[v] = low[v] = counter[0]
            counter[0] += 1
            stack.append(v)
            on_stack[v] = True
            while work:
                node, it = work[-1]
                advanced = False
                for w in it:
                    if w not in nodes:
                        continue
                    if w not in index:
                        index[w] = low[w] = counter[0]
                        counter[0] += 1
                        stack.append(w)
                        on_stack[w] = True
                        work.append((w, iter(self.edges.get(w, ()))))
                        advanced = True
                        break
                    if on_stack.get(w):
                        low[node] = min(low[node], index[w])
                if advanced:
                    continue
                work.pop()
                if work:
                    parent = work[-1][0]
                    low[parent] = min(low[parent], low[node])
                if low[node] == index[node]:
                    comp = []
                    while True:
                        w = stack.pop()
                        on_stack[w] = False
                        comp.append(w)
                        if w == node:
                            break
                    if len(comp) > 1:
                        result.append({"kind": "file", "size": len(comp), "nodes": sorted(comp)})
                    elif node in (self.edges.get(node) or {}):
                        result.append({"kind": "self", "size": 1, "nodes": [node]})

        for v in sorted(nodes):
            if v not in index:
                strongconnect(v)
        result.sort(key=lambda r: (-r["size"], r["nodes"][0]))
        for i, r in enumerate(result):
            r["id"] = f"c{i + 1}"
        return result

    def mutators(self, state: str) -> list:
        if "." in state:
            cls, field_name = state.split(".", 1)
        else:
            cls, field_name = "", state
        out = []
        for rel, meta in self.files.items():
            if cls and cls.lower() not in rel.lower() and cls not in " ".join(meta.get("types", [])):
                continue
            for obj, fld, line in meta.get("writes", []):
                if fld != field_name:
                    continue
                if cls and obj not in (cls, "this", "_instance", "Instance"):
                    continue
                out.append({"file": rel, "line": line, "owner": obj, "expr": f"{obj}.{fld}"})
        out.sort(key=lambda r: (r["file"], r["line"]))
        return out

    def dead_code(self) -> dict:
        entry_suffixes = ("Main.cs", "StructureRegistry.cs")
        unreferenced = []
        for rel, meta in self.files.items():
            if meta.get("ext") == ".cs" and not rel.startswith(("tests/", "addons/")):
                if not self.rev.get(rel) and any(m["name"] in ENTRY_METHODS for m in meta.get("methods", [])):
                    continue
            if self.rev.get(rel):
                continue
            if rel.startswith(("tests/", "addons/", "tools/", "scenes/", "docs/", "autoload/")):
                continue
            if rel.endswith(entry_suffixes):
                continue
            unreferenced.append({"file": rel, "layer": meta.get("layer", "other"),
                                 "types": meta.get("types", [])})
        strings = self._string_refs()
        orphan_types = []
        for rel, meta in self.files.items():
            for t in meta.get("types", []):
                if not self.rev.get(rel) and t not in strings and rel not in strings:
                    orphan_types.append({"type": t, "file": rel})
        return {
            "unreferenced_files": sorted(unreferenced, key=lambda r: r["file"]),
            "orphan_types": orphan_types[:200],
            "scenes_not_referenced": sorted(
                s for s in self.scenes
                if not self.rev.get(s) and "main" not in s.lower()),
        }

    def _string_refs(self) -> set:
        out = set()
        try:
            proc = subprocess.run(
                ["git", "-C", self.repo, "grep", "-h", "-o", "-E",
                 r"[A-Za-z_][A-Za-z0-9_\.]{3,}", "--", "*.cs", "*.gd", "*.tscn", "*.json", "*.godot"],
                capture_output=True, text=True, timeout=180,
            )
            if proc.returncode == 0:
                out = {w.split(".")[-1] for w in proc.stdout.split()}
        except (OSError, subprocess.SubprocessError):
            pass
        return out

    def dangling(self) -> dict:
        missing_refs = []
        for rel, meta in self.files.items():
            if meta.get("ext") != ".tscn":
                continue
            for ref in meta.get("imports", []):
                ref = ref.replace("\\", "/").lstrip("/")
                if ref.startswith("res://"):
                    ref = ref[len("res://"):]
                if ref and not os.path.exists(os.path.join(self.repo, ref)):
                    missing_refs.append({"scene": rel, "ref": ref})
        known_methods = set()
        for meta in self.files.values():
            for m in meta.get("methods", []):
                known_methods.add(m["name"])
        missing_connections = []
        for rel, meta in self.files.items():
            if meta.get("ext") != ".tscn":
                continue
            for m in meta.get("methods", []):
                if m["name"] and m["name"] not in known_methods:
                    missing_connections.append({"scene": rel, "line": m["line"], "method": m["name"]})
        return {"missing_resource_refs": missing_refs,
                "connections_without_method": missing_connections}

    def concurrency(self) -> dict:
        rows = []
        for rel, meta in self.files.items():
            markers = meta.get("markers") or {}
            if not markers:
                continue
            shared = [f for (o, f, _l) in meta.get("writes", []) if o in ("_instance", "Instance", "this")]
            rows.append({
                "file": rel,
                "layer": meta.get("layer", "other"),
                "markers": markers,
                "shared_field_writes": len(shared),
                "types": meta.get("types", []),
                "risk": (3 if "interlocked" in markers and "lock" not in markers else 2)
                        + (1 if "static_singleton" in markers else 0),
            })
        rows.sort(key=lambda r: (-r["risk"], -r["shared_field_writes"], r["file"]))
        return {"files": rows, "count": len(rows),
                "top": rows[:15],
                "global_state_mutators": [
                    r for r in rows if "static_singleton" in r["markers"]
                ][:20]}

    def hot_path(self, depth: int = 3) -> dict:
        entries = []
        for rel, meta in self.files.items():
            for m in meta.get("methods", []):
                if m["name"] in ENTRY_METHODS:
                    entries.append({"file": rel, "method": m["name"], "line": m["line"]})
        seen = {}
        frontier = [(e["file"], 0) for e in entries]
        while frontier:
            rel, d = frontier.pop()
            if rel in seen and seen[rel] <= d:
                continue
            seen[rel] = d
            if d >= depth:
                continue
            for tgt in (self.edges.get(rel) or {}):
                frontier.append((tgt, d + 1))
        hot = []
        for rel, d in seen.items():
            meta = self.files.get(rel, {})
            for m in meta.get("methods", []):
                if m.get("loops", 0) or m.get("allocs", 0):
                    cost = 140 + m.get("loops", 0) * 2500 + m.get("allocs", 0) * 700 + m.get("size", 0) // 200 * 150
                    hot.append({
                        "file": rel, "method": m["name"], "line": m["line"],
                        "depth": d, "loops": m.get("loops", 0), "allocs": m.get("allocs", 0),
                        "size": m.get("size", 0), "cost_estimate": cost,
                        "layer": meta.get("layer", "other"),
                    })
        hot.sort(key=lambda r: (-r["cost_estimate"], r["file"]))
        return {"entry_points": entries[:60], "reachable_files": len(seen),
                "hot_methods": hot[:40], "hot_count": len(hot)}

    def layer_violations(self) -> dict:
        viol = []
        for src, targets in self.edges.items():
            sl = self.files.get(src, {}).get("layer", "other")
            for dst, cnt in targets.items():
                dl = self.files.get(dst, {}).get("layer", "other")
                if sl == dl:
                    continue
                if sl in ("tests", "tools", "docs", "addons", "scenes") or dl in ("tests", "tools", "docs", "addons", "scenes"):
                    continue
                if LAYER_ORDER.get(dl, 3) > LAYER_ORDER.get(sl, 3):
                    viol.append({"from": src, "from_layer": sl, "to": dst,
                                 "to_layer": dl, "refs": cnt})
        viol.sort(key=lambda r: -r["refs"])
        agg = Counter(f"{v['from_layer']} -> {v['to_layer']}" for v in viol)
        return {"violations": viol[:100], "by_pair": dict(agg), "count": len(viol)}

    def coverage(self) -> dict:
        tested = defaultdict(set)
        for rel, meta in self.files.items():
            if not rel.startswith("tests/"):
                continue
            for nm in meta.get("calls", []):
                for tgt in self.symbols.get(nm, ()):  # noqa: PLC0206
                    if not tgt.startswith("tests/"):
                        tested[tgt].add(rel)
        src = [r for r in self.files if r.startswith("src/")]
        covered = [r for r in src if r in tested]
        hot = {h["file"] for h in self.hot_path()["hot_methods"]}
        return {
            "src_files": len(src),
            "covered": len(covered),
            "percent": round(100.0 * len(covered) / max(1, len(src)), 1),
            "untested_hot_files": sorted(hot - set(covered)),
            "untested": sorted(set(src) - set(covered))[:200],
        }

    def plan_drift(self, plan_path: str = "PLAN.md") -> dict:
        full = os.path.join(self.repo, plan_path)
        if not os.path.exists(full):
            return {"error": f"{plan_path} not found"}
        with open(full, "r", encoding="utf-8", errors="replace") as f:
            text = f.read()
        refs = []
        section = ""
        for i, line in enumerate(text.splitlines(), 1):
            if line.startswith("#"):
                section = line.strip("# ").strip()[:80]
            for m in PLAN_REF.finditer(line):
                path, ln = m.group(1), m.group(2)
                exists = os.path.exists(os.path.join(self.repo, path))
                status = "ok"
                if not exists:
                    status = "missing_file"
                elif ln:
                    total = self.files.get(path, {}).get("lines", 0)
                    if total and int(ln) > total:
                        status = "line_out_of_range"
                refs.append({"plan_line": i, "section": section, "file": path,
                             "line": int(ln) if ln else None, "status": status})
        drift = [r for r in refs if r["status"] != "ok"]
        return {"refs_total": len(refs), "drift": drift, "drift_count": len(drift),
                "checked_plan": plan_path}

    def symbol_at(self, file: str, line: int):
        file = file.replace("\\", "/")
        meta = self.files.get(file)
        if not meta:
            for rel in self.files:
                if rel.endswith(file):
                    file, meta = rel, self.files[rel]
                    break
        if not meta:
            return None
        best = None
        for m in meta.get("methods", []):
            if m["line"] <= line and (best is None or m["line"] > best["line"]):
                best = m
        if best:
            return {"file": file, "symbol": best["name"], "owner": best.get("owner", ""),
                    "line": best["line"], "kind": "method"}
        return {"file": file, "symbol": (meta.get("types") or [""])[0], "kind": "file"}

    def file_list(self, kind: str | None = None) -> list:
        rows = []
        for rel, meta in sorted(self.files.items()):
            if kind and meta.get("ext") != kind:
                continue
            rows.append({"file": rel, "ext": meta.get("ext"), "layer": meta.get("layer"),
                         "methods": len(meta.get("methods", [])),
                         "inbound": len(self.rev.get(rel, ())),
                         "outbound": len(self.edges.get(rel, ()))})
        return rows

    def stats(self) -> dict:
        by_ext = Counter(m.get("ext") for m in self.files.values())
        by_layer = Counter(m.get("layer") for m in self.files.values())
        fan_in = sorted(((len(self.rev.get(r, ())), r) for r in self.files), reverse=True)
        fan_out = sorted(((len(self.edges.get(r, ())), r) for r in self.files), reverse=True)
        return {
            "repo": self.repo,
            "files": len(self.files),
            "by_ext": dict(by_ext),
            "by_layer": dict(by_layer),
            "symbols": len(self.symbols),
            "edges": sum(len(v) for v in self.edges.values()),
            "methods": sum(len(m.get("methods", [])) for m in self.files.values()),
            "scenes": len(self.scenes),
            "errors": len(self.errors),
            "top_fan_in": [{"file": r, "count": c} for c, r in fan_in[:10]],
            "top_fan_out": [{"file": r, "count": c} for c, r in fan_out[:10]],
        }


def build(repo: str, ignore=None, use_cache: bool = True) -> ProjectIndex:
    return ProjectIndex.build(repo, ignore=ignore, use_cache=use_cache)
