function highlightPython(code) {
    if (!code) return '';
    const escapeHtml = (str) => str.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

    const masterRegex = /(#[^\r\n]*)|(\$?"(?:[^"\\]|\\.)*"|'(?:[^'\\]|\\.)*')|(\b(?:def|for|in|if|else|elif|return|True|False|None|and|or|not|print|while|import|from|class|pass|break|continue)\b)|(\b\d+(?:\.\d+)?\b)|(\b[a-zA-Z_]\w*(?=\s*\())/g;

    let result = '';
    let lastIndex = 0;
    let match;

    while ((match = masterRegex.exec(code)) !== null) {
        result += escapeHtml(code.slice(lastIndex, match.index));
        lastIndex = masterRegex.lastIndex;

        const [full, comment, stringLit, keyword, numberLit, methodCall] = match;

        if (comment) {
            result += `<span class="cm">${escapeHtml(comment)}</span>`;
        } else if (stringLit) {
            result += `<span class="str">${escapeHtml(stringLit)}</span>`;
        } else if (keyword) {
            result += `<span class="kw">${escapeHtml(keyword)}</span>`;
        } else if (numberLit) {
            result += `<span class="num">${escapeHtml(numberLit)}</span>`;
        } else if (methodCall) {
            result += `<span class="fn">${escapeHtml(methodCall)}</span>`;
        } else {
            result += escapeHtml(full);
        }
    }

    result += escapeHtml(code.slice(lastIndex));
    return result;
}

const pySample = `# В Python код:
for colonist in colonists:
    colonist.update_ai()
print("Готово!")`;

console.log(highlightPython(pySample));
