with open(r'd:\bebebe-main (1)\csharp-course\index.html', 'rb') as f:
    raw = f.read()

# Try decoding as utf-8
decoded = raw.decode('utf-8')
print("File successfully decoded as UTF-8! Character length:", len(decoded))
assert 'Как думает компьютер и что такое C#?' in decoded
print("Found exact Russian string 'Как думает компьютер и что такое C#?' in decoded text!")
