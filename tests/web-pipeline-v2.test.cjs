const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

class FakeElement {
  constructor(tag = 'div') {
    this.tagName = tag;
    this.children = [];
    this.dataset = {};
    this.attributes = {};
    this.classList = { add() {}, remove() {} };
  }
  append(...items) { this.children.push(...items); }
  replaceChildren(...items) { this.children = [...items]; }
  setAttribute(key, value) { this.attributes[key] = String(value); }
  addEventListener() {}
  querySelectorAll() { return []; }
}

test('a failed execution clears placement diagnostics from the prior successful render', () => {
  const ids = ['pipelineV2', 'v2Tree', 'v2Inspector', 'v2Summary', 'v2Diagnostics',
    'v2PlacementDiagnostics', 'treePanel', 'statsPanel', 'v2Stages'];
  const elements = Object.fromEntries(ids.map(id => [id, new FakeElement()]));
  const document = {
    getElementById: id => elements[id] ?? null,
    createElement: tag => new FakeElement(tag),
  };
  const window = {};
  const source = fs.readFileSync(path.join(__dirname, '../src/DocxHeaderExtractor.Web/wwwroot/pipeline-v2.js'), 'utf8');
  vm.runInNewContext(source, { window, document });

  window.WebPipelineV2.render({
    availability: 'same-execution', executionId: 'run-1', outcome: 'Partial', sourceKind: 'scan',
    headings: [], relations: [], roots: [], unresolved: [],
    summary: { accepted: 0, unknownLevel: 0, requiresReview: 0 },
    stages: [], checkpoints: [], provenance: {},
    audit: { placementExecution: {
      decisions: [{ alias: 'P021-L012', proposedParentAlias: 'P021-L010',
        verificationStatus: 'MISSING_ANCESTOR', resolutionBasis: 'model-missing-ancestor',
        blockingAliases: ['P021-L010'], evidenceRefs: ['P021-L012', 'P021-L010'] }],
      sourceTrace: [], failures: [],
    }, headingAliasStates: {} },
  });
  assert.ok(elements.v2PlacementDiagnostics.children.length > 0);

  window.WebPipelineV2.showFailure('new execution failed');
  assert.deepEqual(elements.v2PlacementDiagnostics.children, []);
});
