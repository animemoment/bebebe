with open(r'd:\bebebe-main (1)\csharp-course\index.html', 'r', encoding='utf-8') as f:
    text = f.read()

assert 'class=class=' not in text, "Corrupted tags found!"
assert 'highlightCSharp' in text
assert 'highlightPython' in text
assert 'white-space: pre-wrap;' in text
assert 'word-break: break-word;' in text

print("Verification passed! No corrupted HTML tags, responsive pre-wrap styles are present. Total size:", len(text))
