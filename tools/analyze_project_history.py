import subprocess
import json
import os
import re

def run_cmd(args):
    result = subprocess.run(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding='utf-8', errors='replace')
    return result.stdout

def get_git_branches():
    raw = run_cmd(["git", "branch", "-a"])
    branches = [b.strip() for b in raw.splitlines() if b.strip()]
    return branches

def get_all_commits():
    hashes = run_cmd(["git", "log", "--all", "--format=%H"]).strip().splitlines()
    # deduplicate
    hashes = list(dict.fromkeys([h.strip() for h in hashes if h.strip()]))
    commits = []
    print(f"Processing {len(hashes)} unique commits...")
    for h in hashes:
        format_str = "%H%x1f%h%x1f%an%x1f%ae%x1f%ad%x1f%s%x1f%B"
        info = run_cmd(["git", "show", "-s", f"--format={format_str}", "--date=iso-strict", h]).strip()
        parts = info.split('\x1f')
        if len(parts) >= 7:
            full_hash, short_hash, author_name, author_email, date_iso, subject, body = parts[:7]
            stat = run_cmd(["git", "show", "--stat", "--oneline", h])
            stat_lines = [l for l in stat.splitlines()[1:] if l.strip()]
            stat_summary = stat_lines[-1] if stat_lines else ""
            
            # Get list of files changed
            numstat = run_cmd(["git", "show", "--numstat", "--format=", h]).strip().splitlines()
            files_changed = []
            for ns in numstat:
                ns_parts = ns.split('\t')
                if len(ns_parts) == 3:
                    files_changed.append({
                        "additions": ns_parts[0],
                        "deletions": ns_parts[1],
                        "file": ns_parts[2]
                    })
            
            # Find which branch contains this commit
            branches_containing = run_cmd(["git", "branch", "-a", "--contains", h]).strip().splitlines()
            cleaned_branches = [b.replace('*', '').strip() for b in branches_containing if b.strip()]

            commits.append({
                "hash": full_hash,
                "short_hash": short_hash,
                "author": author_name,
                "email": author_email,
                "date": date_iso,
                "subject": subject,
                "body": body.strip(),
                "stat_summary": stat_summary,
                "files_changed": files_changed,
                "branches": cleaned_branches
            })
    # Sort chronologically
    commits.sort(key=lambda x: x['date'])
    return commits

def analyze_plan_md():
    if not os.path.exists("PLAN.md"):
        return None
    with open("PLAN.md", "r", encoding="utf-8", errors="replace") as f:
        content = f.read()
    
    headers = re.findall(r'^(#{1,4}\s+.*)', content, flags=re.MULTILINE)
    line_count = len(content.splitlines())
    char_count = len(content)
    
    return {
        "line_count": line_count,
        "char_count": char_count,
        "headers": headers,
        "sample": content[:2000]
    }

def analyze_codebase():
    stats = {}
    extensions = {'.cs': 'C#', '.gd': 'GDScript', '.tscn': 'Scene', '.gdshader': 'Shader', '.py': 'Python', '.html': 'HTML', '.md': 'Markdown', '.png': 'Image', '.json': 'JSON'}
    file_list = []
    for root, dirs, files in os.walk("."):
        if ".git" in root or ".godot" in root:
            continue
        for file in files:
            ext = os.path.splitext(file)[1]
            fpath = os.path.join(root, file).replace('\\', '/')
            if ext in extensions:
                lang = extensions[ext]
                lines = 0
                size = os.path.getsize(fpath)
                if ext not in ['.png']:
                    try:
                        with open(fpath, 'r', encoding='utf-8', errors='replace') as f:
                            lines = len(f.readlines())
                    except Exception:
                        pass
                if lang not in stats:
                    stats[lang] = {"files": 0, "lines": 0, "bytes": 0}
                stats[lang]["files"] += 1
                stats[lang]["lines"] += lines
                stats[lang]["bytes"] += size
                
                if ext in ['.cs', '.gdshader', '.tscn', '.py', '.html']:
                    file_list.append({
                        "path": fpath,
                        "type": lang,
                        "lines": lines,
                        "size": size
                    })
    return stats, file_list

def analyze_git_status():
    status = run_cmd(["git", "status", "--short"])
    lines = status.strip().splitlines()
    modified = []
    untracked = []
    deleted = []
    staged = []
    for line in lines:
        code = line[:2]
        f = line[3:]
        if 'M' in code:
            modified.append(f)
        elif '?' in code:
            untracked.append(f)
        elif 'D' in code:
            deleted.append(f)
        elif code[0] in ['A', 'M', 'D', 'R']:
            staged.append(f)
            
    # Also get uncommitted diff summary
    diff_stat = run_cmd(["git", "diff", "--stat"])
    return {
        "modified_count": len(modified),
        "untracked_count": len(untracked),
        "deleted_count": len(deleted),
        "staged_count": len(staged),
        "modified_files": modified,
        "untracked_files": untracked,
        "diff_stat": diff_stat
    }

def main():
    print("Collecting branches...")
    branches = get_git_branches()
    print("Collecting commits...")
    commits = get_all_commits()
    print("Analyzing PLAN.md...")
    plan_info = analyze_plan_md()
    print("Analyzing codebase stats...")
    code_stats, file_list = analyze_codebase()
    print("Analyzing git status...")
    status_info = analyze_git_status()
    
    data = {
        "branches": branches,
        "commits": commits,
        "total_commits": len(commits),
        "plan_md": plan_info,
        "code_stats": code_stats,
        "files": file_list,
        "git_status": status_info
    }
    
    with open("tools/history_analysis.json", "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
    print("Saved to tools/history_analysis.json")
    print(f"Total unique commits: {len(commits)}")
    print("Code stats:", code_stats)

if __name__ == "__main__":
    main()
