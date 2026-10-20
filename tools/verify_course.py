with open(r'd:\bebebe-main (1)\csharp-course\index.html', 'r', encoding='utf-8') as f:
    text = f.read()

assert '<!DOCTYPE html>' in text
assert 'const MODULES =' in text
assert 'const GLOSSARY =' in text
assert 'let bobSim =' in text
modules_count = text.count('"id": "module-')
print(f"Verification successful: {len(text)} bytes, {modules_count} modules present.")
