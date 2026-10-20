with open('PLAN.md', 'r', encoding='utf-8', errors='replace') as f:
    lines = f.readlines()

headers = []
for idx, line in enumerate(lines):
    if line.startswith('#'):
        headers.append(f"L{idx+1}: {line.strip()}")

with open('tools/plan_headers.txt', 'w', encoding='utf-8') as out:
    out.write('\n'.join(headers))

print(f"Total headers: {len(headers)}. Written to tools/plan_headers.txt")
