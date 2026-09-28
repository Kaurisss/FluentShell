// Run: node --test Tests/Terminal/theme.test.cjs
// Executes the shipped page bridge; the WinUI smoke harness covers host propagation.
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const { test } = require('node:test');

const html = readFileSync(join(__dirname, '../../Assets/Terminal/index.html'), 'utf8');
const script = html.match(/<script>([\s\S]*?)<\/script>/)[1];
function page() {
    let terminal, receive;
    const document = {
        body: { style: {} }, documentElement: { style: {} }, getElementById: () => ({})
    };
    const context = {
        document,
        Terminal: class {
            constructor(options) { this.options = options; this.output = ''; terminal = this; }
            loadAddon() {} open() {} onData() {} onResize() {} focus() {}
            write(data) { this.output += data; }
            clear() { this.output = ''; }
        },
        FitAddon: { FitAddon: class { fit() {} } },
        ResizeObserver: class { observe() {} }, requestAnimationFrame: callback => callback(),
        window: { chrome: { webview: {
            postMessage() {}, addEventListener: (_, callback) => { receive = callback; }
        } } }
    };
    vm.runInNewContext(script, context);
    return { terminal, document, send: data => receive({ data }) };
}
function luminance(hex) {
    const rgb = hex.slice(1).match(/../g).map(value => parseInt(value, 16) / 255)
        .map(value => value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4);
    return rgb[0] * 0.2126 + rgb[1] * 0.7152 + rgb[2] * 0.0722;
}
function contrast(a, b) {
    const values = [luminance(a), luminance(b)].sort((x, y) => y - x);
    return (values[0] + 0.05) / (values[1] + 0.05);
}
test('light terminal text and all ANSI colors have at least 4.5:1 contrast', () => {
    const { terminal, send } = page();
    send({ type: 'theme', value: 'light' });
    const theme = terminal.options.theme;
    for (const key of ['foreground', 'cursor', 'black', 'red', 'green', 'yellow', 'blue',
        'magenta', 'cyan', 'white', 'brightBlack', 'brightRed', 'brightGreen', 'brightYellow',
        'brightBlue', 'brightMagenta', 'brightCyan', 'brightWhite']) {
        const ratio = contrast(theme[key], theme.background);
        assert.ok(ratio >= 4.5, `${key}: ${ratio.toFixed(2)}:1`);
    }
});
test('light/dark/light updates terminal and page without clearing output', () => {
    const { terminal, document, send } = page();
    send({ type: 'write', data: 'existing session output' });
    for (const value of ['light', 'dark', 'light']) {
        send(JSON.stringify({ type: 'theme', value }));
        assert.equal(document.body.style.backgroundColor, terminal.options.theme.background);
        assert.equal(document.documentElement.style.colorScheme, value);
        assert.equal(terminal.options.theme.background, value === 'light' ? '#FEFEFE' : '#363636');
        assert.equal(terminal.output, 'existing session output');
    }
});
