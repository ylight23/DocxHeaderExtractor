// Run: node --test tests/web-source-detection.test.cjs
// Exercise the real upload/route functions with controlled HTTP responses, including an out-of-order response.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const html = fs.readFileSync(path.join(__dirname, '../src/DocxHeaderExtractor.Web/wwwroot/index.html'), 'utf8');
const selection = html.slice(html.indexOf('async function setFile(f)'), html.indexOf('// ---------- mặc định, backend, mô hình ----------'));
const backend = html.slice(html.indexOf('function syncBackend()'), html.indexOf('function renderCfgNote()'));
const scan = { format: 'docx', route: 'scan', requiresOcr: true, supported: true, requiresOpenRouter: true, canWriteback: false, message: 'Scan → OCR/VLM' };
const native = { format: 'docx', route: 'docx', requiresOcr: false, supported: true, requiresOpenRouter: false, canWriteback: true, message: 'Word text' };

function screen() {
  const elements = new Map();
  const options = Object.fromEntries(['local', 'openrouter', 'lmstudio'].map(v => [v, { disabled: false }]));
  function $(id) {
    if (!elements.has(id)) elements.set(id, { value: id === 'backend' ? 'local' : '', checked: false, disabled: false,
      textContent: '', classList: { add() {}, toggle() {} },
      querySelector(selector) { return options[selector.match(/value="([^"]+)"/)[1]]; } });
    return elements.get(id);
  }
  const pending = [];
  const context = vm.createContext({ $, AbortController, FormData, File, console,
    fetch(url, request) { assert.equal(url, '/api/inspect'); return new Promise(resolve => pending.push({ request, resolve })); },
    renderPrompt() {}, renderCfgNote() {}, chip() {}, showError(message) { $('error').textContent = message; } });
  vm.runInContext('let picked=null, sourceInspection=null, inspectController=null, inspecting=false, runController=null; let defaults={openRouterAvailable:true}, promptRoute="docx";', context);
  vm.runInContext(selection + '\n' + backend, context);
  return { $, options, pending, context,
    choose(name) { context.file = new File(['content'], name); return vm.runInContext('setFile(file)', context); },
    reply(index, data, ok = true) { pending[index].resolve({ ok, async json() { return data; } }); },
    read(code) { return vm.runInContext(code, context); } };
}

test('scan inspection selects scan prompt and remote route; no provider is called during inspection', async () => {
  const s = screen();
  const done = s.choose('actually-a-scan.pdf');
  assert.equal(s.$('run').disabled, true);
  s.reply(0, scan);
  await done;
  assert.equal(s.read('promptRoute'), 'scan');
  assert.equal(s.$('backend').value, 'openrouter');
  assert.equal(s.options.local.disabled, true);
  assert.equal(s.$('writeback').disabled, true);
  assert.equal(s.$('run').disabled, false);
});

test('an older inspection cannot overwrite the newly selected native file', async () => {
  const s = screen();
  const first = s.choose('old.docx');
  const second = s.choose('new.docx');
  assert.equal(s.pending[0].request.signal.aborted, true);
  s.reply(1, native);
  await second;
  s.reply(0, scan); // even if the HTTP mock ignores abort
  await first;
  assert.equal(s.read('promptRoute'), 'docx');
  assert.equal(s.$('sourceNote').textContent, native.message);
  assert.equal(s.$('writeback').disabled, false);
  assert.equal(s.$('run').disabled, false);
});

test('missing key blocks model use but preserves the explicit offline OCR option', async () => {
  const s = screen();
  s.read('defaults.openRouterAvailable=false');
  const done = s.choose('scan.docx');
  s.reply(0, scan);
  await done;
  assert.equal(s.$('run').disabled, true);
  assert.match(s.$('sourceNote').textContent, /OPENROUTER_API_KEY/);
  s.$('noLlm').checked = true;
  s.read('syncBackend()');
  assert.equal(s.$('run').disabled, false);
  assert.match(s.$('sourceNote').textContent, /chỉ chạy OCR/);
});

test('PDF scans use the scan prompt and enable extraction; failed inspection cannot start it', async () => {
  const s = screen();
  let done = s.choose('scan.pdf');
  s.reply(0, { ...scan, format: 'pdf', route: 'scan', supported: true });
  await done;
  assert.equal(s.$('run').disabled, false);
  assert.equal(s.read('promptRoute'), 'scan');
  done = s.choose('broken.docx');
  s.reply(1, { message: 'Invalid document' }, false);
  await done;
  assert.equal(s.$('run').disabled, true);
  assert.equal(s.$('error').textContent, 'Invalid document');
});
