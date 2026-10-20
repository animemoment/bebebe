import json

with open('tools/history_analysis.json', 'r', encoding='utf-8') as f:
    data = json.load(f)

print(f"Total commits: {len(data['commits'])}\n")
for i, c in enumerate(data['commits'], 1):
    print(f"[{i:02d}] {c['date']} | {c['short_hash']} | {c['author']}")
    print(f"     Subject: {c['subject']}")
    if c['body'] and c['body'] != c['subject']:
        print(f"     Body: {c['body'][:200]}...")
    print(f"     Branches: {', '.join(c['branches'])}")
    print(f"     Files changed: {len(c['files_changed'])}, Summary: {c['stat_summary']}")
    print("-" * 80)
