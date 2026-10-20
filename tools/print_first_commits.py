import json

with open('tools/history_analysis.json', 'r', encoding='utf-8') as f:
    data = json.load(f)

for i in range(min(18, len(data['commits']))):
    c = data['commits'][i]
    print(f"[{i+1:02d}] {c['date']} | {c['short_hash']} | {c['author']}")
    print(f"     Subject: {c['subject']}")
    if c['body'] and c['body'] != c['subject']:
        print(f"     Body: {c['body'][:200]}...")
    print(f"     Branches: {', '.join(c['branches'])}")
    print(f"     Files changed: {len(c['files_changed'])}, Summary: {c['stat_summary']}")
    print("-" * 80)
