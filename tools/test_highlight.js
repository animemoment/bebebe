function highlightCSharp(code) {
    if (!code) return '';
    const escapeHtml = (str) => str.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

    const masterRegex = /(\/\/[^\r\n]*|\/\*[\s\S]*?\*\/)|(\$?"(?:[^"\\]|\\.)*"|@"(?:[^"]|"")*")|(\[\s*\w+\s*\])|(\b(?:public|private|protected|internal|static|sealed|readonly|var|void|new|return|if|else|for|foreach|while|do|class|struct|enum|interface|record|namespace|using|where|true|false|null|this|throw|try|catch|finally|break|continue|default|switch|case|when|in|is|as|override|virtual|abstract|partial|get|set|async|await|ref|out)\b)|(\b(?:int|float|double|bool|string|byte|char|long|short|object|TileType|JobData|ItemId|ItemDefinition|WorkItem|GameSpeed|ToolRequirement|Node|SimulationContext|AgentDataPool|ITool|IJobHandler|TilePos)\b)|(\b\d+(?:\.\d+)?[fFdDmM]?\b)|(\b[a-zA-Z_]\w*(?=\s*\())/g;

    let result = '';
    let lastIndex = 0;
    let match;

    while ((match = masterRegex.exec(code)) !== null) {
        result += escapeHtml(code.slice(lastIndex, match.index));
        lastIndex = masterRegex.lastIndex;

        const [full, comment, stringLit, attribute, keyword, typeName, numberLit, methodCall] = match;

        if (comment) {
            result += `<span class="cm">${escapeHtml(comment)}</span>`;
        } else if (stringLit) {
            result += `<span class="str">${escapeHtml(stringLit)}</span>`;
        } else if (attribute) {
            result += `<span class="attr">${escapeHtml(attribute)}</span>`;
        } else if (keyword) {
            result += `<span class="kw">${escapeHtml(keyword)}</span>`;
        } else if (typeName) {
            result += `<span class="tp">${escapeHtml(typeName)}</span>`;
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

const sampleCode = `// В C# код компилируется в машинный код:
foreach (var colonist in colonists)
{
    colonist.UpdateAi();
}
// .NET 8 компилирует C# заранее:
// Результат: 10 000 колонистов!`;

console.log(highlightCSharp(sampleCode));
