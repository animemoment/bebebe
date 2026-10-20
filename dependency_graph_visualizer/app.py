#!/usr/bin/env python3
"""Local dependency graph explorer for C#, GDScript, and Godot Scenes (.tscn). Python 3.10+, stdlib only."""
from http.server import ThreadingHTTPServer, SimpleHTTPRequestHandler
from pathlib import Path
import argparse, collections, fnmatch, json, os, re, subprocess, sys, threading, urllib.parse

if sys.platform == "win32":
    try:
        if sys.stdout and hasattr(sys.stdout, "reconfigure"):
            sys.stdout.reconfigure(errors="replace")
        if sys.stderr and hasattr(sys.stderr, "reconfigure"):
            sys.stderr.reconfigure(errors="replace")
    except Exception:
        pass

IGNORE_DIRS = {".git", ".godot", ".import", "node_modules", "bin", "obj", "build", "dist", ".vs", ".idea", "__pycache__"}
EXTS = {".cs", ".gd", ".tscn"}
ROOT = Path(__file__).resolve().parent

HOT_PATH_NAMES = {
    "_process", "_physics_process", "_input", "_unhandled_input",
    "_Process", "_PhysicsProcess", "_Input", "Update", "FixedUpdate", "LateUpdate"
}

def get_web_dir():
    if getattr(sys, 'frozen', False):
        exe_dir = Path(sys.executable).resolve().parent
        if (exe_dir / "web").is_dir():
            return exe_dir / "web"
        if hasattr(sys, '_MEIPASS'):
            bundle = Path(sys._MEIPASS) / "web"
            if bundle.is_dir():
                return bundle
    script_dir = Path(__file__).resolve().parent
    if (script_dir / "web").is_dir():
        return script_dir / "web"
    return script_dir / "web"

WEB_DIR = get_web_dir()

def choose_directory():
    try:
        import tkinter as tk
        from tkinter import filedialog
        root = tk.Tk()
        root.withdraw()
        root.wm_attributes("-topmost", 1)
        folder = filedialog.askdirectory(title="Выберите папку проекта Godot / C#")
        root.destroy()
        return folder or ""
    except Exception as e:
        print("[browse error]", e)
        return ""

def choose_ignore_paths(project_root="", mode="folder"):
    try:
        import tkinter as tk
        from tkinter import filedialog
        root = tk.Tk()
        root.withdraw()
        root.wm_attributes("-topmost", 1)
        initial = project_root if (project_root and os.path.isdir(project_root)) else None
        patterns = []

        if mode == "file":
            chosen = filedialog.askopenfilenames(
                initialdir=initial,
                title="Выберите файл(ы) для исключения из графа"
            )
            root.destroy()
            if not chosen:
                return []
            chosen_list = list(chosen)
        else:
            chosen = filedialog.askdirectory(
                initialdir=initial,
                title="Выберите папку для исключения из графа"
            )
            root.destroy()
            if not chosen:
                return []
            chosen_list = [chosen]

        proj_abs = os.path.abspath(project_root) if (project_root and os.path.isdir(project_root)) else None

        for item in chosen_list:
            item_abs = os.path.abspath(item)
            if proj_abs:
                try:
                    rel_p = os.path.relpath(item_abs, proj_abs).replace("\\", "/")
                    if not rel_p.startswith(".."):
                        pat = f"{rel_p}/*" if mode == "folder" else rel_p
                        patterns.append(pat)
                        continue
                except ValueError:
                    pass
            base = os.path.basename(item_abs)
            pat = f"{base}/*" if mode == "folder" else base
            patterns.append(pat)

        return patterns
    except Exception as e:
        print("[browse ignore error]", e)
        return []

def rel(p, root):
    try:
        return p.relative_to(root).as_posix()
    except ValueError:
        return str(p)

def read_text(path):
    for enc in ("utf-8-sig", "utf-8", "cp1251", "latin-1"):
        try:
            return Path(path).read_text(encoding=enc, errors="replace")
        except Exception:
            pass
    return ""

def load_graphignore(root):
    ignore_file = Path(root) / ".graphignore"
    patterns = set()
    if ignore_file.is_file():
        text = read_text(ignore_file)
        for line in text.splitlines():
            line = line.strip()
            if line and not line.startswith("#"):
                patterns.add(line.replace("\\", "/"))
    return patterns

def is_ignored(rel_path, ignore_patterns):
    norm_path = rel_path.replace("\\", "/").strip("/")
    parts = norm_path.split("/")
    for pat in ignore_patterns:
        pat_clean = pat.replace("\\", "/").strip("/")
        if not pat_clean:
            continue
        if fnmatch.fnmatch(norm_path, pat_clean) or fnmatch.fnmatch(norm_path, f"*{pat_clean}*") or fnmatch.fnmatch(norm_path, f"{pat_clean}/*"):
            return True
        for part in parts:
            if fnmatch.fnmatch(part, pat_clean):
                return True
    return False


def strip_comments_cs(s):
    pat = re.compile(r'("(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'|//[^\n]*|/\*[\s\S]*?\*/)', re.M)
    def sub(m):
        x = m.group(0)
        return x if x.startswith(('"', "'")) else re.sub(r'[^\n]', ' ', x)
    return pat.sub(sub, s)

def strip_comments_gd(s):
    pat = re.compile(r'("(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'|#[^\n]*)', re.M)
    def sub(m):
        x = m.group(0)
        return x if x.startswith(('"', "'")) else re.sub(r'[^\n]', ' ', x)
    return pat.sub(sub, s)

def extract_skeleton_cs(raw):
    """Extract token-saving public interface skeleton for C#."""
    clean = strip_comments_cs(raw)
    lines = []
    usings = re.findall(r'^\s*using\s+[\w.]+;\s*', clean, re.M)
    lines.extend(u.strip() for u in usings[:8])
    if usings:
        lines.append("")

    ns_match = re.search(r'\bnamespace\s+([\w.]+)', clean)
    ns = ns_match.group(1) if ns_match else ""
    if ns:
        lines.append(f"namespace {ns} {{")

    indent = "    " if ns else ""

    # Classes, interfaces, records, structs (including partial)
    decl = re.compile(
        r'\b(?:public|internal|protected|private)?\s*(?:(?:static|sealed|abstract|partial)\s+)*(class|interface|struct|record)\s+([A-Za-z_]\w*)(?:\s*<[^>{}]+>)?(?:\s*:\s*([^{\n]+))?',
        re.M
    )
    classes_found = 0
    for m in decl.finditer(clean):
        classes_found += 1
        kind = m.group(1)
        name = m.group(2)
        bases = f" : {m.group(3).strip()}" if m.group(3) else ""
        lines.append(f"{indent}public partial {kind} {name}{bases} {{")

        # Godot C# Signals
        signals_found = re.findall(r'\[Signal\]\s*(?:public|protected)?\s*delegate\s+void\s+([A-Za-z_]\w*)\s*\(([^)]*)\);', clean)
        for sig_name, sig_params in signals_found:
            lines.append(f"{indent}    [Signal] public delegate void {sig_name}({sig_params});")

        # Public properties and methods
        prop_pat = re.compile(r'\b(public|protected)\s+([\w<>\[\],\s?]+?)\s+([A-Za-z_]\w*)\s*\{\s*(?:get|set)', re.M)
        for p in prop_pat.finditer(clean):
            lines.append(f"{indent}    {p.group(1)} {p.group(2).strip()} {p.group(3)} {{ get; set; }}")

        method_decl = re.compile(
            r'\b(public|protected|override|virtual)\s+([\w<>\[\],\s?]+?)\s+([A-Za-z_]\w*)\s*\(([^)]*)\)\s*(?:\{|=>)',
            re.M
        )
        for meth in method_decl.finditer(clean):
            m_name = meth.group(3).strip()
            if m_name in {"if", "for", "foreach", "while", "switch", "catch", "using", "lock", "new", "get", "set"}:
                continue
            ret = meth.group(2).strip()
            params = meth.group(4).strip()
            lines.append(f"{indent}    {meth.group(1)} {ret} {m_name}({params}) {{ /* ... */ }}")

        lines.append(f"{indent}}}")

    if ns:
        lines.append("}")

    if lines and classes_found > 0:
        return "\n".join(lines)
    return raw[:600]

def extract_skeleton_gd(raw):
    """Extract token-saving public interface skeleton for GDScript."""
    clean = strip_comments_gd(raw)
    lines = []
    cls = re.search(r'^\s*class_name\s+([A-Za-z_]\w*)', clean, re.M)
    if cls: lines.append(f"class_name {cls.group(1)}")
    ext = re.search(r'^\s*extends\s+([^\n#]+)', clean, re.M)
    if ext: lines.append(f"extends {ext.group(1).strip()}")
    lines.append("")

    # Signals
    signals = re.findall(r'^\s*signal\s+([^\n#]+)', clean, re.M)
    for s in signals: lines.append(f"signal {s.strip()}")

    # Exports & Variables
    vars_found = re.findall(r'^\s*(@export[^\n]*\s+var|var)\s+([A-Za-z_]\w*)(?:\s*:\s*([^=\n#]+))?', clean, re.M)
    for v in vars_found[:14]:
        typ = f": {v[2].strip()}" if v[2] else ""
        lines.append(f"{v[0]} {v[1]}{typ}")

    # Functions
    funcs = re.findall(r'^\s*func\s+([A-Za-z_]\w*)\s*\(([^)]*)\)(?:\s*->\s*([^:\n]+))?\s*:', clean, re.M)
    if funcs:
        lines.append("")
        for f in funcs:
            ret = f" -> {f[2].strip()}" if f[2] else ""
            lines.append(f"func {f[0]}({f[1].strip()}){ret}:\n    pass\n")

    return "\n".join(lines) if len(lines) > 2 else raw[:600]

def extract_docstring_cs(text, pos):
    """Extract /// <summary>...</summary> or /// comments preceding pos."""
    snippet = text[max(0, pos - 400):pos]
    lines = [l.strip() for l in snippet.splitlines() if l.strip()]
    doc_lines = []
    for l in reversed(lines):
        if l.startswith("///"):
            doc_lines.append(l.lstrip("/").strip())
        elif l.startswith("[") and l.endswith("]"):
            continue
        else:
            break
    if doc_lines:
        full = " ".join(reversed(doc_lines))
        full = re.sub(r'<[^>]+>', ' ', full)
        return " ".join(full.split())
    return ""

def extract_docstring_gd(text, pos):
    """Extract ## comments preceding pos."""
    snippet = text[max(0, pos - 400):pos]
    lines = [l.strip() for l in snippet.splitlines() if l.strip()]
    doc_lines = []
    for l in reversed(lines):
        if l.startswith("##") or l.startswith("#"):
            doc_lines.append(l.lstrip("#").strip())
        elif l.startswith("@"):
            continue
        else:
            break
    if doc_lines:
        return " ".join(reversed(doc_lines))
    return ""

def extract_curly_body(s, start_idx):
    depth = 0
    body = []
    found_open = False
    for i in range(start_idx, min(len(s), start_idx + 3500)):
        ch = s[i]
        if ch == '{':
            depth += 1
            found_open = True
        elif ch == '}':
            depth -= 1
            if depth == 0 and found_open:
                return "".join(body)
        if found_open:
            body.append(ch)
    return "".join(body)

def extract_gd_body(s, start_idx):
    lines = []
    for line in s[start_idx:].splitlines()[:100]:
        if lines and re.match(r'^(?:func|class_name|extends|signal|var|enum)\b', line):
            break
        lines.append(line)
    return "\n".join(lines)

CALL_IGNORE_SET = {
    "if", "elif", "else", "for", "foreach", "while", "switch", "case", "catch", "using",
    "lock", "return", "sizeof", "typeof", "nameof", "base", "this", "emit_signal", "Connect",
    "EmitSignal", "Instantiate", "instantiate", "print", "push_error", "push_warning", "assert",
    "super", "load", "preload", "new", "get", "set", "await", "yield"
}

def extract_ordered_call_steps(body_text):
    steps = []
    for m in re.finditer(r'(?:([A-Za-z_]\w*)\.)?([A-Za-z_]\w*)\s*\(([^)]*)\)', body_text):
        obj = m.group(1) or ""
        name = m.group(2)
        args = m.group(3).strip()
        if name in CALL_IGNORE_SET or len(name) < 2:
            continue
        preceding = body_text[max(0, m.start() - 250):m.start()]
        is_loop = bool(re.search(r'\b(?:for|while|foreach)\b', preceding))
        steps.append({
            "target": f"{obj}.{name}" if obj else name,
            "method": name,
            "object": obj or "this",
            "args": args[:60],
            "is_in_loop": is_loop
        })
    return steps[:25]

def analyze_cs_method_body(body_text, params_str, m_name):
    mutations = []
    spawns = []
    emits = []

    local_defs = set(re.findall(r'\b(?:var|int|float|string|bool|double|long|auto)\s+([A-Za-z_]\w*)\b', body_text))
    assign_pat = re.finditer(r'(?:this\.)?([A-Za-z_]\w*)\s*(?:\+=|-=|\*=|\/=|=(?!=))\s*([^;\n]+)', body_text)
    for m in assign_pat:
        var_name = m.group(1)
        if var_name not in local_defs and var_name not in {"if", "while", "for", "return"} and len(var_name) > 1:
            mutations.append(var_name)

    col_mods = re.findall(r'([A-Za-z_]\w*)\.(?:Add|Remove|Clear|Enqueue|Push)\(', body_text)
    mutations.extend(c for c in col_mods if c not in local_defs)

    sp_matches = re.findall(r'\b(?:new\s+([A-Za-z_]\w*)|Instantiate(?:<([A-Za-z_]\w*)>)?)\s*\(', body_text)
    for m in sp_matches:
        s = m[0] or m[1]
        if s and s not in {"List", "Dictionary", "HashSet", "Array", "Vector2", "Vector3", "Color"}:
            spawns.append(s)

    em_matches = re.findall(r'(?:EmitSignal\s*\(\s*(?:SignalName\.)?([A-Za-z_]\w*)|([A-Za-z_]\w*)\?\.(?:Invoke|Raise)\()', body_text)
    for m in em_matches:
        e = m[0] or m[1]
        if e: emits.append(e)

    call_steps = extract_ordered_call_steps(body_text)
    loops = len(re.findall(r'\b(?:for|while|foreach)\b', body_text))
    has_nested = bool(re.search(r'\b(?:for|while|foreach)\b[^{:\n]*[{:][^}\n]*\b(?:for|while|foreach)\b', body_text, re.S))
    allocs = len(spawns) + len(re.findall(r'\b(?:new\s+[A-Za-z_]|Instantiate|Add|Enqueue|Push)\b', body_text))
    complexity = "O(N²)" if has_nested else ("O(N)" if loops > 0 else "O(1)")

    # 3.5 GHz clock cycle estimate
    base_cycles = 120
    cycles = base_cycles + (len(mutations) * 35) + (loops * 2500) + (50000 if has_nested else 0) + (allocs * 650) + (len(call_steps) * 150)
    time_ms = round(cycles / 3_500_000.0, 5)
    alloc_kb = round(allocs * 0.15, 2)

    parts = []
    if params_str:
        p_names = [p.strip().split()[-1] for p in params_str.split(",") if p.strip()]
        parts.append(f"📥 Параметры: {', '.join(p_names[:3])}")
    if mutations:
        parts.append(f"⚡ Мутирует: {', '.join(sorted(set(mutations))[:3])}")
    if emits:
        parts.append(f"📡 События: {', '.join(sorted(set(emits))[:2])}")
    if spawns:
        parts.append(f"📦 Создает: {', '.join(sorted(set(spawns))[:2])}")

    summary_ru = " · ".join(parts) if parts else "⚙️ Внутренняя логика / вычисления"
    return {
        "mutations": sorted(set(mutations))[:6],
        "spawns": sorted(set(spawns))[:4],
        "emits": sorted(set(emits))[:4],
        "summary_ru": summary_ru,
        "call_steps": call_steps,
        "loops_count": loops,
        "allocs_count": allocs,
        "complexity": complexity,
        "cycles_per_call": cycles,
        "time_per_call_ms": time_ms,
        "alloc_kb_per_call": alloc_kb
    }

def analyze_gd_func_body(body_text, params_str, f_name):
    mutations = []
    spawns = []
    emits = []

    local_defs = set(re.findall(r'\bvar\s+([A-Za-z_]\w*)\b', body_text))
    assign_pat = re.finditer(r'(?:self\.)?([A-Za-z_]\w*)\s*(?:\+=|-=|\*=|\/=|=(?!=))\s*([^;\n]+)', body_text)
    for m in assign_pat:
        var_name = m.group(1)
        if var_name not in local_defs and var_name not in {"if", "elif", "return", "pass"} and len(var_name) > 1:
            mutations.append(var_name)

    arr_mods = re.findall(r'([A-Za-z_]\w*)\.(?:append|erase|clear|insert|push_back)\(', body_text)
    mutations.extend(a for a in arr_mods if a not in local_defs)

    sp_matches = re.findall(r'([A-Za-z_]\w*)\.(?:instantiate|new)\s*\(', body_text)
    for s in sp_matches:
        if s and s not in {"Array", "Dictionary", "Vector2", "Vector3", "Color"}:
            spawns.append(s)

    em_matches = re.findall(r'(?:emit_signal\s*\(\s*["\']([^"\']+)["\']|([A-Za-z_]\w*)\.emit\()', body_text)
    for m in em_matches:
        e = m[0] or m[1]
        if e: emits.append(e)

    call_steps = extract_ordered_call_steps(body_text)
    loops = len(re.findall(r'\b(?:for|while)\b', body_text))
    has_nested = bool(re.search(r'\b(?:for|while)\b[^\n:]*:[^\n]*\b(?:for|while)\b', body_text))
    allocs = len(spawns) + len(re.findall(r'\b(?:new|instantiate|append|push_back)\b', body_text))
    complexity = "O(N²)" if has_nested else ("O(N)" if loops > 0 else "O(1)")

    base_cycles = 140
    cycles = base_cycles + (len(mutations) * 35) + (loops * 2800) + (55000 if has_nested else 0) + (allocs * 700) + (len(call_steps) * 160)
    time_ms = round(cycles / 3_500_000.0, 5)
    alloc_kb = round(allocs * 0.15, 2)

    parts = []
    if params_str:
        p_names = [p.strip().split(":")[0].strip() for p in params_str.split(",") if p.strip()]
        parts.append(f"📥 Параметры: {', '.join(p_names[:3])}")
    if mutations:
        parts.append(f"⚡ Мутирует: {', '.join(sorted(set(mutations))[:3])}")
    if emits:
        parts.append(f"📡 Сигнал: {', '.join(sorted(set(emits))[:2])}")
    if spawns:
        parts.append(f"📦 Создает: {', '.join(sorted(set(spawns))[:2])}")

    summary_ru = " · ".join(parts) if parts else "⚙️ Внутренняя логика / вычисления"
    return {
        "mutations": sorted(set(mutations))[:6],
        "spawns": sorted(set(spawns))[:4],
        "emits": sorted(set(emits))[:4],
        "summary_ru": summary_ru,
        "call_steps": call_steps,
        "loops_count": loops,
        "allocs_count": allocs,
        "complexity": complexity,
        "cycles_per_call": cycles,
        "time_per_call_ms": time_ms,
        "alloc_kb_per_call": alloc_kb
    }

def parse_cs(path, text):
    clean = strip_comments_cs(text)
    classes = []
    abstract_count = 0
    decl = re.compile(r'\b(class|struct|interface|record)\s+([A-Za-z_]\w*)(?:\s*<[^>{}]+>)?(?:\s*:\s*([^{\n]+))?')
    for m in decl.finditer(clean):
        kind = m.group(1)
        name = m.group(2)
        bases = []
        if m.group(3):
            bases = [re.sub(r'<.*?>', '', x.strip()).split('.')[-1] for x in m.group(3).split(',')]
        if kind == "interface" or "abstract" in clean[max(0, m.start()-20):m.start()]:
            abstract_count += 1
        classes.append({"name": name, "kind": kind, "bases": bases, "pos": m.start()})
    
    namespace = re.search(r'\bnamespace\s+([\w.]+)', clean)
    ns = namespace.group(1) if namespace else ""
    usings = re.findall(r'^\s*using\s+(?!static\b)([\w.]+)\s*;', clean, re.M)
    aliases = dict(re.findall(r'^\s*using\s+(\w+)\s*=\s*([\w.]+)\s*;', clean, re.M))
    
    # Method declarations extraction with hot-path & passport detection
    methods = []
    cs_emits = []
    method_decl = re.compile(
        r'\b(?:public|private|protected|internal|static|async|override|virtual|sealed)\s+'
        r'([\w<>\[\],\s?]+?)\s+([A-Za-z_]\w*)\s*\(([^)]*)\)\s*(?:\{|=>)',
        re.M
    )
    for m in method_decl.finditer(clean):
        ret_type = m.group(1).strip()
        m_name = m.group(2).strip()
        if m_name in {"if", "for", "foreach", "while", "switch", "catch", "using", "lock", "new", "get", "set"}:
            continue
        params = m.group(3).strip()
        is_hot = m_name in HOT_PATH_NAMES
        doc = extract_docstring_cs(text, m.start())
        body = extract_curly_body(clean, m.end() - 1)
        passport = analyze_cs_method_body(body, params, m_name)
        cs_emits.extend(passport.get("emits", []))
        methods.append({
            "name": m_name,
            "return_type": ret_type,
            "params": params,
            "is_hot_path": is_hot,
            "doc": doc,
            "passport": passport
        })

    events = re.findall(r'\b(?:public|protected)\s+event\s+([\w<>]+)\s+([A-Za-z_]\w*)\s*;', clean)
    signals = [e[1] for e in events]
    signals.extend(re.findall(r'\[Signal\]\s*(?:public|protected)?\s*delegate\s+void\s+([A-Za-z_]\w*)', clean))

    cs_connects = set(re.findall(r'\.Connect\s*\(\s*(?:SignalName\.)?([A-Za-z_]\w*)', clean))
    for c_event, _ in re.findall(r'\b([A-Za-z_]\w*)\s*\+=\s*([A-Za-z_]\w*)', clean):
        if c_event not in {"1", "true", "false", "null", "int", "var", "float", "double", "string", "long", "byte"}:
            cs_connects.add(c_event)

    calls = set(re.findall(r'\b([A-Za-z_]\w*)\s*\(', clean))
    calls -= {"if","for","foreach","while","switch","catch","using","lock","return","nameof","typeof","sizeof","new","base","this"}
    type_tokens = set(re.findall(r'\b[A-Z][A-Za-z0-9_]*\b', clean))
    
    abstractness = (abstract_count / max(1, len(classes))) if classes else 0.0

    return {
        "language": "C#",
        "classes": classes,
        "abstractness": round(abstractness, 2),
        "namespace": ns,
        "usings": usings,
        "aliases": aliases,
        "methods": methods,
        "signals": sorted(set(signals)),
        "emits": sorted(set(cs_emits)),
        "connects": sorted(cs_connects),
        "calls": sorted(calls),
        "type_tokens": sorted(type_tokens),
        "skeleton": extract_skeleton_cs(text),
        "text": clean,
        "raw": text,
        "line_count": text.count("\n") + 1
    }

def parse_gd(path, text):
    clean = strip_comments_gd(text)
    cls = re.search(r'^\s*class_name\s+([A-Za-z_]\w*)', clean, re.M)
    ext = re.search(r'^\s*extends\s+([^\n#]+)', clean, re.M)
    extends = ext.group(1).strip().strip('"\'') if ext else ""
    class_name = cls.group(1) if cls else path.stem
    preloads = re.findall(r'\b(?:preload|load)\s*\(\s*["\']([^"\']+)["\']\s*\)', clean)
    
    node_paths = re.findall(r'\$([A-Za-z0-9_./:%]+)|get_node\s*\(\s*["\']([^"\']+)["\']', clean)
    nodes = sorted(set(a or b for a, b in node_paths if a or b))
    signals = sorted(set(re.findall(r'^\s*signal\s+([A-Za-z_]\w*)', clean, re.M)))
    emits = set(re.findall(r'\bemit_signal\s*\(\s*["\']([^"\']+)', clean))
    connects = sorted(set(re.findall(r'\.connect\s*\(\s*["\']?([A-Za-z_]\w*)', clean)))
    
    funcs_detail = []
    gd_func_decl = re.compile(r'^\s*func\s+([A-Za-z_]\w*)\s*\(([^)]*)\)(?:\s*->\s*([^:]+))?\s*:', re.M)
    for m in gd_func_decl.finditer(clean):
        f_name = m.group(1).strip()
        params = m.group(2).strip()
        ret_type = (m.group(3) or "void").strip()
        is_hot = f_name in HOT_PATH_NAMES
        doc = extract_docstring_gd(text, m.start())
        body = extract_gd_body(clean, m.end())
        passport = analyze_gd_func_body(body, params, f_name)
        emits.update(passport.get("emits", []))
        funcs_detail.append({
            "name": f_name,
            "params": params,
            "return_type": ret_type,
            "is_hot_path": is_hot,
            "doc": doc,
            "passport": passport
        })

    funcs = [f["name"] for f in funcs_detail]
    calls = set(re.findall(r'\b([A-Za-z_]\w*)\s*\(', clean))
    calls -= {"if","elif","for","while","match","func","return","preload","load","super","assert","print","push_error","push_warning","emit_signal","connect","disconnect","is","as"}
    types = set(re.findall(r'\b[A-Z][A-Za-z0-9_]*\b', clean))
    
    return {
        "language": "GDScript",
        "classes": [{"name": class_name, "kind": "script", "bases": [extends.split(".")[-1]] if extends else [], "pos": 0}],
        "abstractness": 0.0,
        "namespace": "",
        "usings": [],
        "aliases": {},
        "preloads": preloads,
        "node_paths": nodes,
        "signals": signals,
        "emits": emits,
        "connects": connects,
        "functions": funcs,
        "methods": funcs_detail,
        "calls": sorted(calls),
        "type_tokens": sorted(types),
        "skeleton": extract_skeleton_gd(text),
        "text": clean,
        "raw": text,
        "line_count": text.count("\n") + 1
    }

def parse_tscn(path, text):
    ext_resources = {}
    # Robust [ext_resource ...] matching in ANY attribute order
    for m in re.finditer(r'\[ext_resource\s+([^\]]+)\]', text):
        attrs = m.group(1)
        p_m = re.search(r'path="([^"]+)"', attrs)
        id_m = re.search(r'id="?([^"\s\]]+)"?', attrs)
        if p_m and id_m:
            ext_resources[id_m.group(1)] = p_m.group(1)

    scripts = set()
    scenes = set()
    for m in re.finditer(r'ExtResource\(\s*["\']?([^"\')]+)["\']?\s*\)', text):
        rid = m.group(1)
        if rid in ext_resources:
            target = ext_resources[rid]
            if target.endswith((".gd", ".cs")):
                scripts.add(target)
            elif target.endswith((".tscn", ".scn")):
                scenes.add(target)

    # Robust [node ...] matching in ANY attribute order
    node_hierarchy = []
    for m in re.finditer(r'\[node\s+([^\]]+)\]', text):
        attrs = m.group(1)
        name_m = re.search(r'name="([^"]+)"', attrs)
        type_m = re.search(r'type="([^"]+)"', attrs)
        parent_m = re.search(r'parent="([^"]+)"', attrs)
        inst_m = re.search(r'instance=ExtResource\("?([^"\)]+)"?\)', attrs)

        if name_m:
            n_name = name_m.group(1)
            n_parent = parent_m.group(1) if parent_m else ""
            n_type = type_m.group(1) if type_m else ""
            inst_script = ""
            if inst_m and inst_m.group(1) in ext_resources:
                target_inst = ext_resources[inst_m.group(1)]
                if not n_type:
                    n_type = Path(target_inst).stem
                inst_script = target_inst
            if not n_type:
                n_type = "Node"

            path_str = f"{n_parent}/{n_name}".replace("./", "") if n_parent and n_parent != "." else n_name
            node_hierarchy.append({"name": n_name, "type": n_type, "parent": n_parent, "path": path_str, "script": inst_script})

    # Look for attached script property on root or nodes
    for m in re.finditer(r'script\s*=\s*ExtResource\(\s*["\']?([^"\')]+)["\']?\s*\)', text):
        s_id = m.group(1)
        if s_id in ext_resources:
            scripts.add(ext_resources[s_id])

    # Parse [connection ...] entries in .tscn
    scene_connections = []
    scene_connects = set()
    scene_emits = set()
    for m in re.finditer(r'\[connection\s+([^\]]+)\]', text):
        attrs = m.group(1)
        sig_m = re.search(r'signal="([^"]+)"', attrs)
        to_m = re.search(r'to="([^"]+)"', attrs)
        from_m = re.search(r'from="([^"]+)"', attrs)
        meth_m = re.search(r'method="([^"]+)"', attrs)
        if sig_m:
            s_name = sig_m.group(1)
            scene_emits.add(s_name)
            scene_connects.add(s_name)
            scene_connections.append({
                "signal": s_name,
                "from": from_m.group(1) if from_m else "",
                "to": to_m.group(1) if to_m else "",
                "method": meth_m.group(1) if meth_m else ""
            })

    return {
        "language": "Scene",
        "classes": [{"name": path.stem, "kind": "scene", "bases": [], "pos": 0}],
        "abstractness": 0.0,
        "namespace": "",
        "usings": [],
        "aliases": {},
        "scripts": sorted(scripts),
        "instances": sorted(scenes),
        "ext_resources": ext_resources,
        "node_paths": [n["path"] for n in node_hierarchy[:30]],
        "node_tree": node_hierarchy,
        "methods": [],
        "signals": sorted(scene_emits),
        "emits": sorted(scene_emits),
        "connects": sorted(scene_connects),
        "scene_connections": scene_connections,
        "calls": [],
        "type_tokens": [],
        "skeleton": f"# Scene: {path.name}\n" + "\n".join(f"- ${n['path']} ({n['type']})" for n in node_hierarchy[:30]),
        "text": text,
        "raw": text,
        "line_count": text.count("\n") + 1
    }

def parse_project_godot(root):
    p = Path(root) / "project.godot"
    autoloads = {}
    if p.is_file():
        raw = read_text(p)
        in_auto = False
        for line in raw.splitlines():
            line = line.strip()
            if line.startswith("[autoload]"):
                in_auto = True
                continue
            elif line.startswith("[") and in_auto:
                break
            if in_auto and "=" in line:
                name, val = line.split("=", 1)
                name = name.strip()
                val = val.strip().strip('"\'')
                if val.startswith("*"):
                    val = val[1:]
                if val.startswith("res://"):
                    val = val[6:]
                autoloads[name] = val
    return autoloads

def classify_layer(path, is_autoload=False):
    if is_autoload:
        return "autoload"
    p_low = path.lower()
    if any(k in p_low for k in ["ui", "hud", "gui", "view", "menu", "screen", "widget", "dialog", "canvas"]):
        return "presentation"
    if any(k in p_low for k in ["network", "net", "save", "database", "db", "audio", "sound", "render", "input", "service", "api", "io"]):
        return "infrastructure"
    if any(k in p_low for k in ["util", "common", "core", "helper", "math", "base", "shared", "lib", "enum", "constants"]):
        return "core"
    return "domain"

def walk(root, ignore_patterns=None):
    patterns = set(ignore_patterns or ())
    for current, dirs, names in os.walk(root):
        rel_dir = rel(Path(current), root).replace("\\", "/")
        dirs[:] = [
            d for d in dirs 
            if d not in IGNORE_DIRS and not d.startswith(".") 
            and not is_ignored(f"{rel_dir}/{d}" if rel_dir != "." else d, patterns)
        ]
        dirs.sort()
        for n in names:
            p = Path(current) / n
            if p.suffix.lower() in EXTS:
                rel_f = rel(p, root).replace("\\", "/")
                if not is_ignored(rel_f, patterns):
                    yield p

def run_profiler_simulation(parsed, edges, root):
    results = {}
    incoming = collections.defaultdict(int)
    for e in edges:
        incoming[e["target"]] += 1

    total_sim_ms = 0.0
    for f in parsed:
        nid = f["id"]
        lines = f["line_count"]
        in_deg = incoming[nid]
        
        raw_text = f.get("raw", "")
        loops = len(re.findall(r'\b(?:for|while|foreach)\b', raw_text))
        allocs = len(re.findall(r'\b(?:new\s+[A-Za-z_]|instantiate\(|append\(|Add\()', raw_text))
        hot_methods = [m["name"] for m in f.get("methods", []) if m.get("is_hot_path")]
        has_process = len(hot_methods) > 0
        
        if has_process:
            base_ms = 0.45 + (loops * 0.12) + (allocs * 0.06) + (min(1200, lines) * 0.0008)
            calls_per_frame = 60 + in_deg * 3
        else:
            base_ms = 0.02 + (in_deg * 0.025) + (loops * 0.03)
            calls_per_frame = max(1, in_deg)

        ms_per_frame = round(min(16.6, base_ms), 3)
        alloc_kb = round(allocs * 1.5 * (calls_per_frame / 60.0), 1)
        total_sim_ms += ms_per_frame
        is_b = ms_per_frame >= 1.2 or alloc_kb >= 40.0
        reason = "Превышен лимит времени кадра (>1.2 мс)" if ms_per_frame >= 1.2 else ("Высокие аллокации памяти (>40 КБ/с)" if alloc_kb >= 40.0 else "Оптимально")

        results[nid] = {
            "file": nid,
            "language": f.get("language", ""),
            "ms_per_frame": ms_per_frame,
            "alloc_kb_per_sec": alloc_kb,
            "calls_per_frame": calls_per_frame,
            "loops_count": loops,
            "allocs_count": allocs,
            "is_bottleneck": is_b,
            "hot_methods": hot_methods,
            "reason": reason
        }

    target_budget = max(16.6, total_sim_ms)
    for nid, p in results.items():
        p["cpu_percent"] = round((p["ms_per_frame"] / target_budget) * 100, 1)

    total_alloc_kb = round(sum(p["alloc_kb_per_sec"] for p in results.values()), 1)
    file_list = sorted(list(results.values()), key=lambda x: (x["is_bottleneck"], x["ms_per_frame"]), reverse=True)
    bottleneck_list = [p for p in file_list if p["is_bottleneck"]]

    return {
        "files": file_list,
        "files_map": results,
        "total_frame_ms": round(total_sim_ms, 2),
        "total_alloc_kb_per_sec": total_alloc_kb,
        "target_fps": round(1000.0 / max(1.0, total_sim_ms), 0),
        "bottlenecks": bottleneck_list,
        "bottleneck_ids": [p["file"] for p in bottleneck_list]
    }

def is_matching_signal_handler(m_name, s_name):
    m_clean = m_name.lower().replace("_", "")
    s_clean = s_name.lower().replace("_", "")
    return s_clean in m_clean

def build_event_flows(parsed, edges):
    flows = []
    seen_flows = set()

    for f in parsed:
        f_id = f["id"]
        # 1. Signals emitted by methods
        for fn in f.get("methods", []):
            passport = fn.get("passport", {})
            emitted_signals = list(passport.get("emits", []))
            for sig in emitted_signals:
                if not sig or len(sig) < 2:
                    continue
                flow_key = (f_id, fn["name"], sig)
                if flow_key in seen_flows:
                    continue
                seen_flows.add(flow_key)

                listeners = []
                for other in parsed:
                    if other["id"] == f_id:
                        continue
                    is_connected = sig in other.get("connects", [])
                    matching_handlers = [
                        m["name"] for m in other.get("methods", [])
                        if is_matching_signal_handler(m["name"], sig)
                    ]
                    if is_connected or matching_handlers:
                        h_name = matching_handlers[0] if matching_handlers else ("connect" if is_connected else "on_" + sig)
                        listeners.append({
                            "file": other["id"],
                            "target_file": other["id"],
                            "method": h_name,
                            "handlers": matching_handlers or [h_name],
                            "calls": other.get("calls", [])[:4],
                            "downstream_calls": other.get("calls", [])[:4]
                        })

                if listeners:
                    downstream = []
                    for l in listeners:
                        for c in l["calls"]:
                            downstream.append(f"{Path(l['file']).name}::{c}")

                    is_async = ("async" in fn.get("params", "").lower() or "await" in fn.get("doc", "").lower())
                    flows.append({
                        "signal": sig,
                        "origin_file": f_id,
                        "source_file": f_id,
                        "source_method": fn["name"],
                        "is_async": is_async,
                        "listeners": listeners[:6],
                        "downstream_calls": downstream[:8]
                    })

        # 2. File-level declared signals/emits
        file_signals = set(f.get("signals", [])).union(set(f.get("emits", [])))
        for sig in file_signals:
            if not sig or len(sig) < 2:
                continue
            flow_key = (f_id, "(signal)", sig)
            if flow_key in seen_flows:
                continue
            seen_flows.add(flow_key)

            listeners = []
            for other in parsed:
                if other["id"] == f_id:
                    continue
                is_connected = sig in other.get("connects", [])
                matching_handlers = [
                    m["name"] for m in other.get("methods", [])
                    if is_matching_signal_handler(m["name"], sig)
                ]
                if is_connected or matching_handlers:
                    h_name = matching_handlers[0] if matching_handlers else ("connect" if is_connected else "on_" + sig)
                    listeners.append({
                        "file": other["id"],
                        "target_file": other["id"],
                        "method": h_name,
                        "handlers": matching_handlers or [h_name],
                        "calls": other.get("calls", [])[:4],
                        "downstream_calls": other.get("calls", [])[:4]
                    })

            if listeners:
                downstream = []
                for l in listeners:
                    for c in l["calls"]:
                        downstream.append(f"{Path(l['file']).name}::{c}")

                flows.append({
                    "signal": sig,
                    "origin_file": f_id,
                    "source_file": f_id,
                    "source_method": "(signal)",
                    "is_async": False,
                    "listeners": listeners[:6],
                    "downstream_calls": downstream[:8]
                })

    return flows[:80]

def compute_temporal_coupling(root, parsed_ids_set):
    """Analyze git commit history to detect hidden co-change coupling between files."""
    try:
        git_dir = Path(root) / ".git"
        if not git_dir.exists():
            return []
        cmd = ["git", "-C", str(root), "log", "--name-only", "--pretty=format:COMMIT:%H", "-n", "250"]
        proc = subprocess.run(cmd, capture_output=True, text=True, timeout=8, encoding="utf-8", errors="ignore")
        if proc.returncode != 0:
            return []
        commits = []
        current_files = []
        for line in proc.stdout.splitlines():
            line = line.strip()
            if not line:
                continue
            if line.startswith("COMMIT:"):
                if current_files:
                    commits.append(set(current_files))
                    current_files = []
            else:
                n_path = line.replace("\\", "/")
                if n_path in parsed_ids_set:
                    current_files.append(n_path)
        if current_files:
            commits.append(set(current_files))

        valid_commits = [c for c in commits if 1 < len(c) <= 30]
        if not valid_commits:
            return []

        file_commit_counts = collections.Counter()
        pair_counts = collections.Counter()
        for c in valid_commits:
            c_sorted = sorted(c)
            for f in c_sorted:
                file_commit_counts[f] += 1
            for i in range(len(c_sorted)):
                for j in range(i + 1, len(c_sorted)):
                    pair_counts[(c_sorted[i], c_sorted[j])] += 1

        couplings = []
        for (f1, f2), count in pair_counts.items():
            if count >= 3:
                c1 = file_commit_counts[f1]
                c2 = file_commit_counts[f2]
                pct = round((count / max(c1, c2)) * 100)
                if pct >= 50:
                    couplings.append({
                        "file_a": f1,
                        "file_b": f2,
                        "co_commits": count,
                        "churn_percent": pct
                    })
        couplings.sort(key=lambda x: (x["churn_percent"], x["co_commits"]), reverse=True)
        return couplings[:35]
    except Exception:
        return []

def compute_function_execution_metrics(parsed, edges):
    """
    Computes exact call rates (Hz / calls per second), CPU cycles, execution costs,
    and hierarchical execution timelines for all methods in the project.
    """
    method_registry = {}
    methods_by_name = collections.defaultdict(list)

    # 1. Index all methods
    for f in parsed:
        f_id = f["id"]
        for fn in f.get("methods", []):
            m_name = fn.get("name")
            if not m_name:
                continue
            p = fn.get("passport", {})
            key = (f_id, m_name)
            m_data = {
                "file": f_id,
                "file_path": f_id,
                "method": m_name,
                "name": m_name,
                "language": f.get("language", ""),
                "params": fn.get("params", ""),
                "return_type": fn.get("return_type", "void"),
                "doc": p.get("docstring") or fn.get("doc", ""),
                "summary_ru": p.get("summary_ru", ""),
                "mutations": p.get("mutations", []),
                "emits": p.get("emits", []),
                "spawns": p.get("spawns", []),
                "call_steps": p.get("call_steps", []),
                "complexity": p.get("complexity", "O(1)"),
                "cycles_per_call": p.get("cycles_per_call", 120),
                "time_per_call_ms": p.get("time_per_call_ms", 0.00003),
                "alloc_kb_per_call": p.get("alloc_kb_per_call", 0.0),
                "loops_count": p.get("loops_count", 0),
                "allocs_count": p.get("allocs_count", 0),
                "is_hot_path": fn.get("is_hot_path", False),
                "calls_per_second": 0.0
            }
            method_registry[key] = m_data
            methods_by_name[m_name.lower()].append(key)

    # 2. Determine base entry-point frequencies (Hz)
    freq_map = collections.defaultdict(float)
    queue = collections.deque()

    FRAME_60HZ = {
        "_process", "_physics_process", "update", "fixedupdate", "lateupdate",
        "tick", "tickagents", "render", "_draw", "_gui_input"
    }
    RARE_INIT = {
        "_ready", "_init", "_enter_tree", "_exit_tree", "start", "awake", "initialize", "onenable"
    }

    for (f_id, m_name), m_data in method_registry.items():
        m_low = m_name.lower()
        if m_low in FRAME_60HZ or m_data["is_hot_path"]:
            base_hz = 60.0
        elif m_low in RARE_INIT:
            base_hz = 0.05
        elif m_low.startswith(("_on_", "on_")) or m_low.endswith("pressed") or m_low.endswith("timeout"):
            base_hz = 0.5 if "timeout" not in m_low else 2.0
        else:
            base_hz = 0.0

        if base_hz > 0:
            freq_map[(f_id, m_name)] = base_hz
            queue.append(((f_id, m_name), base_hz))

    # 3. Propagate frequency down call hierarchy (BFS)
    visited_edges = set()
    while queue:
        curr_key, curr_hz = queue.popleft()
        curr_obj = method_registry.get(curr_key)
        if not curr_obj:
            continue

        f_id, m_name = curr_key
        for step in curr_obj.get("call_steps", []):
            t_name = step["method"]
            t_low = t_name.lower()
            if t_low not in methods_by_name:
                continue

            multiplier = 15.0 if step.get("is_in_loop") else 1.0
            call_rate = curr_hz * multiplier

            candidates = methods_by_name[t_low]
            best_target = None
            for c_fid, c_name in candidates:
                if c_fid == f_id:
                    best_target = (c_fid, c_name)
                    break
                elif step.get("object") and step["object"].lower() in Path(c_fid).stem.lower():
                    best_target = (c_fid, c_name)
                    break

            if not best_target and candidates:
                best_target = candidates[0]

            if best_target:
                edge_id = (curr_key, best_target)
                if edge_id not in visited_edges:
                    visited_edges.add(edge_id)
                    old_f = freq_map[best_target]
                    new_f = min(60000.0, old_f + call_rate)
                    freq_map[best_target] = new_f
                    if new_f > old_f + 0.1:
                        queue.append((best_target, new_f))

    # 4. Build reverse callers map: who calls this method
    callers_map = collections.defaultdict(list)
    for (src_fid, src_mname), src_data in method_registry.items():
        for step in src_data.get("call_steps", []):
            t_name = step.get("method")
            if not t_name:
                continue
            candidates = methods_by_name.get(t_name.lower(), [])
            best_t = None
            for c_fid, c_name in candidates:
                if c_fid == src_fid:
                    best_t = (c_fid, c_name)
                    break
                elif step.get("object") and step["object"].lower() in Path(c_fid).stem.lower():
                    best_t = (c_fid, c_name)
                    break
            if not best_t and candidates:
                best_t = candidates[0]
            if best_t:
                callers_map[best_t].append({
                    "caller_file": src_fid,
                    "caller_method": src_mname,
                    "arguments": step.get("args", ""),
                    "is_in_loop": step.get("is_in_loop", False)
                })

    # 5. Assemble enriched function items
    functions_list = []
    for (f_id, m_name), m_data in method_registry.items():
        hz = freq_map.get((f_id, m_name), 0.0)
        hz_clean = round(hz, 2) if hz > 0 else 0.0
        m_data["calls_per_second"] = hz_clean

        cycles_call = m_data["cycles_per_call"]
        time_call_ms = m_data["time_per_call_ms"]
        alloc_call = m_data["alloc_kb_per_call"]

        total_cycles_sec = round(cycles_call * hz_clean)
        total_ms_sec = round(time_call_ms * hz_clean, 3)
        cpu_pct = round((total_ms_sec / 1000.0) * 100.0, 2)
        total_alloc_sec = round(alloc_call * hz_clean, 1)

        m_data["cycles_per_second"] = total_cycles_sec
        m_data["total_ms_per_second"] = total_ms_sec
        m_data["cpu_core_percent"] = cpu_pct
        m_data["alloc_kb_per_second"] = total_alloc_sec
        m_data["callers"] = callers_map.get((f_id, m_name), [])
        m_data["calls"] = [s.get("method") for s in m_data.get("call_steps", []) if s.get("method")]

        functions_list.append(m_data)

    functions_list.sort(key=lambda x: (x["calls_per_second"], x["total_ms_per_second"]), reverse=True)

    # 5. Build Execution Sequences (Waterfall) for top entry points
    def build_waterfall(key, visited, depth=1):
        if depth > 4 or key in visited:
            return []
        visited.add(key)
        steps = []
        obj = method_registry.get(key)
        if not obj:
            return []
        f_id, _ = key

        for idx, call in enumerate(obj.get("call_steps", []), 1):
            t_name = call["method"]
            t_low = t_name.lower()
            candidates = methods_by_name.get(t_low, [])
            best = None
            for c_fid, c_name in candidates:
                if c_fid == f_id or (call.get("object") and call["object"].lower() in Path(c_fid).stem.lower()):
                    best = (c_fid, c_name)
                    break
            if not best and candidates:
                best = candidates[0]

            sub_steps = []
            tgt_obj = method_registry.get(best) if best else None
            if best and depth < 3:
                sub_steps = build_waterfall(best, set(visited), depth + 1)

            step_data = {
                "order": idx,
                "depth": depth,
                "caller_file": f_id,
                "target_file": best[0] if best else (call.get("object") or "this"),
                "target_method": t_name,
                "arguments": call.get("args", ""),
                "is_in_loop": call.get("is_in_loop", False),
                "cycles": tgt_obj["cycles_per_call"] if tgt_obj else 150,
                "time_ms": tgt_obj["time_per_call_ms"] if tgt_obj else 0.0001,
                "mutations": tgt_obj.get("mutations", []) if tgt_obj else [],
                "complexity": tgt_obj.get("complexity", "O(1)") if tgt_obj else "O(1)",
                "substeps": sub_steps
            }
            steps.append(step_data)
        return steps

    # Select top 25 entry points for sequence waterfall
    timelines = []
    top_entries = [f for f in functions_list if f["calls_per_second"] >= 60.0 or f["method"].startswith(("_on_", "On")) or f["method"] in FRAME_60HZ][:25]
    if not top_entries:
        top_entries = functions_list[:15]

    for entry in top_entries:
        key = (entry["file"], entry["method"])
        wf_steps = build_waterfall(key, set())
        if wf_steps:
            timelines.append({
                "entry_id": f"{Path(entry['file']).name}::{entry['method']}",
                "file": entry["file"],
                "method": entry["method"],
                "frequency_hz": entry["calls_per_second"],
                "total_cycles": entry["cycles_per_call"],
                "total_ms": entry["time_per_call_ms"],
                "params": entry["params"],
                "entry_point": {
                    "name": entry["method"],
                    "file": entry["file"],
                    "frequency": entry["calls_per_second"],
                    "cycles": entry["cycles_per_call"],
                    "time_ms": entry["time_per_call_ms"]
                },
                "steps": wf_steps
            })

    active_count = len([f for f in functions_list if f["calls_per_second"] > 0])
    return {
        "functions": functions_list,
        "methods": functions_list,
        "timelines": timelines,
        "total_methods": len(functions_list),
        "active_methods": active_count,
        "summary": {
            "total_methods": len(functions_list),
            "active_methods": active_count,
            "hot_frame_methods": len([f for f in functions_list if f["calls_per_second"] >= 60.0])
        }
    }

def analyze(root, user_ignores=None):
    root = Path(root).resolve()
    if not root.exists() or not root.is_dir():
        raise ValueError("Project path is not a directory")

    ignore_patterns = set(load_graphignore(root))
    if user_ignores:
        for u in user_ignores:
            u = u.strip()
            if u:
                ignore_patterns.add(u.replace("\\", "/"))

    autoloads = parse_project_godot(root)
    autoload_paths = set(autoloads.values())

    parsed = []
    for p in walk(root, ignore_patterns):
        raw = read_text(p)
        if not raw:
            continue
        ext = p.suffix.lower()
        if ext == ".cs":
            data = parse_cs(p, raw)
        elif ext == ".gd":
            data = parse_gd(p, raw)
        elif ext == ".tscn":
            data = parse_tscn(p, raw)
        else:
            continue
        data["path"] = rel(p, root)
        data["id"] = data["path"]
        parsed.append(data)

    # Symbol indexes
    by_class = {}
    by_stem = {}
    by_ns = {}
    by_path = {}
    for f in parsed:
        by_path[f["path"]] = f
        by_stem.setdefault(Path(f["path"]).stem.lower(), []).append(f)
        for c in f["classes"]:
            by_class.setdefault(c["name"], []).append(f)
        if f.get("namespace"):
            by_ns.setdefault(f["namespace"], []).append(f)

    edges = []
    edge_seen = set()
    direct_connected = set()

    def add(src, dst, kind, confidence="medium", detail=""):
        if src == dst:
            return
        key = (src, dst, kind, detail)
        if key not in edge_seen:
            edge_seen.add(key)
            edges.append({"source": src, "target": dst, "type": kind, "confidence": confidence, "detail": detail})
            if confidence in ("high", "medium"):
                direct_connected.add((src, dst))

    GENERIC_NAMES = {
        "if","elif","else","for","foreach","while","switch","case","catch","using",
        "lock","return","nameof","typeof","sizeof","new","base","this","true","false",
        "null","void","int","string","bool","float","double","var","object","async","await",
        "get","set","add","remove","clear","contains","count","length","to_string","tostring",
        "format","split","join","replace","trim","index_of","indexof","sub","substring",
        "min","max","clamp","abs","round","floor","ceil","sqrt","pow","sin","cos",
        "run","start","stop","update","tick","init","ready","_ready","_process","_physics_process",
        "_init","_enter_tree","_exit_tree","_input","_unhandled_input","emit","connect","disconnect",
        "is","as","in","print","push_error","push_warning","assert","super","load","preload",
        "open","close","read","write","save","reset","show","hide","dispose","log","info","warn","error",
        "queue_free","free","duplicate","clone","has","pop","push","append","insert","erase",
        "find","sort","filter","map","all","any","first","last","take","skip","where","select",
        "item","node","data","test","event","manager","handler","main","helper","app","view","scene"
    }

    # Track which scripts are attached to scenes
    scene_attached_scripts = set()

    for f in parsed:
        src = f["id"]
        lang = f["language"]

        if lang == "C#":
            for c in f["classes"]:
                for base in c["bases"]:
                    for target in by_class.get(base, []):
                        add(src, target["id"], "inheritance", "high", f'{c["name"]} : {base}')
            for token in f["type_tokens"]:
                for target in by_class.get(token, []):
                    if target["id"] != src:
                        add(src, target["id"], "type-reference", "medium", token)
            for u in f["usings"]:
                targets = by_ns.get(u, [])
                if not targets:
                    targets = [t for ns_key, t_list in by_ns.items() if ns_key.startswith(u + ".") for t in t_list]
                count = 0
                for target in targets:
                    if target["id"] != src and (src, target["id"]) not in direct_connected:
                        add(src, target["id"], "namespace-using", "low", u)
                        count += 1
                        if count >= 3:
                            break

        elif lang == "GDScript":
            for ref in f.get("preloads", []):
                t_clean = ref.replace("res://", "")
                if t_clean in by_path:
                    add(src, by_path[t_clean]["id"], "resource-load", "high", ref)
                else:
                    rel_p = (Path(f["path"]).parent / ref).as_posix()
                    if rel_p in by_path:
                        add(src, by_path[rel_p]["id"], "resource-load", "high", ref)

            for c in f["classes"]:
                for base in c["bases"]:
                    for target in by_class.get(base, []):
                        if target["id"] != src:
                            add(src, target["id"], "inheritance", "high", f'extends {base}')

            for node in f.get("node_paths", []):
                stem = Path(node.split("/")[-1]).stem.lower()
                for target in by_stem.get(stem, []):
                    if target["id"] != src:
                        add(src, target["id"], "node-reference", "low", node)

        elif lang == "Scene":
            for s_path in f.get("scripts", []):
                t_clean = s_path.replace("res://", "")
                if t_clean in by_path:
                    target_id = by_path[t_clean]["id"]
                    scene_attached_scripts.add(target_id)
                    add(src, target_id, "scene-script", "high", "Attached Script")
                    for sig in f.get("connects", []):
                        if sig not in by_path[t_clean].setdefault("connects", []):
                            by_path[t_clean]["connects"].append(sig)
            for i_path in f.get("instances", []):
                t_clean = i_path.replace("res://", "")
                if t_clean in by_path:
                    add(src, by_path[t_clean]["id"], "scene-instance", "high", "Instantiated Scene")

        # Function/Method call resolution
        for call in f.get("calls", []):
            c_low = call.lower()
            if len(c_low) >= 4 and c_low not in GENERIC_NAMES:
                targets = by_stem.get(c_low, [])
                if len(targets) <= 3:
                    for target in targets:
                        if target["id"] != src and (src, target["id"]) not in direct_connected:
                            add(src, target["id"], "possible-call", "low", call)

    # Autoload global reference annotations
    for auto_name, auto_p in autoloads.items():
        if auto_p in by_path:
            auto_id = by_path[auto_p]["id"]
            for f in parsed:
                if f["id"] != auto_id and auto_name in f.get("type_tokens", []):
                    add(f["id"], auto_id, "autoload-access", "medium", f'Global Autoload: {auto_name}')

    # Graph connectivity
    incoming = {f["id"]: 0 for f in parsed}
    outgoing = {f["id"]: 0 for f in parsed}
    adjacency = {f["id"]: set() for f in parsed}
    reverse = {f["id"]: set() for f in parsed}

    for e in edges:
        s, t = e["source"], e["target"]
        if s in outgoing and t in incoming:
            outgoing[s] += 1
            incoming[t] += 1
            adjacency[s].add(t)
            reverse[t].add(s)

    # Tarjan SCC algorithm
    index = 0
    stack = []
    onstack = set()
    indices = {}
    lowlink = {}
    components = []

    def strong(v):
        nonlocal index
        indices[v] = lowlink[v] = index
        index += 1
        stack.append(v)
        onstack.add(v)
        for w in adjacency[v]:
            if w not in indices:
                strong(w)
                lowlink[v] = min(lowlink[v], lowlink[w])
            elif w in onstack:
                lowlink[v] = min(lowlink[v], indices[w])
        if lowlink[v] == indices[v]:
            comp = []
            while True:
                w = stack.pop()
                onstack.remove(w)
                comp.append(w)
                if w == v:
                    break
            if len(comp) > 1 or v in adjacency[v]:
                components.append(sorted(comp))

    sys.setrecursionlimit(max(10000, len(parsed) * 3 + 100))
    for v in adjacency:
        if v not in indices:
            strong(v)

    cycle_nodes = {n for c in components for n in c}

    # Blast Radius calculation (BFS on reverse graph)
    blast_radii = {}
    blast_nodes_map = {}
    for f in parsed:
        nid = f["id"]
        visited = set()
        queue = collections.deque([nid])
        while queue:
            curr = queue.popleft()
            for parent in reverse.get(curr, ()):
                if parent not in visited and parent != nid:
                    visited.add(parent)
                    queue.append(parent)
        blast_radii[nid] = len(visited)
        blast_nodes_map[nid] = sorted(list(visited))[:15]

    # Cross-file function callers index
    func_callers = {}
    for f in parsed:
        for c in f.get("calls", []):
            func_callers.setdefault(c, set()).add(f["id"])

    # Global Signal & Event Registry
    signal_registry = {}
    for f in parsed:
        for sig in f.get("signals", []):
            signal_registry.setdefault(sig, {"declared_in": [], "emitted_by": [], "connected_in": []})
            signal_registry[sig]["declared_in"].append(f["id"])
        for em in f.get("emits", []):
            signal_registry.setdefault(em, {"declared_in": [], "emitted_by": [], "connected_in": []})
            signal_registry[em]["emitted_by"].append(f["id"])
        for con in f.get("connects", []):
            signal_registry.setdefault(con, {"declared_in": [], "emitted_by": [], "connected_in": []})
            signal_registry[con]["connected_in"].append(f["id"])

    # Elementary cycle loops extraction & Godot preload crash detection
    cycle_details = []
    preload_cycles = []
    for comp in components:
        comp_set = set(comp)
        sub_edges = [e for e in edges if e["source"] in comp_set and e["target"] in comp_set]
        sub_edges.sort(key=lambda x: ({"low": 0, "medium": 1, "high": 2}[x["confidence"]], x["type"] != "possible-call"))
        weakest = sub_edges[0] if sub_edges else None

        has_preload_crash = False
        pl_files = []
        for nid in comp_set:
            f_obj = by_path.get(nid)
            if f_obj and f_obj.get("preloads"):
                for pl in f_obj["preloads"]:
                    pl_clean = pl.replace("res://", "").replace("\\", "/")
                    if any(target in pl_clean or pl_clean in target for target in comp_set if target != nid):
                        has_preload_crash = True
                        pl_files.append(Path(nid).name)
                        break

        cd_item = {
            "nodes": comp,
            "size": len(comp),
            "weakest_link": weakest,
            "is_preload_cycle": has_preload_crash,
            "preload_files": list(set(pl_files)),
            "recommendation": f"Разрежьте связь '{Path(weakest['source']).name}' → '{Path(weakest['target']).name}', выделив интерфейс или сигналы." if weakest else "Выделите общие структуры данных в отдельный файл."
        }
        cycle_details.append(cd_item)
        if has_preload_crash:
            preload_cycles.append((cd_item, list(set(pl_files))))

    # PageRank Centrality Calculation (25 iterations)
    all_nids = [f["id"] for f in parsed]
    n_total = len(all_nids)
    if n_total > 0:
        d_factor = 0.85
        pagerank = {nid: 1.0 / n_total for nid in all_nids}
        for _ in range(25):
            new_pr = {}
            for nid in all_nids:
                rank_sum = sum(pagerank[p] / max(1, len(adjacency.get(p, ()))) for p in reverse.get(nid, ()))
                new_pr[nid] = (1.0 - d_factor) / n_total + d_factor * rank_sum
            pagerank = new_pr
        max_pr = max(pagerank.values()) if pagerank else 1.0
        normalized_pr = {k: round((v / max_pr) * 100, 1) for k, v in pagerank.items()}
    else:
        normalized_pr = {}

    # Git Temporal Coupling (co-churn)
    parsed_ids_set = {f["id"] for f in parsed}
    temporal_couplings = compute_temporal_coupling(root, parsed_ids_set)

    # Prepare enriched node data
    files = []
    god_objects = []
    truly_dead = []
    scene_entry_points = []
    layer_violations = []
    hot_paths_list = []

    for f in parsed:
        nid = f["id"]
        fan_in = incoming[nid]
        fan_out = outgoing[nid]
        total_deg = fan_in + fan_out
        is_auto = (f["path"] in autoload_paths) or any(f["path"].endswith(ap) for ap in autoload_paths)
        layer = classify_layer(f["path"], is_auto)

        instability = (fan_out / total_deg) if total_deg > 0 else 0.5
        abstractness = f.get("abstractness", 0.0)
        dist_seq = abs(abstractness + instability - 1.0)

        is_god = (total_deg >= 12 and f["line_count"] >= 400)
        if is_god:
            god_objects.append(nid)

        # Dead code / Orphan classification
        is_attached = nid in scene_attached_scripts
        is_orph = (total_deg == 0)
        orphan_status = "active"
        if is_orph:
            if is_attached or f["language"] == "Scene":
                orphan_status = "scene_entry_point"
                scene_entry_points.append(nid)
            else:
                orphan_status = "truly_dead"
                truly_dead.append(nid)

        if layer in ("core", "domain"):
            for dep in adjacency.get(nid, ()):
                target_f = by_path.get(dep)
                if target_f and classify_layer(target_f["path"]) == "presentation":
                    layer_violations.append((nid, dep))

        # Enriched function intelligence (Callers, Callees, Data Providers, Hot-Path)
        raw_methods = f.get("methods", [])
        detailed_funcs = []
        for fn in raw_methods:
            name = fn.get("name") or "anonymous"
            callers = sorted(list(func_callers.get(name, set()) - {nid}))
            triggering_signals = [sig for sig in f.get("signals", []) if sig.lower() in name.lower()]
            accessed_autoloads = [auto_name for auto_name in autoloads if auto_name in f.get("type_tokens", [])]
            is_hot = fn.get("is_hot_path", False)
            if is_hot:
                hot_paths_list.append({"file": nid, "method": name, "params": fn.get("params", "")})

            p = fn.get("passport", {})
            detailed_funcs.append({
                "name": name,
                "params": fn.get("params", ""),
                "return_type": fn.get("return_type", ""),
                "callers": callers,
                "is_hot_path": is_hot,
                "triggering_signals": triggering_signals,
                "accessed_autoloads": accessed_autoloads,
                "docstring": p.get("docstring") or fn.get("doc", ""),
                "summary_ru": p.get("summary_ru", ""),
                "mutations": p.get("mutations", []),
                "emits": p.get("emits", []),
                "spawns": p.get("spawns", []),
                "call_steps": p.get("call_steps", []),
                "complexity": p.get("complexity", "O(1)"),
                "cycles_per_call": p.get("cycles_per_call", 120),
                "time_per_call_ms": p.get("time_per_call_ms", 0.00003),
                "alloc_kb_per_call": p.get("alloc_kb_per_call", 0.0),
            })

        files.append({
            "id": nid,
            "path": nid,
            "language": f["language"],
            "classes": [c["name"] for c in f["classes"]],
            "namespace": f["namespace"],
            "lines": f["line_count"],
            "fan_in": fan_in,
            "fan_out": fan_out,
            "degree": total_deg,
            "in_cycle": nid in cycle_nodes,
            "is_autoload": is_auto,
            "layer": layer,
            "instability": round(instability, 2),
            "abstractness": abstractness,
            "distance_sequence": round(dist_seq, 2),
            "blast_radius": blast_radii.get(nid, 0),
            "blast_sample": blast_nodes_map.get(nid, []),
            "pagerank": normalized_pr.get(nid, 0.0),
            "is_god_object": is_god,
            "is_orphan": is_orph,
            "orphan_status": orphan_status,
            "is_scene_attached": is_attached,
            "signals": f.get("signals", []),
            "functions": f.get("functions", []),
            "functions_detail": detailed_funcs,
            "skeleton": f.get("skeleton", ""),
            "node_paths": f.get("node_paths", []),
            "node_tree": f.get("node_tree", []),
            "preloads": f.get("preloads", [])
        })

    # Architectural Findings
    findings = []
    def add_finding(sev, code, title, desc, n_list):
        findings.append({"severity": sev, "code": code, "title": title, "description": desc, "nodes": n_list})

    for cd in cycle_details:
        add_finding("error", "CYCLE", "Циклическая зависимость (петля)", f"{cd['size']} файлов замкнуты в бесконечный круг зависимостей. {cd['recommendation']}", cd["nodes"])

    for cd, pl_files in preload_cycles:
        add_finding(
            "error",
            "PRELOAD_CRASH",
            "🔥 Критический риск краша Godot (preload-петля)",
            f"Файлы замкнуты в цикл через preload(): {', '.join(pl_files)}. В Godot взаимный preload() вызывает зависание компилятора или падение игры при загрузке. Замените preload() на load() или используйте сигналы!",
            cd["nodes"]
        )

    for nid, dep in layer_violations:
        add_finding("warning", "LAYER_INVERSION", "Нарушение чистой архитектуры (инверсия слоев)", f"Базовый компонент '{nid}' напрямую импортирует визуальный слой UI '{dep}'. Это делает код жестким.", [nid, dep])

    for tc in temporal_couplings:
        f1, f2 = tc["file_a"], tc["file_b"]
        if f2 not in adjacency.get(f1, ()) and f1 not in adjacency.get(f2, ()):
            add_finding(
                "warning",
                "TEMPORAL_COUPLING",
                "Скрытая темпоральная зависимость (Git Churn)",
                f"Файлы '{Path(f1).name}' и '{Path(f2).name}' менялись вместе в {tc['churn_percent']}% коммитов ({tc['co_commits']} раз), но не имеют связей в коде. Возможна скрытая архитектурная связанность или дублирование логики.",
                [f1, f2]
            )

    for f in files:
        if f["is_god_object"]:
            add_finding("warning", "GOD_OBJECT", "Монолитный класс (God-Object)", f"'{f['path']}' содержит {f['degree']} связей и {f['lines']} строк кода. Он делает слишком много, нарушая принцип единственной ответственности.", [f["id"]])
        if f["fan_in"] >= 15:
            add_finding("warning", "HIGH_FAN_IN", "Критический архитектурный фундамент", f"'{f['path']}' является опорой для {f['fan_in']} компонентов (Радиус поражения: {f['blast_radius']} файлов). Будьте осторожны: любые правки могут вызвать регрессию.", [f["id"]])
        if f["fan_out"] >= 18:
            add_finding("warning", "HIGH_FAN_OUT", "Избыточная внешняя связанность", f"'{f['path']}' зависит от {f['fan_out']} различных файлов. Высокий риск хрупкости.", [f["id"]])
        if f["instability"] <= 0.15 and f["lines"] >= 450 and f["abstractness"] == 0:
            add_finding("warning", "ZONE_OF_PAIN", "Зона боли (Жесткое бетонное ядро)", f"'{f['path']}' используется всеми, но написан как конкретный монолит без единого интерфейса. Его очень трудно тестировать.", [f["id"]])
        if f["orphan_status"] == "truly_dead":
            add_finding("info", "TRULY_DEAD", "Мертвый код (0 связей в коде и сценах)", f"'{f['path']}' никем не вызывается и не прикреплен ни к одной сцене. Скорее всего, это забытый черновик или старый файл.", [f["id"]])

    findings.sort(key=lambda x: ({"error": 0, "warning": 1, "info": 2}[x["severity"]], x["title"]))

    # System Health Score
    score = 100
    score -= min(40, len(components) * 15)
    score -= min(25, len(god_objects) * 5)
    score -= min(20, len(layer_violations) * 5)
    score -= min(15, len(preload_cycles) * 10)
    if len(files) > 0 and len(truly_dead) / len(files) > 0.25:
        score -= 10
    health_score = max(10, score)

    profiling = run_profiler_simulation(parsed, edges, root)
    files_map = profiling.get("files_map", {})
    for f in files:
        f["profiling"] = files_map.get(f["id"], {
            "file": f["id"], "ms_per_frame": 0.01, "alloc_kb_per_sec": 0.0, "calls_per_frame": 1, "cpu_percent": 0.1, "is_bottleneck": False, "hot_methods": [], "reason": "Оптимально"
        })
    event_flows = build_event_flows(parsed, edges)
    chronology = compute_function_execution_metrics(parsed, edges)

    # Map enriched metrics and reverse callers back into files.functions_detail
    chron_map = {(m["file_path"], m["name"]): m for m in chronology.get("methods", [])}
    for f in files:
        for fn in f.get("functions_detail", []):
            m_enr = chron_map.get((f["id"], fn["name"]))
            if m_enr:
                fn["calls_per_second"] = m_enr.get("calls_per_second", 0.0)
                fn["cycles_per_second"] = m_enr.get("cycles_per_second", 0)
                fn["total_ms_per_second"] = m_enr.get("total_ms_per_second", 0.0)
                fn["complexity"] = m_enr.get("complexity", "O(1)")
                fn["callers"] = m_enr.get("callers", [])
                fn["calls"] = m_enr.get("calls", [])

    langs = {"C#": 0, "GDScript": 0, "Scene": 0}
    for f in files:
        langs[f["language"]] = langs.get(f["language"], 0) + 1

    return {
        "version": "2.3.0",
        "root": str(root),
        "health_score": health_score,
        "files": files,
        "edges": edges,
        "findings": findings,
        "cycles": components,
        "cycle_details": cycle_details,
        "autoloads": autoloads,
        "signal_registry": signal_registry,
        "hot_paths": hot_paths_list,
        "profiling": profiling,
        "event_flows": event_flows,
        "chronology": chronology,
        "temporal_coupling": temporal_couplings,
        "applied_ignores": sorted(list(ignore_patterns)),
        "orphans_summary": {
            "truly_dead": truly_dead,
            "scene_entry_points": scene_entry_points
        },
        "stats": {
            "files": len(files),
            "edges": len(edges),
            "lines": sum(f["lines"] for f in files),
            "csharp": langs.get("C#", 0),
            "gdscript": langs.get("GDScript", 0),
            "scenes": langs.get("Scene", 0),
            "cycles": len(components),
            "god_objects": len(god_objects),
            "truly_dead": len(truly_dead),
            "scene_entry_points": len(scene_entry_points),
            "hot_paths": len(hot_paths_list),
            "bottlenecks": len(profiling["bottlenecks"]),
            "event_flows": len(event_flows),
            "temporal_couplings": len(temporal_couplings),
            "total_methods": chronology["summary"]["total_methods"],
            "active_methods": chronology["summary"]["active_methods"],
            "findings": len(findings),
            "health_score": health_score
        }
    }

def open_browser(url):
    import time, webbrowser
    time.sleep(0.7)
    try:
        webbrowser.open(url)
    except Exception:
        pass

class Handler(SimpleHTTPRequestHandler):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, directory=str(WEB_DIR), **kwargs)

    def log_message(self, fmt, *args):
        print("[http]", fmt % args)

    def do_POST(self):
        if self.path == "/api/browse":
            try:
                folder = choose_directory()
                data = json.dumps({"path": folder}, ensure_ascii=False).encode("utf-8")
                self.send_response(200)
                self.send_header("Content-Type", "application/json; charset=utf-8")
                self.send_header("Content-Length", str(len(data)))
                self.send_header("Cache-Control", "no-store")
                self.end_headers()
                self.wfile.write(data)
            except Exception as e:
                data = json.dumps({"error": str(e)}, ensure_ascii=False).encode("utf-8")
                self.send_response(500)
                self.send_header("Content-Type", "application/json; charset=utf-8")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)
            return

        if self.path == "/api/analyze":
            try:
                n = int(self.headers.get("Content-Length", "0"))
                payload = json.loads(self.rfile.read(n) or b"{}")
                path = payload.get("path", "").strip()
                user_ignores = payload.get("ignore", [])
                if not path:
                    raise ValueError("Enter an absolute path to your project folder.")
                result = analyze(path, user_ignores=user_ignores)
                data = json.dumps(result, ensure_ascii=False).encode("utf-8")
                self.send_response(200)
                self.send_header("Content-Type", "application/json; charset=utf-8")
                self.send_header("Content-Length", str(len(data)))
                self.send_header("Cache-Control", "no-store")
                self.end_headers()
                self.wfile.write(data)
            except Exception as e:
                data = json.dumps({"error": str(e)}, ensure_ascii=False).encode("utf-8")
                self.send_response(400)
                self.send_header("Content-Type", "application/json; charset=utf-8")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)
            return

        if self.path == "/api/browse_ignore":
            try:
                n = int(self.headers.get("Content-Length", "0"))
                payload = json.loads(self.rfile.read(n) or b"{}")
                project_path = payload.get("project_path", "").strip()
                mode = payload.get("mode", "folder")
                patterns = choose_ignore_paths(project_path, mode)
                data = json.dumps({"patterns": patterns}, ensure_ascii=False).encode("utf-8")
                self.send_response(200)
                self.send_header("Content-Type", "application/json; charset=utf-8")
                self.send_header("Content-Length", str(len(data)))
                self.send_header("Cache-Control", "no-store")
                self.end_headers()
                self.wfile.write(data)
            except Exception as e:
                data = json.dumps({"error": str(e)}, ensure_ascii=False).encode("utf-8")
                self.send_response(500)
                self.send_header("Content-Type", "application/json; charset=utf-8")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)
            return

        if self.path == "/api/save_ignore":
            try:
                n = int(self.headers.get("Content-Length", "0"))
                payload = json.loads(self.rfile.read(n) or b"{}")
                path = payload.get("path", "").strip()
                patterns = payload.get("patterns") or payload.get("ignores", [])
                if path and Path(path).is_dir():
                    ig_file = Path(path) / ".graphignore"
                    ig_file.write_text("\n".join(patterns) + "\n", encoding="utf-8")
                data = json.dumps({"status": "saved", "patterns": patterns}, ensure_ascii=False).encode("utf-8")
                self.send_response(200)
                self.send_header("Content-Type", "application/json; charset=utf-8")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)
            except Exception as e:
                data = json.dumps({"error": str(e)}, ensure_ascii=False).encode("utf-8")
                self.send_response(500)
                self.send_header("Content-Type", "application/json; charset=utf-8")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)
            return

        if self.path == "/api/query":
            try:
                n = int(self.headers.get("Content-Length", "0"))
                payload = json.loads(self.rfile.read(n) or b"{}")
                path = payload.get("path") or payload.get("project", "")
                if not path:
                    raise ValueError("Укажите путь к проекту (path).")
                user_ign = payload.get("ignore", [])
                res = analyze(path, user_ignores=user_ign)

                file_q = payload.get("file")
                func_q = payload.get("func") or payload.get("method")
                fields_q = payload.get("fields") or payload.get("field")
                sig_q = payload.get("signal")
                min_hz_q = payload.get("min_hz")
                has_mut_q = payload.get("has_mutations", False)
                has_loop_q = payload.get("has_loops", False)
                limit_q = payload.get("limit")
                budget_q = payload.get("budget")
                fmt_q = payload.get("format", "json")

                output = query_codebase(
                    res,
                    file=file_q,
                    func=func_q,
                    fields=fields_q,
                    signal=sig_q,
                    min_hz=min_hz_q,
                    has_mutations=has_mut_q,
                    has_loops=has_loop_q,
                    limit=limit_q,
                    budget=budget_q,
                    output_format=fmt_q
                )

                content_type = "application/json; charset=utf-8" if fmt_q == "json" else "text/plain; charset=utf-8"
                data = output.encode("utf-8") if isinstance(output, str) else json.dumps(output, ensure_ascii=False).encode("utf-8")
                self.send_response(200)
                self.send_header("Content-Type", content_type)
                self.send_header("Content-Length", str(len(data)))
                self.send_header("Cache-Control", "no-store")
                self.end_headers()
                self.wfile.write(data)
            except Exception as e:
                data = json.dumps({"error": str(e)}, ensure_ascii=False).encode("utf-8")
                self.send_response(400)
                self.send_header("Content-Type", "application/json; charset=utf-8")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)
            return

        if self.path == "/api/function_detail":
            try:
                n = int(self.headers.get("Content-Length", "0"))
                payload = json.loads(self.rfile.read(n) or b"{}")
                path = payload.get("path") or payload.get("project", "")
                func_name = payload.get("method") or payload.get("func", "")
                file_name = payload.get("file", "")
                if not path or not func_name:
                    raise ValueError("Укажите path и method/func.")
                res = analyze(path)
                dd = get_function_deep_dive(res, func_name, file_query=file_name)
                data = json.dumps(dd, ensure_ascii=False).encode("utf-8")
                self.send_response(200)
                self.send_header("Content-Type", "application/json; charset=utf-8")
                self.send_header("Content-Length", str(len(data)))
                self.send_header("Cache-Control", "no-store")
                self.end_headers()
                self.wfile.write(data)
            except Exception as e:
                data = json.dumps({"error": str(e)}, ensure_ascii=False).encode("utf-8")
                self.send_response(400)
                self.send_header("Content-Type", "application/json; charset=utf-8")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)
            return

        self.send_error(404)

# Text & Markdown Formatters for AI & CLI
def fmt_caller(c):
    """Caller бывает строкой (file id) или словарём из chronology-метрик.
    Слепой ', '.join по словарям падал с TypeError:
    'sequence item 0: expected str instance, dict found' — ломал всю команду --deps."""
    if isinstance(c, dict):
        return f"{c.get('caller_file') or '?'}::{c.get('caller_method') or '?'}()"
    return str(c)


def format_file_deps_text(result, target_path):
    target = next((f for f in result["files"] if f["path"].lower() == target_path.lower() or f["path"].lower().endswith(target_path.lower())), None)
    if not target:
        return f"Файл '{target_path}' не найден в проекте."

    in_edges = [e for e in result["edges"] if e["target"] == target["id"]]
    out_edges = [e for e in result["edges"] if e["source"] == target["id"]]

    lines = [
        f"Файл: {target['path']}",
        f"Язык: {target['language']} | Слой: {target['layer']} | Нестабильность I: {target['instability']}",
        f"Радиус поражения (Blast Radius): {target['blast_radius']} файлов",
        f"Всего связанных файлов: {len(in_edges) + len(out_edges)}",
        "=" * 50,
        f"← ВХОДЯЩИЕ ЗАВИСИМОСТИ (файлы, зависящие от {Path(target['path']).name}): {len(in_edges)}"
    ]
    for e in in_edges:
        lines.append(f"  • {e['source']}  [{e['type']}, {e['confidence']}] {e.get('detail', '')}")
    if not in_edges:
        lines.append("  (нет входящих зависимостей)")

    lines.append("")
    lines.append(f"→ ИСХОДЯЩИЕ ЗАВИСИМОСТИ ({Path(target['path']).name} зависит от этих файлов): {len(out_edges)}")
    for e in out_edges:
        lines.append(f"  • {e['target']}  [{e['type']}, {e['confidence']}] {e.get('detail', '')}")
    if not out_edges:
        lines.append("  (нет исходящих зависимостей)")

    if target.get("functions_detail"):
        lines.append("")
        lines.append(f"ФУНКЦИИ И МЕТОДЫ ({len(target['functions_detail'])}):")
        for fn in target["functions_detail"]:
            hot = " [⚡ 60-FPS HOT-PATH]" if fn.get("is_hot_path") else ""
            callers_str = (" | Вызывается в: " + ", ".join(fmt_caller(c) for c in fn['callers'])) if fn['callers'] else " | Локальный вызов"
            lines.append(f"  - {fn['name']}({fn.get('params', '')}){hot}{callers_str}")

    lines.append("=" * 50)
    return "\n".join(lines)

def format_signals_text(result):
    sig_map = result.get("signal_registry", {})
    if not sig_map:
        return "В проекте не найдено сигналов или событий."
    lines = [
        f"# КАРТА СИГНАЛОВ И СОБЫТИЙ GODOT / C# ({len(sig_map)} сигналов)",
        "Связывает слабосвязанные компоненты, предотвращая поломку слушателей при рефакторинге.",
        "=" * 60
    ]
    for sig, data in sorted(sig_map.items()):
        lines.append(f"\n📡 Сигнал / Событие: '{sig}'")
        if data["declared_in"]:
            lines.append(f"  • Объявлен в: {', '.join(data['declared_in'])}")
        if data["emitted_by"]:
            lines.append(f"  • Эмиттится (триггерится): {', '.join(data['emitted_by'])}")
        if data["connected_in"]:
            lines.append(f"  • Слушатели (.connect): {', '.join(data['connected_in'])}")
        if not data["emitted_by"] and not data["connected_in"]:
            lines.append("  • (не имеет зарегистрированных эмиттеров или слушателей)")
    return "\n".join(lines)

def format_scenes_text(result):
    scenes = [f for f in result["files"] if f["language"] == "Scene"]
    if not scenes:
        return "В проекте не найдено файлов сцен .tscn."
    lines = [
        f"# ДЕРЕВО НОД СЦЕН GODOT (.tscn DOM Hierarchy) — {len(scenes)} сцен",
        "Используйте эти пути для исключения галлюцинаций с $NodePath и %UniqueNode.",
        "=" * 60
    ]
    for sc in scenes:
        lines.append(f"\n🌲 Сцена: {sc['path']}")
        tree = sc.get("node_tree", [])
        if tree:
            for n in tree:
                parent_str = f" (parent: {n['parent']})" if n['parent'] else " [ROOT]"
                lines.append(f"  • ${n['path']} [{n['type']}]{parent_str}")
        else:
            lines.append("  (дерево нод пусто)")
    return "\n".join(lines)

def format_orphans_text(result):
    orphans = result.get("orphans_summary", {})
    dead = orphans.get("truly_dead", [])
    entry = orphans.get("scene_entry_points", [])

    lines = [
        "# РЕВИЗИЯ НЕИСПОЛЬЗУЕМОГО И ИЗОЛИРОВАННОГО КОДА",
        f"Найдено абсолютно мертвых файлов: {len(dead)} | Точек входа сцен: {len(entry)}",
        "=" * 60
    ]
    lines.append("\n💀 АБСОЛЮТНО МЕРТВЫЕ ФАЙЛЫ (0 связей в коде и 0 в сценах — можно удалять):")
    if dead:
        for d in dead: lines.append(f"  - {d}")
    else:
        lines.append("  (нет абсолютно мертвых файлов)")

    lines.append("\n🎬 ТОЧКИ ВХОДА СЦЕН (прикреплены к .tscn, но из скриптов не вызываются):")
    if entry:
        for e in entry: lines.append(f"  - {e}")
    else:
        lines.append("  (нет таких файлов)")
    return "\n".join(lines)

def format_hotpaths_text(result):
    hots = result.get("hot_paths", [])
    lines = [
        f"# КАРТА ГОРЯЧИХ ПУТЕЙ (60-FPS HOT-PATHS) — {len(hots)} методов",
        "ВНИМАНИЕ ДЛЯ ИИ: Эти методы выполняются каждый кадр. Не аллоцируйте объекты и не делайте тяжелые циклы!",
        "=" * 60
    ]
    if hots:
        for h in hots:
            lines.append(f"  ⚡ {h['file']} -> {h['method']}({h['params']})")
    else:
        lines.append("  (покадровых методов _process / Update не обнаружено)")
    return "\n".join(lines)

def format_all_deps_text(result):
    lines = [
        f"# КАРТА ЗАВИСИМОСТЕЙ ПРОЕКТА: {result['root']}",
        f"Всего файлов: {result['stats']['files']}, Связей: {result['stats']['edges']}",
        "=" * 60
    ]
    for f in sorted(result["files"], key=lambda x: x["path"]):
        in_edges = [e["source"] for e in result["edges"] if e["target"] == f["id"]]
        out_edges = [e["target"] for e in result["edges"] if e["source"] == f["id"]]
        lines.append(f"\n[{f['path']}] ({f['language']}, LoC: {f['lines']}, I: {f['instability']})")
        if in_edges:
            lines.append(f"  ← Входящие ({len(in_edges)}): {', '.join(in_edges)}")
        if out_edges:
            lines.append(f"  → Исходящие ({len(out_edges)}): {', '.join(out_edges)}")
        if not in_edges and not out_edges:
            lines.append("  (изолированный файл)")
    return "\n".join(lines)

def format_benchmark_text(result):
    prof = result.get("profiling", {})
    files_prof = prof.get("files", [])
    if isinstance(files_prof, dict):
        file_items = list(files_prof.values())
    else:
        file_items = list(files_prof)
    lines = [
        f"# СИМУЛЯЦИЯ НАГРУЗКИ И ПРОФАЙЛИНГ CPU (Целевой кадр: 16.6 мс)",
        f"Общее процессорное время кадра: ~{prof.get('total_frame_ms', 0)} мс | Прогнозный FPS: {prof.get('target_fps', 60)}",
        f"Узких мест (Bottlenecks): {len(prof.get('bottlenecks', []))}",
        "=" * 60
    ]
    sorted_files = sorted(file_items, key=lambda x: x.get("ms_per_frame", 0), reverse=True)
    for p in sorted_files[:30]:
        fid = p.get("file", "unknown")
        flag = " [🔥 БУТЫЛОЧНОЕ ГОРЛЫШКО]" if p.get("is_bottleneck") else ""
        lines.append(f"• {fid}: {p.get('ms_per_frame', 0)} мс/кадр ({p.get('cpu_percent', 0)}% CPU) | Аллокации: {p.get('alloc_kb_per_sec', 0)} КБ/с | Вызовов: {p.get('calls_per_frame', 0)}{flag}")
    return "\n".join(lines)

def format_event_flows_text(result):
    flows = result.get("event_flows", [])
    if not flows:
        return "Сквозных цепочек Event-Flow не обнаружено."
    lines = [
        f"# СКВОЗНЫЕ ЦЕПОЧКИ EVENT-FLOW ({len(flows)} цепочек)",
        "Трассировка сквозного потока событий от триггера до обработчиков.",
        "=" * 60
    ]
    for idx, fl in enumerate(flows, 1):
        src_file = fl.get("origin_file") or fl.get("source_file", "unknown")
        src_meth = fl.get("source_method", "")
        lines.append(f"⚡ Цепочка #{idx}: Сигнал/Событие '{fl['signal']}'")
        lines.append(f"  Исток: {src_file} :: {src_meth}()")
        for l in fl.get("listeners", []):
            l_target = l.get("file") or l.get("target_file", "unknown")
            handlers = ", ".join(l.get("handlers", [])) or l.get("method", "обработчик")
            lines.append(f"  └──> Слушатель: {l_target} ({handlers})")
            downstream = l.get("calls") or l.get("downstream_calls", [])
            if downstream:
                lines.append(f"       Вторичные вызовы: {', '.join(downstream)}")
        lines.append("")
    return "\n".join(lines)

def format_passports_text(result, target_arg):
    lines = [
        "# ПАСПОРТА ПОВЕДЕНИЯ ФУНКЦИЙ (МУТАЦИИ, СПАВНЫ, СИГНАЛЫ)",
        "=" * 60
    ]
    for f in result.get("files", []):
        if target_arg != "all" and not (f["path"].lower() == target_arg.lower() or f["path"].lower().endswith(target_arg.lower())):
            continue
        funcs = f.get("functions_detail", [])
        if not funcs:
            continue
        lines.append(f"\n## Файл: {f['path']}")
        for fn in funcs:
            passp = fn.get("passport", {})
            summary = fn.get("summary_ru") or passp.get("summary_ru", "Внутренняя логика")
            doc_str = fn.get("docstring") or fn.get("doc") or passp.get("docstring", "")
            doc = f" // {doc_str}" if doc_str else ""
            lines.append(f"  • {fn['name']}({fn.get('params', '')}){doc}")
            lines.append(f"    {summary}")
    return "\n".join(lines)

def format_chronology_text(result):
    chron = result.get("chronology", {})
    methods = chron.get("methods", [])
    timelines = chron.get("timelines", [])
    lines = [
        "# ХРОНОЛОГИЯ ВЫЗОВОВ, ПЕРЕДАЧА ДАННЫХ И ТАКТЫ CPU",
        "=" * 60,
        f"Всего методов: {chron.get('total_methods', len(methods))} | Активных в симуляции: {chron.get('active_methods', 0)}",
        "",
        "## ТОП МЕТОДОВ ПО ЧАСТОТЕ ВЫЗОВОВ И НАГРУЗКЕ CPU",
        "| Метод / Функция | Файл | Вызовов/сек (Hz) | Тактов CPU | Время/вызов (мс) | Суммарно мс/сек | Сложность |",
        "| :--- | :--- | :--- | :--- | :--- | :--- | :--- |"
    ]
    sorted_m = sorted(methods, key=lambda m: (m.get("calls_per_second", 0), m.get("cycles_per_call", 0)), reverse=True)
    for m in sorted_m[:35]:
        hz = f"{m.get('calls_per_second', 0):.2f}".rstrip("0").rstrip(".")
        cyc = f"{m.get('cycles_per_call', 0):,}".replace(",", " ")
        ms_c = f"{m.get('time_per_call_ms', 0):.4f}"
        tot_ms = f"{m.get('total_ms_per_second', 0):.2f}"
        lines.append(f"| `{m.get('name')}` | `{m.get('file_path')}` | **{hz} Hz** | {cyc} | {ms_c} ms | {tot_ms} ms | `{m.get('complexity', 'O(1)')}` |")

    lines.extend([
        "",
        "## ПОСЛЕДОВАТЕЛЬНОСТЬ ВЫПОЛНЕНИЯ И ПЕРЕДАЧА ДАННЫХ (WATERFALL FLOW)",
    ])
    for tl in timelines[:10]:
        ep = tl.get("entry_point", {})
        lines.append(f"\n### Точка входа: `{ep.get('name')}` ({ep.get('frequency', 60)} Hz) в `{ep.get('file')}`")
        steps = tl.get("steps", [])
        if not steps:
            lines.append("  (Внутренних шагов и вызовов не зафиксировано)")
        for st in steps:
            indent = "  " * st.get("depth", 1)
            args_str = f" args: {st.get('arguments')}" if st.get("arguments") else ""
            muts = f" [мутирует: {', '.join(st.get('mutations'))}]" if st.get("mutations") else ""
            cyc = f"{st.get('cycles', 0):,} тактов".replace(",", " ")
            lines.append(f"{indent}↳ Шаг {st.get('order')}: `{st.get('target_method')}` ({st.get('target_file')}){args_str}{muts} ~{cyc}")

    return "\n".join(lines)

def get_function_deep_dive(result, method_query, file_query=None):
    """
    Returns complete 360-degree introspection for a specific method across all files or within a target file.
    Includes signature, purpose, callers, callees, mutations, signals, takts CPU, frequency, complexity, and flow.
    """
    chron = result.get("chronology", {})
    methods = chron.get("methods", [])

    m_low = method_query.lower().strip()
    matches = []
    for m in methods:
        name_match = (m["name"].lower() == m_low) or (m_low in m["name"].lower())
        if not name_match:
            continue
        if file_query:
            f_low = file_query.lower().strip()
            if f_low not in m["file_path"].lower():
                continue
        matches.append(m)

    if not matches:
        return {"error": f"Функция '{method_query}' не найдена.", "query": method_query, "matches": []}

    enriched = []
    for m in matches:
        timelines = [tl for tl in chron.get("timelines", []) if tl.get("file") == m["file_path"] and tl.get("method") == m["name"]]
        waterfall = timelines[0]["steps"] if timelines else []

        related_flows = []
        for ef in result.get("event_flows", []):
            if ef.get("origin_file") == m["file_path"] and ef.get("source_method") == m["name"]:
                related_flows.append({"role": "emitter", "signal": ef.get("signal"), "flow": ef})
            for lis in ef.get("listeners", []):
                if lis.get("file") == m["file_path"] and lis.get("method") == m["name"]:
                    related_flows.append({"role": "listener", "signal": ef.get("signal"), "flow": ef})

        item = dict(m)
        item["waterfall_steps"] = waterfall
        item["event_flows"] = related_flows
        enriched.append(item)

    return {"query": method_query, "file": file_query, "count": len(enriched), "matches": enriched}

def format_function_deep_dive_text(deep_dive):
    if "error" in deep_dive and not deep_dive.get("matches"):
        return f"# Ошибка: {deep_dive['error']}"
    matches = deep_dive.get("matches", [])
    lines = [
        f"# ДЕТАЛЬНЫЙ 360° РЕНТГЕН ФУНКЦИИ: '{deep_dive.get('query')}' ({len(matches)} совпадений)",
        "=" * 60
    ]
    for m in matches:
        hz = m.get("calls_per_second", 0)
        cyc = m.get("cycles_per_call", 0)
        tot_cyc = m.get("cycles_per_second", 0)
        ms = m.get("time_per_call_ms", 0)
        tot_ms = m.get("total_ms_per_second", 0)
        alloc = m.get("alloc_kb_per_call", 0)
        complexity = m.get("complexity", "O(1)")
        hot = "🔥 60-FPS HOT-PATH" if m.get("is_hot_path") or hz >= 60 else "Обычный вызов"

        lines.extend([
            f"\n## Функция: `{m['name']}({m.get('params', '')}) -> {m.get('return_type', 'void')}`",
            f"- 📁 **Файл**: `{m.get('file_path')}` ({m.get('language')})",
            f"- 📖 **Документация**: *{m.get('doc') or 'Не указана'}*",
            f"- 🎯 **Суть (поведение)**: {m.get('summary_ru') or 'Внутренняя логика'}",
            "",
            "### ⏱️ Метрики нагрузки и частоты вызовов:",
            f"- **Частота в секунду**: **{hz:.1f} Hz** ({hot})",
            f"- **Тактов CPU / вызов**: **{cyc:,} тактов** (~3.5 GHz)".replace(",", " "),
            f"- **Время / вызов**: **{ms:.4f} мс** (суммарно: {tot_ms:.2f} мс/сек, {m.get('cpu_core_percent', 0)}% ядра)",
            f"- **Память GC / вызов**: **{alloc:.2f} КБ**",
            f"- **Алгоритмическая сложность**: `{complexity}`",
            "",
            "### ⚡ Влияние на состояние и данные (Data Mutations):"
        ])
        muts = m.get("mutations", [])
        if muts:
            lines.append(f"- **Мутирует переменные состояния**: `{', '.join(muts)}`")
        else:
            lines.append("- **Мутации состояния**: (чистая функция / без мутаций)")

        emits = m.get("emits", [])
        if emits:
            lines.append(f"- **Испускает сигналы**: `{', '.join(emits)}`")

        spawns = m.get("spawns", [])
        if spawns:
            lines.append(f"- **Создает объекты (new)**: `{', '.join(spawns)}`")

        callers = m.get("callers", [])
        lines.append("\n### 📥 Кто вызывает эту функцию (Incoming Callers):")
        if callers:
            for c in callers:
                loop_note = " [в цикле! 🔄]" if c.get("is_in_loop") else ""
                args_note = f" с args: `{c.get('arguments')}`" if c.get("arguments") else ""
                lines.append(f"  • `{c.get('caller_file')}::{c.get('caller_method')}()`{args_note}{loop_note}")
        else:
            lines.append("  • (Прямых вызовов в коде не найдено — возможно, вызывается движком, через сигнал или рефлексию)")

        callees = m.get("call_steps", [])
        lines.append("\n### 📤 Кого вызывает эта функция (Outgoing Calls):")
        if callees:
            for s in callees:
                loop_note = " [в цикле! 🔄]" if s.get("is_in_loop") else ""
                args_note = f" args: `{s.get('args')}`" if s.get("args") else ""
                lines.append(f"  ↳ `{s.get('method')}()`{args_note}{loop_note}")
        else:
            lines.append("  ↳ (Вторичных вызовов не зафиксировано)")

        wf = m.get("waterfall_steps", [])
        if wf:
            lines.append("\n### 🌊 Хронологическая последовательность (Waterfall Flow):")
            for st in wf:
                indent = "  " * st.get("depth", 1)
                mut_str = f" [мутирует: {', '.join(st.get('mutations'))}]" if st.get("mutations") else ""
                lines.append(f"{indent}↳ Шаг {st.get('order')}: `{st.get('target_method')}()` ({st.get('target_file')}){mut_str} ~{st.get('cycles', 0):,} тактов".replace(",", " "))

        efs = m.get("event_flows", [])
        if efs:
            lines.append("\n### 📡 Связи в цепочках Event-Flow:")
            for ef_item in efs:
                lines.append(f"  • Роль: **{ef_item['role']}** в сигнале `{ef_item['signal']}`")

        lines.append("\n" + "-" * 50)

    return "\n".join(lines)

def query_codebase(result, file=None, func=None, fields=None, signal=None, min_hz=None, has_mutations=None, has_loops=None, limit=None, budget=None, output_format="json"):
    """
    Granular projection query engine for AI agents and scripts.
    Allows extracting ONLY requested fields (e.g. only 'mutations' or only 'flow' or only 'cycles,hz'),
    saving up to 99% of token context budget.
    """
    chron = result.get("chronology", {})
    methods = chron.get("methods", [])

    requested_fields = None
    if fields:
        if isinstance(fields, str):
            requested_fields = [f.strip().lower() for f in fields.split(",") if f.strip()]
        elif isinstance(fields, (list, tuple)):
            requested_fields = [str(f).strip().lower() for f in fields if f]

    f_filter = file.strip().lower() if file else None
    fn_filter = func.strip().lower() if func else None
    sig_filter = signal.strip().lower() if signal else None

    filtered = []
    for m in methods:
        if f_filter and f_filter not in m["file_path"].lower():
            continue
        if fn_filter:
            m_low = m["name"].lower()
            if fn_filter != m_low and fn_filter not in m_low:
                continue
        if min_hz is not None and m.get("calls_per_second", 0.0) < float(min_hz):
            continue
        if has_mutations and not m.get("mutations"):
            continue
        if has_loops and (m.get("loops_count", 0) <= 0):
            continue
        if sig_filter:
            emits_match = any(sig_filter in str(s).lower() for s in m.get("emits", []))
            flow_match = any(sig_filter in str(ef.get("signal", "")).lower() for ef in result.get("event_flows", []) if ef.get("origin_file") == m["file_path"] and ef.get("source_method") == m["name"])
            if not (emits_match or flow_match):
                continue
        filtered.append(m)

    filtered.sort(key=lambda x: (x.get("calls_per_second", 0), x.get("cycles_per_call", 0)), reverse=True)

    if limit is not None and limit > 0:
        filtered = filtered[:limit]

    FIELD_ALIASES = {
        "hz": "calls_per_second",
        "frequency": "calls_per_second",
        "rate": "calls_per_second",
        "cycles": "cycles_per_call",
        "takts": "cycles_per_call",
        "time": "time_per_call_ms",
        "time_ms": "time_per_call_ms",
        "total_ms": "total_ms_per_second",
        "alloc": "alloc_kb_per_call",
        "alloc_kb": "alloc_kb_per_call",
        "muts": "mutations",
        "mutation": "mutations",
        "calls": "calls",
        "callees": "calls",
        "call_steps": "call_steps",
        "callers": "callers",
        "doc": "doc",
        "docstring": "doc",
        "summary": "summary_ru",
        "args": "params",
        "parameters": "params",
        "returns": "return_type"
    }

    results = []
    for m in filtered:
        if not requested_fields or "all" in requested_fields:
            item = dict(m)
        else:
            item = {}
            if len(requested_fields) > 1 or requested_fields[0] in ("name", "method"):
                item["name"] = m["name"]
                item["file"] = m["file_path"]

            for req in requested_fields:
                canonical = FIELD_ALIASES.get(req, req)
                if canonical in m:
                    item[canonical] = m[canonical]
                elif req in ("flow", "event_flow"):
                    flows = [ef for ef in result.get("event_flows", []) if ef.get("origin_file") == m["file_path"] and ef.get("source_method") == m["name"]]
                    item["event_flow"] = flows
                elif req in ("timeline", "waterfall"):
                    tls = [tl["steps"] for tl in chron.get("timelines", []) if tl.get("file") == m["file_path"] and tl.get("method") == m["name"]]
                    item["waterfall"] = tls[0] if tls else []
                elif req == "skeleton":
                    item["signature"] = f"{m['name']}({m.get('params', '')}) -> {m.get('return_type', 'void')}"

        results.append(item)

    if output_format == "json":
        out_dict = {
            "status": "ok",
            "query": {"file": file, "func": func, "fields": requested_fields, "signal": signal, "limit": limit},
            "total_matches": len(results),
            "results": results
        }
        res_str = json.dumps(out_dict, ensure_ascii=False, indent=2)
    elif output_format == "compact":
        lines = []
        for item in results:
            name = item.get("name", func or "func")
            f = item.get("file", file or "")
            parts = [f"[{f}::{name}]"]
            for k, v in item.items():
                if k in ("name", "file"):
                    continue
                if isinstance(v, list):
                    v_str = ", ".join(str(x) for x in v)
                    parts.append(f"{k}: [{v_str}]")
                else:
                    parts.append(f"{k}: {v}")
            lines.append(" | ".join(parts))
        res_str = "\n".join(lines)
    else:
        lines = [
            "# РЕЗУЛЬТАТ ВЫБОРОЧНОГО ЗАПРОСА (QUERY PROJECTION)",
            f"> Запрос: func='{func or '*'}' file='{file or '*'}' fields='{','.join(requested_fields) if requested_fields else 'all'}' | Найдено: {len(results)}",
            "=" * 60
        ]
        for item in results:
            lines.append(f"\n### `{item.get('name', 'method')}` ({item.get('file', '')})")
            for k, v in item.items():
                if k in ("name", "file"):
                    continue
                if isinstance(v, list):
                    if not v:
                        lines.append(f"- **{k}**: (пусто)")
                    elif isinstance(v[0], dict):
                        lines.append(f"- **{k}**:")
                        for d_it in v[:5]:
                            lines.append(f"  • {json.dumps(d_it, ensure_ascii=False)}")
                    else:
                        lines.append(f"- **{k}**: `{', '.join(str(x) for x in v)}`")
                elif isinstance(v, dict):
                    lines.append(f"- **{k}**: {json.dumps(v, ensure_ascii=False)}")
                else:
                    lines.append(f"- **{k}**: **{v}**")
        res_str = "\n".join(lines)

    if budget and budget > 0:
        est_tokens = len(res_str) // 3.8
        if est_tokens > budget:
            target_chars = int(budget * 3.8)
            res_str = res_str[:target_chars] + f"\n\n[... Truncated to fit budget of {budget} tokens. {len(results)} items found.]"

    return res_str

def main():
    parser = argparse.ArgumentParser(description="Local C# / GDScript / Scene dependency graph visualizer")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8765)
    parser.add_argument("--no-browser", action="store_true", help="Не открывать браузер автоматически")

    # CLI / AI Autonomous Access flags
    parser.add_argument("--cli", action="store_true", help="Консольный режим для ИИ (выводит результат в stdout)")
    parser.add_argument("--project", default="", help="Путь к проекту для CLI анализа")
    parser.add_argument("--ignore", default="", help="Список папок или масок для исключения (через запятую)")
    parser.add_argument("--query", action="store_true", help="Точечный запрос / срез кодовой базы с выборочной проекцией полей")
    parser.add_argument("--deep-dive", default="", help="360° рентген функции (синоним --method / --func)")
    parser.add_argument("--deps", default="", help="Вывести список связей конкретного файла (для ИИ)")
    parser.add_argument("--func", default="", help="Найти функцию / метод или получить ее детальный 360° паспорт")
    parser.add_argument("--method", default="", help="Синоним для --func")
    parser.add_argument("--file", default="", help="Фильтр по файлу (путь или имя)")
    parser.add_argument("--fields", default="", help="Выборочные поля (mutations, flow, calls, callers, cycles, hz, doc, skeleton)")
    parser.add_argument("--field", default="", help="Синоним для --fields (одиночное поле)")
    parser.add_argument("--signal", default="", help="Фильтр по имени сигнала / события")
    parser.add_argument("--min-hz", type=float, default=None, help="Фильтр по минимальной частоте вызовов (Hz), например 60")
    parser.add_argument("--has-mutations", action="store_true", help="Фильтр: только методы с мутациями состояния")
    parser.add_argument("--has-loops", action="store_true", help="Фильтр: только методы с циклами")
    parser.add_argument("--limit", type=int, default=None, help="Ограничение количества возвращаемых записей")
    parser.add_argument("--budget", type=int, default=None, help="Бюджет токенов для ИИ (авто-обрезка контекста)")
    parser.add_argument("--format", choices=["json", "markdown", "compact"], default="markdown", help="Формат вывода: markdown, json, compact")
    parser.add_argument("--skeletons", nargs="?", const="all", help="Сгенерировать компактные скелеты кода (экономия 90%% токенов)")
    parser.add_argument("--signals", action="store_true", help="Вывести карту сигналов и событий Godot / C#")
    parser.add_argument("--scenes", action="store_true", help="Вывести дерево нод всех сцен .tscn")
    parser.add_argument("--orphans", action="store_true", help="Ревизия неиспользуемого / мертвого кода")
    parser.add_argument("--hotpaths", action="store_true", help="Вывести список методов горячего пути (60-FPS)")
    parser.add_argument("--benchmark", action="store_true", help="Вывести замеры нагрузки и симуляцию FPS/CPU")
    parser.add_argument("--flows", action="store_true", help="Вывести сквозные цепочки Event-Flow")
    parser.add_argument("--passports", nargs="?", const="all", help="Вывести паспорта поведения функций")
    parser.add_argument("--chronology", action="store_true", help="Вывести частоту вызовов, такты CPU и порядок выполнения")
    parser.add_argument("--all-deps", action="store_true", help="Вывести карту связей всех файлов проекта")
    parser.add_argument("--json", action="store_true", help="Выводить результат в формате JSON")
    args = parser.parse_args()

    # Autonomous CLI handling for AI & Scripts
    if args.cli:
        if hasattr(sys.stdout, "reconfigure"):
            try:
                sys.stdout.reconfigure(encoding="utf-8")
            except Exception:
                pass
        proj_path = args.project or os.getcwd()
        try:
            user_ign = [x.strip() for x in args.ignore.split(",") if x.strip()] if args.ignore else None
            res = analyze(proj_path, user_ignores=user_ign)

            target_func = args.func or args.method or args.deep_dive
            target_fields = args.fields or args.field
            out_fmt = "json" if args.json else (args.format or "markdown")
            has_other_mode = bool(args.deps or args.skeletons or args.signals or args.scenes or args.orphans or args.hotpaths or args.benchmark or args.flows or args.passports or args.chronology or args.all_deps)

            if args.query or (not has_other_mode and (target_fields or args.file or args.signal or args.min_hz is not None or args.has_mutations or args.has_loops or args.limit or args.budget)):
                output = query_codebase(
                    res,
                    file=args.file,
                    func=target_func,
                    fields=target_fields,
                    signal=args.signal,
                    min_hz=args.min_hz,
                    has_mutations=args.has_mutations,
                    has_loops=args.has_loops,
                    limit=args.limit,
                    budget=args.budget,
                    output_format=out_fmt
                )
                print(output)
            elif not has_other_mode and target_func:
                dd = get_function_deep_dive(res, target_func, file_query=args.file)
                if args.json or out_fmt == "json":
                    print(json.dumps(dd, ensure_ascii=False, indent=2))
                else:
                    print(format_function_deep_dive_text(dd))
            elif args.deps:
                if args.json:
                    target = next((f for f in res["files"] if f["path"].lower() == args.deps.lower() or f["path"].lower().endswith(args.deps.lower())), None)
                    in_e = [e for e in res["edges"] if e["target"] == target["id"]] if target else []
                    out_e = [e for e in res["edges"] if e["source"] == target["id"]] if target else []
                    print(json.dumps({"target": target, "in": in_e, "out": out_e}, ensure_ascii=False, indent=2))
                else:
                    print(format_file_deps_text(res, args.deps))
            elif args.skeletons:
                target_arg = args.skeletons
                if target_arg == "all":
                    out_skel = [f"# {f['path']}\n{f.get('skeleton', '')}\n" for f in res["files"]]
                    print("\n" + "="*50 + "\n".join(out_skel))
                else:
                    target = next((f for f in res["files"] if f["path"].lower() == target_arg.lower() or f["path"].lower().endswith(target_arg.lower())), None)
                    print(target.get("skeleton", "Скелет не найден") if target else f"Файл {target_arg} не найден.")
            elif args.signals:
                if args.json:
                    print(json.dumps(res.get("signal_registry", {}), ensure_ascii=False, indent=2))
                else:
                    print(format_signals_text(res))
            elif args.scenes:
                if args.json:
                    print(json.dumps([{"path": f["path"], "tree": f.get("node_tree", [])} for f in res["files"] if f["language"] == "Scene"], ensure_ascii=False, indent=2))
                else:
                    print(format_scenes_text(res))
            elif args.orphans:
                if args.json:
                    print(json.dumps(res.get("orphans_summary", {}), ensure_ascii=False, indent=2))
                else:
                    print(format_orphans_text(res))
            elif args.hotpaths:
                if args.json:
                    print(json.dumps(res.get("hot_paths", []), ensure_ascii=False, indent=2))
                else:
                    print(format_hotpaths_text(res))
            elif args.benchmark:
                if args.json:
                    print(json.dumps(res.get("profiling", {}), ensure_ascii=False, indent=2))
                else:
                    print(format_benchmark_text(res))
            elif args.flows:
                if args.json:
                    print(json.dumps(res.get("event_flows", []), ensure_ascii=False, indent=2))
                else:
                    print(format_event_flows_text(res))
            elif args.passports:
                if args.json:
                    print(json.dumps([{"path": f["path"], "functions": f.get("functions_detail", [])} for f in res["files"]], ensure_ascii=False, indent=2))
                else:
                    print(format_passports_text(res, args.passports))
            elif args.chronology:
                if args.json:
                    print(json.dumps(res.get("chronology", {}), ensure_ascii=False, indent=2))
                else:
                    print(format_chronology_text(res))
            elif args.all_deps:
                print(format_all_deps_text(res))
            else:
                if args.json:
                    print(json.dumps(res, ensure_ascii=False, indent=2))
                else:
                    print(f"Проанализировано файлов: {res['stats']['files']}, связей: {res['stats']['edges']}, здоровье: {res['health_score']}%")
        except Exception as e:
            print(f"[CLI Error]: {e}", file=sys.stderr)
            sys.exit(1)
        sys.exit(0)

    # Standard Web UI Mode
    port = args.port
    server = None
    for p in range(port, port + 50):
        try:
            server = ThreadingHTTPServer((args.host, p), Handler)
            port = p
            break
        except OSError:
            continue

    if server is None:
        print(f"\n[!] Ошибка: не удалось занять ни один порт в диапазоне {args.port}..{args.port+50}")
        input("Нажмите Enter для выхода...")
        return

    url = f"http://{args.host}:{port}"
    print("=" * 64)
    print("    Dependency Graph Visualizer (C# + GDScript + Godot)")
    print("=" * 64)
    print(f"  [+] Сервер запущен: {url}")
    print(f"  [+] Веб-файлы: {WEB_DIR}")
    print(f"  [+] Режим для ИИ: доступен через флаг --cli")
    if not args.no_browser:
        print("  [+] Открываю браузер...")
        threading.Thread(target=open_browser, args=(url,), daemon=True).start()
    print()
    print("  Для остановки закройте это окно или нажмите Ctrl+C.")
    print("=" * 64)
    print()

    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\nОстановка сервера...")
    except Exception as e:
        print(f"\nОшибка во время работы сервера: {e}")
        input("Нажмите Enter для выхода...")
    finally:
        server.server_close()

if __name__ == "__main__":
    main()
