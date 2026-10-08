const { test } = require('node:test');
const assert = require('node:assert/strict');
const { buildForest } = require('../../src/DocxHeaderExtractor.Web/wwwroot/pipeline-v2.js');

const heading = (id, level = null, status = 'no-validated-parent') => ({ elementId: id,
  text: 'Duplicate <script>not executable</script>', sourceId: 'source-' + id,
  sourceOrdinal: 7, level, parentStatus: status });
test('validated relations alone create nested tree; duplicate text/ordinal/level do not', () => {
  const model = { headings: [heading('a', 1), heading('b', 2), heading('c', 3), heading('d', 2)],
    relations: [{ fromId: 'a', toId: 'b', type: 'ParentChild' }, { fromId: 'b', toId: 'c', type: 'ParentChild' }] };
  const forest = buildForest(model);
  assert.deepEqual(forest.roots.map(n => n.heading.elementId), ['a', 'd']);
  assert.equal(forest.nodes.get('a').children[0].heading.elementId, 'b');
  assert.equal(forest.nodes.get('b').children[0].heading.elementId, 'c');
  assert.equal(forest.nodes.size, 4);
});
test('filtered/missing parents and unresolved nodes remain separate', () => {
  const model = { headings: [heading('b', 2, 'parent-not-emitted'), heading('u', null, 'unresolved'), heading('r', 1)],
    relations: [{ fromId: 'hidden', toId: 'b', type: 'ParentChild' }] };
  const forest = buildForest(model);
  assert.deepEqual(forest.unresolved.map(n => n.heading.elementId), ['b', 'u']);
  assert.deepEqual(forest.roots.map(n => n.heading.elementId), ['r']);
});
test('unknown levels and no relationships never imply a parent', () => {
  const forest = buildForest({ headings: [heading('x', 1), heading('y', 2), heading('z', null)], relations: [] });
  assert.equal(forest.roots.length, 3);
  assert.ok([...forest.nodes.values()].every(n => n.children.length === 0));
});
test('empty graph is empty, not a synthetic placeholder heading', () => {
  const forest = buildForest({ headings: [], relations: [] });
  assert.equal(forest.nodes.size, 0);
  assert.equal(forest.roots.length, 0);
});
test('main Web script initializes escaping before the first progress render and is syntactically valid', () => {
  const fs = require('node:fs'), vm = require('node:vm');
  const html = fs.readFileSync(require.resolve('../../src/DocxHeaderExtractor.Web/wwwroot/index.html'), 'utf8');
  const script = html.match(/<script>\s*([\s\S]*?)<\/script>/)[1];
  assert.ok(script.indexOf('const esc =') < script.indexOf('\nrenderHarnessStages();'));
  assert.ok(!script.includes('((h.level ?? 1) - 1) * 22')); // even the flat fallback must not suggest parentage
  assert.doesNotThrow(() => new vm.Script(script));
});
test('renderer uses inert text DOM sinks and responsive stylesheet', () => {
  const fs = require('node:fs');
  const js = fs.readFileSync(require.resolve('../../src/DocxHeaderExtractor.Web/wwwroot/pipeline-v2.js'), 'utf8');
  assert.ok(!js.includes('innerHTML'));
  assert.ok(!js.includes('eval('));
  assert.ok(js.includes('e.textContent = String(text)'));
  assert.ok(js.indexOf("document.getElementById('v2Diagnostics').replaceChildren();") < js.indexOf("model.availability !== 'same-execution'"));
  const css = fs.readFileSync(require.resolve('../../src/DocxHeaderExtractor.Web/wwwroot/pipeline-v2.css'), 'utf8');
  assert.ok(css.includes('@media(max-width:720px)'));
});
