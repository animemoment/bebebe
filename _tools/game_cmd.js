// game_cmd.js - one-shot TCP client for the in-game McpInteractionServer (godot-bridge).
//
// Why this exists: godot-bridge's built-in bridge spawns `node -e ...` with a
// PIPED stdout ({ collect: ... }). Under the DSH Windows sandbox a spawned
// process cannot open that named pipe, so the spawn dies with EPERM and the
// tool reports "bridge spawn failed: spawn EPERM". This script does the same
// TCP round-trip but writes the response to a FILE instead of stdout, so it
// works without any pipe.
//
// Usage:
//   node game_cmd.js <command> <paramsJson> [port] [outFile] [timeoutMs]
//
// Example:
//   node game_cmd.js eval '{"code":"1+1"}' 9090 out.json 15000

const fs = require('fs')
const net = require('net')

const NL = '\n'
const command = process.argv[2] || ''
const paramsJson = process.argv[3] || '{}'
const port = parseInt(process.argv[4] || '9090', 10)
const outFile = process.argv[5] || 'game_cmd_out.json'
const timeoutMs = parseInt(process.argv[6] || '20000', 10)

// argv[3] may be inline JSON, or "@path" to read it from a file.
//   @path/to/params.json -> JSON params, read from file
//   @path/to/code.gd     -> raw GDScript, sent as {"code": <file contents>}
// The file form is required for anything containing quotes/newlines (e.g. eval
// code): PowerShell mangles double quotes when passing args to a native exe.
// Reminder: _cmd_eval wraps the code in a function, so use `return` to send a
// value back.
let params = {}
let paramsRaw = paramsJson
if (paramsJson.charAt(0) === '@') {
  const p = paramsJson.slice(1)
  try {
    paramsRaw = fs.readFileSync(p, 'utf8')
    if (/\.gd$/i.test(p)) {
      params = { code: paramsRaw }
      paramsRaw = null
    }
  } catch (e) {
    fs.writeFileSync(outFile, JSON.stringify({ error: 'cannot read params file: ' + e.message }), 'utf8')
    process.exit(0)
  }
}
if (paramsRaw !== null) {
  try {
    params = JSON.parse(paramsRaw)
  } catch (e) {
    fs.writeFileSync(outFile, JSON.stringify({ error: 'bad params json: ' + e.message, raw: paramsRaw.slice(0, 500) }), 'utf8')
    process.exit(0)
  }
}

let buf = ''
let done = false

function finish(obj) {
  if (done) return
  done = true
  try {
    fs.writeFileSync(outFile, JSON.stringify(obj), 'utf8')
  } catch (e) {
    // last resort: nothing else we can do without a pipe
  }
  process.exit(0)
}

const sock = net.connect(port, '127.0.0.1')
const timer = setTimeout(() => finish({ error: 'timeout after ' + timeoutMs + 'ms' }), timeoutMs)

sock.on('connect', () => {
  sock.write(JSON.stringify({ command: command, params: params, id: 1 }) + NL)
})

sock.on('data', (d) => {
  buf += d.toString()
  for (;;) {
    const i = buf.indexOf(NL)
    if (i < 0) break
    const line = buf.slice(0, i).trim()
    buf = buf.slice(i + 1)
    if (!line) continue
    clearTimeout(timer)
    let parsed
    try {
      parsed = JSON.parse(line)
    } catch (e) {
      parsed = { error: 'unparsable response', raw: line.slice(0, 4000) }
    }
    try { sock.destroy() } catch (e) {}
    finish(parsed)
    return
  }
})

sock.on('error', (e) => finish({ error: 'tcp: ' + e.message }))
sock.on('close', () => finish({ error: 'connection closed without response' }))