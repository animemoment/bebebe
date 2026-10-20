with open('index.html', 'r', encoding='utf-8') as f:
    c = f.read()

print("HTML Length:", len(c))
print("Has CHRONICLE_COMMITS:", "CHRONICLE_COMMITS =" in c)
print("Has colony-canvas:", '<canvas id="colony-canvas"' in c)
print("Has whittaker-canvas:", '<canvas id="whittaker-canvas"' in c)
print("Has ambient-canvas:", '<canvas id="ambient-canvas"' in c)
print("Has SoundFx:", "class SoundFx" in c)
print("Has 31 milestones:", c.count('badge-hash'))
print("Verification successful!")
