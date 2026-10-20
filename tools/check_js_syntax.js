const fs = require('fs');

const html = fs.readFileSync('index.html', 'utf8');
const scriptMatches = html.match(/<script>([\s\S]*?)<\/script>/);

if (!scriptMatches) {
    console.error("No script tag found!");
    process.exit(1);
}

const jsCode = scriptMatches[1];
console.log("Found JavaScript code, length:", jsCode.length);

try {
    // Check syntax with new Function
    new Function(jsCode);
    console.log("JavaScript syntax check: PERFECT! No syntax errors.");
} catch (err) {
    console.error("JavaScript syntax error:", err);
    process.exit(1);
}
