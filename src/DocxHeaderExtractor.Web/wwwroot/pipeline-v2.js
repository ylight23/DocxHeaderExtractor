(function (global) {
  'use strict';
  // Pure projection: relationships alone determine topology. Levels/text/ordinals never do.
  function buildForest(model) {
    const nodes = new Map((model.headings || []).map(h => [h.elementId, { heading: h, children: [] }]));
    const parents = new Map();
    for (const edge of model.relations || []) {
      if (edge.type !== 'ParentChild' && edge.type !== 0) continue;
      if (nodes.has(edge.fromId) && nodes.has(edge.toId)) {
        parents.set(edge.toId, edge.fromId);
        nodes.get(edge.fromId).children.push(nodes.get(edge.toId));
      }
    }
    const roots = [], unresolved = [];
    for (const [id, node] of nodes) {
      if (parents.has(id)) continue;
      (['unresolved', 'parent-not-emitted'].includes(node.heading.parentStatus) ? unresolved : roots).push(node);
    }
    return { nodes, roots, unresolved };
  }
  function element(tag, text, className) {
    const e = document.createElement(tag);
    if (text != null) e.textContent = String(text);
    if (className) e.className = className;
    return e;
  }
  function field(target, key, value) {
    target.append(element('dt', key), element('dd', value == null ? 'unavailable / not-recorded' : value));
  }
  function inspect(heading) {
    const target = document.getElementById('v2Inspector');
    target.replaceChildren(element('h3', 'Heading Evidence Inspector'));
    const dl = element('dl', null, 'v2-facts');
    for (const [key, value] of Object.entries({
      'Structural element ID': heading.elementId, 'Stable ID': heading.stableId,
      'Source ID': heading.sourceId, 'Heading text': heading.text, 'Level': heading.level,
      'Validated parent': heading.parentId, 'Parent status': heading.parentStatus,
      'Hierarchy resolution': heading.hierarchyResolution,
      'Decision origin': heading.decision?.origin, 'Decision status': heading.decision?.status,
      'Confidence basis': heading.decision?.confidenceBasis, 'Disputed': heading.decision?.disputed,
      'Boundary evidence (producer assertion)': heading.boundaryEvidence,
      'Inline body': heading.inlineBody,
      'Inline body span': heading.inlineBodySpan == null ? null : JSON.stringify(heading.inlineBodySpan)
    })) field(dl, key, value);
    target.append(dl);
    for (const source of heading.sources || []) {
      const section = element('section', null, 'v2-source');
      section.append(element('h4', source.sourceId + ' · ordinal ' + source.sourceOrdinal));
      const facts = element('dl', null, 'v2-facts');
      field(facts, 'Exact span [start,end)', JSON.stringify(source.span));
      field(facts, 'Availability', source.availability);
      field(facts, 'Source coordinates', source.coordinates == null ? null : JSON.stringify(source.coordinates));
      section.append(facts, element('h4', 'Selected source text'), element('pre', source.selectedText ?? 'unavailable'),
        element('h4', 'Original source text'), element('pre', source.sourceText ?? 'unavailable'));
      target.append(section);
    }
    const validation = element('details');
    validation.append(element('summary', 'Grounding / validation facts'), element('pre', JSON.stringify(heading.validation, null, 2)));
    target.append(validation);
  }
  function render(model) {
    const panel = document.getElementById('pipelineV2');
    if (!panel) return;
    panel.classList.remove('hidden');
    const tree = document.getElementById('v2Tree');
    tree.replaceChildren();
    document.getElementById('v2Inspector').replaceChildren(element('p', 'Select a heading to inspect same-execution evidence.'));
    const summary = document.getElementById('v2Summary');
    if (!model || model.availability !== 'same-execution') {
      summary.textContent = 'Authority graph unavailable; compatibility outline below is not a hierarchy tree.';
      tree.append(element('p', 'Same-execution authority was not retained. No hierarchy has been inferred.'));
      document.getElementById('v2Stages').replaceChildren(element('p', 'Execution stages: not-recorded'));
      return;
    }
    document.getElementById('treePanel').classList.add('hidden');
    document.getElementById('statsPanel').classList.add('hidden');
    summary.textContent = `${model.summary.accepted} accepted · ${model.summary.unknownLevel} unknown level · ` +
      `${model.summary.requiresReview} require review · rejected: ${model.summary.rejected ?? model.summary.rejectedAvailability}`;
    const forest = buildForest(model);
    function draw(node, visited) {
      if (visited.has(node.heading.elementId)) return element('li', 'Invalid cyclic relationship; unavailable');
      const next = new Set(visited); next.add(node.heading.elementId);
      const li = element('li'); li.setAttribute('role', 'treeitem');
      const row = element('div', null, 'v2-tree-row');
      const group = element('ul'); group.setAttribute('role', 'group');
      if (node.children.length) {
        const toggle = element('button', '−', 'v2-toggle');
        toggle.type = 'button'; toggle.setAttribute('aria-label', 'Collapse ' + node.heading.text);
        toggle.setAttribute('aria-expanded', 'true');
        toggle.onclick = () => {
          group.hidden = !group.hidden;
          toggle.textContent = group.hidden ? '+' : '−';
          toggle.setAttribute('aria-expanded', String(!group.hidden));
          toggle.setAttribute('aria-label', (group.hidden ? 'Expand ' : 'Collapse ') + node.heading.text);
        };
        row.append(toggle);
      }
      const select = element('button', node.heading.text, 'v2-select'); select.type = 'button';
      select.dataset.elementId = node.heading.elementId;
      select.onclick = () => {
        panel.querySelectorAll('.v2-select').forEach(b => b.setAttribute('aria-pressed', String(b === select)));
        inspect(node.heading);
      };
      row.append(select, element('small', `L${node.heading.level ?? '?'} · ${node.heading.sourceId} · ${node.heading.decision.status}`));
      li.append(row);
      for (const child of node.children) group.append(draw(child, next));
      if (node.children.length) li.append(group);
      return li;
    }
    function bucket(label, nodes) {
      tree.append(element('h3', label));
      const list = element('ul'); list.setAttribute('role', 'tree'); list.setAttribute('aria-label', label);
      for (const node of nodes) list.append(draw(node, new Set()));
      tree.append(list);
      if (!nodes.length) tree.append(element('p', 'None'));
    }
    bucket('Roots — no validated parent', forest.roots);
    bucket('Unresolved / parent not emitted', forest.unresolved);
    const stages = document.getElementById('v2Stages'); stages.replaceChildren();
    for (const stage of model.stages) stages.append(element('li', `${stage.id} — ${stage.status}: ${stage.evidence}`));
    document.getElementById('v2Diagnostics').textContent = JSON.stringify({ executionId: model.executionId,
      outcome: model.outcome, sourceKind: model.sourceKind, summary: model.summary,
      checkpoints: model.checkpoints, provenance: model.provenance, audit: model.audit }, null, 2);
  }
  function reset() {
    document.getElementById('pipelineV2')?.classList.add('hidden');
    document.getElementById('v2Diagnostics')?.replaceChildren();
  }
  function showFailure(message) {
    reset();
    const panel = document.getElementById('pipelineV2'); if (!panel) return;
    panel.classList.remove('hidden');
    document.getElementById('v2Summary').textContent = 'Execution failed — no accepted authority result';
    document.getElementById('v2Tree').replaceChildren(element('p', String(message)));
    document.getElementById('v2Inspector').replaceChildren();
    document.getElementById('v2Stages').replaceChildren(element('li', 'execution — failed: server error event; sub-stages not-recorded'));
  }
  const api = { buildForest, render, reset, showFailure };
  if (typeof module !== 'undefined') module.exports = api;
  global.WebPipelineV2 = api;
})(typeof window !== 'undefined' ? window : globalThis);
