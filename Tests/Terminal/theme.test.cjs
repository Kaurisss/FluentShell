// Run: node --test Tests/Terminal/theme.test.cjs
// Executes the shipped page bridge; the WinUI smoke harness covers host propagation.
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const { test } = require('node:test');

const html = readFileSync(join(__dirname, '../../Assets/Terminal/index.html'), 'utf8');
const script = html.match(/<script>([\s\S]*?)<\/script>/)[1];
function page(search = '') {
    let terminal, receive;
    const document = {
        body: { style: {} }, documentElement: { style: {} }, getElementById: () => ({ addEventListener() {}, focus() {} })
    };
    const context = {
        document,
        URLSearchParams,
        Terminal: class {
            constructor(options) { this.options = options; this.output = ''; terminal = this; }
            loadAddon() {} open() {} onData() {} onResize() {} focus() {}
            attachCustomKeyEventHandler() {} onSelectionChange() {} paste(data) { this.write(data); }
            write(data) { this.output += data; }
            clear() { this.output = ''; }
        },
        FitAddon: { FitAddon: class { fit() {} } },
        SearchAddon: { SearchAddon: class { findNext() {} } },
        ResizeObserver: class { observe() {} }, requestAnimationFrame: callback => callback(),
        window: { location: { search }, chrome: { webview: {
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

test('custom colors override only valid keys and reset without modifying defaults or output', () => {
    const { terminal, document, send } = page();
    send({ type: 'write', data: 'preserved' });
    send({ type: 'theme', value: 'dark', colors: { background: '#112233', red: '#abcdef', blue: 'url(test)', unknown: '#123456' } });
    assert.equal(terminal.options.theme.red, '#abcdef');
    assert.equal(document.body.style.backgroundColor, '#112233');
    assert.equal(terminal.options.theme.blue, '#79C0FF');
    assert.equal(terminal.options.theme.unknown, undefined);
    send({ type: 'theme', value: 'light', colors: { red: '#987654' } });
    assert.equal(terminal.options.theme.red, '#987654');
    send({ type: 'theme', value: 'dark', colors: {} });
    assert.equal(terminal.options.theme.red, '#FF7B72');
    assert.equal(document.body.style.backgroundColor, '#363636');
    assert.equal(terminal.output, 'preserved');
});

test('terminal preferences update font cursor and scrollback without clearing output', () => {
    const { terminal, send } = page();
    send({ type: 'write', data: 'keep output' });
    send({ type: 'preferences', value: { fontFamily: 'Consolas', cursorStyle: 'block', cursorBlink: false, scrollback: 4000, shortcuts: {} } });
    assert.equal(terminal.options.fontFamily, 'Consolas, monospace');
    assert.equal(terminal.options.cursorStyle, 'block');
    assert.equal(terminal.options.cursorBlink, false);
    assert.equal(terminal.options.scrollback, 4000);
    assert.equal(terminal.output, 'keep output');
    send({ type: 'paste', data: ' pasted' });
    assert.equal(terminal.output, 'keep output pasted');
});
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

test('both themes define all 16 distinct ANSI colors', () => {
    const { terminal, send } = page();
    const keys = ['black', 'red', 'green', 'yellow', 'blue', 'magenta', 'cyan', 'white',
        'brightBlack', 'brightRed', 'brightGreen', 'brightYellow', 'brightBlue',
        'brightMagenta', 'brightCyan', 'brightWhite'];
    for (const value of ['light', 'dark', 'light']) {
        send({ type: 'theme', value });
        const colors = keys.map(key => terminal.options.theme[key]);
        for (const color of colors) assert.match(color, /^#[0-9a-f]{6}$/i);
        assert.equal(new Set(colors).size, 16);
        // ANSI black is intentionally dark; the remaining dark-theme colors
        // must remain readable against the terminal background.
        if (value === 'dark') {
            for (const key of keys.slice(1)) {
                assert.ok(contrast(terminal.options.theme[key], terminal.options.theme.background) >= 4.5, key);
            }
        }
    }
});

test('ANSI foreground, background, indexed and truecolor escapes reach xterm intact', () => {
    const { terminal, send } = page();
    const output = '\x1b[31mred\x1b[0m \x1b[92mgreen\x1b[0m \x1b[44mbackground\x1b[0m '
        + '\x1b[38;5;208mindexed\x1b[0m \x1b[38;2;255;128;0mtruecolor\x1b[0m';
    send({ type: 'write', data: output });
    assert.equal(terminal.output, output);
});

test('backdrop opt-in keeps page and xterm transparent across themes and color overrides', () => {
    const { terminal, document, send } = page('?backdrop=1');
    assert.equal(terminal.options.allowTransparency, true);
    assert.equal(document.body.className, 'terminal-backdrop');
    assert.equal(terminal.options.theme.background, '#00000000');
    assert.equal(document.body.style.backgroundColor, 'transparent');
    send({ type: 'write', data: '\x1b[44mANSI cell background\x1b[0m' });
    for (const value of ['dark', 'light', 'dark']) {
        send({ type: 'theme', value, colors: { background: '#112233', red: '#abcdef' } });
        assert.equal(terminal.options.theme.background, '#00000000');
        assert.equal(document.body.style.backgroundColor, '#00000000');
        assert.equal(terminal.options.theme.red, '#abcdef');
        assert.equal(document.documentElement.style.colorScheme, value);
        assert.equal(terminal.output, '\x1b[44mANSI cell background\x1b[0m');
    }
});

test('normal sessions and unrecognized backdrop values retain opaque backgrounds', () => {
    for (const search of ['', '?backdrop=0', '?backdrop=true']) {
        const { terminal, document, send } = page(search);
        assert.equal(terminal.options.allowTransparency, false);
        assert.equal(document.body.className, '');
        send({ type: 'theme', value: 'dark', colors: { background: '#112233' } });
        assert.equal(terminal.options.theme.background, '#112233');
        assert.equal(document.body.style.backgroundColor, '#112233');
    }
});
