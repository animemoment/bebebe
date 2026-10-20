import re

# Read index.html and extract the MODULES json
with open(r'd:\bebebe-main (1)\csharp-course\index.html', 'r', encoding='utf-8') as f:
    text = f.read()

# Verify that MODULES is valid JSON
match = re.search(r'const MODULES = (\[[\s\S]*?\]);\s*// ═══', text)
assert match, "Could not extract MODULES"
modules_json = match.group(1)

import json
data = json.loads(modules_json)
print("Modules loaded:", len(data))
first_lesson = data[0]['lessons'][0]
print("First lesson title:", first_lesson['title'])
print("Sections count:", len(first_lesson['sections']))
py_vs_cs = [s for s in first_lesson['sections'] if s['type'] == 'py_vs_cs'][0]
print("py_vs_cs py:\n", py_vs_cs['py'])
print("py_vs_cs cs:\n", py_vs_cs['cs'])
