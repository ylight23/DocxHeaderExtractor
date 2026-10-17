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
    document.getElementById('v2Diagnostics').replaceChildren();
    document.getElementById('v2PlacementDiagnostics')?.replaceChildren();
    if (!model || model.availability !== 'same-execution') {
      summary.textContent = 'Authority graph unavailable; compatibility outline below is not a hierarchy tree.';
      tree.append(element('p', 'Same-execution authority was not retained. No hierarchy has been inferred.'));
      document.getElementById('v2Stages').replaceChildren(element('p', 'Execution stages: not-recorded'));
      return;
    }
    document.getElementById('treePanel').classList.add('hidden');
    document.getElementById('statsPanel').classList.add('hidden');
    summary.textContent = `${model.summary.accepted} tiêu đề · ${model.summary.unknownLevel} chưa có cấp · ` +
      `${model.summary.requiresReview} cần xem lại` +
      (typeof model.summary.rejected === 'number' ? ` · ${model.summary.rejected} bị loại` : '');
    const forest = buildForest(model);
    const STATUS = { RequiresReview: ['Cần xem lại', 'review'], HumanVerified: ['Đã xác nhận', 'ok'] };
    function levelOf(heading) { return heading.level == null ? 'unknown' : String(Math.min(heading.level, 6)); }
    function levelBadge(heading) {
      const badge = element('span', heading.level == null ? 'H?' : 'H' + heading.level, 'v2-level');
      badge.dataset.level = levelOf(heading);
      badge.title = heading.level == null ? 'Chưa có cấp' : 'Cấp ' + heading.level;
      return badge;
    }
    function chips(heading) {
      const out = [];
      const [label, kind] = STATUS[heading.decision?.status] || [heading.decision?.status, 'neutral'];
      if (label) out.push(element('span', label, 'v2-chip v2-chip-' + kind));
      const resolution = String(heading.hierarchyResolution || '');
      if (resolution.includes('out-of-hierarchy')) out.push(element('span', 'Ngoài cây mục', 'v2-chip v2-chip-neutral'));
      else if (resolution.includes('unresolved')) out.push(element('span', 'Chưa xác định cha', 'v2-chip v2-chip-neutral'));
      return out;
    }
    function draw(node, visited) {
      if (visited.has(node.heading.elementId)) return element('li', 'Quan hệ vòng không hợp lệ; không hiển thị');
      const next = new Set(visited); next.add(node.heading.elementId);
      const li = element('li'); li.setAttribute('role', 'treeitem');
      const row = element('div', null, 'v2-row');
      row.dataset.level = levelOf(node.heading);
      const group = element('ul'); group.setAttribute('role', 'group');
      if (node.children.length) {
        const toggle = element('button', '▾', 'v2-toggle');
        toggle.type = 'button'; toggle.setAttribute('aria-label', 'Thu gọn ' + node.heading.text);
        toggle.setAttribute('aria-expanded', 'true');
        toggle.onclick = () => {
          group.hidden = !group.hidden;
          toggle.textContent = group.hidden ? '▸' : '▾';
          toggle.setAttribute('aria-expanded', String(!group.hidden));
          toggle.setAttribute('aria-label', (group.hidden ? 'Mở rộng ' : 'Thu gọn ') + node.heading.text);
        };
        row.append(toggle);
      } else {
        row.append(element('span', null, 'v2-toggle-spacer'));
      }
      const select = element('button', null, 'v2-select'); select.type = 'button';
      select.dataset.elementId = node.heading.elementId;
      // The source id is long (page, coordinates, text); it stays in the tooltip and in the inspector.
      select.title = 'Source ID: ' + node.heading.sourceId;
      select.append(levelBadge(node.heading), element('span', node.heading.text, 'v2-text'));
      select.onclick = () => {
        panel.querySelectorAll('.v2-select').forEach(b => b.setAttribute('aria-pressed', String(b === select)));
        inspect(node.heading);
      };
      row.append(select, ...chips(node.heading));
      li.append(row);
      for (const child of node.children) group.append(draw(child, next));
      if (node.children.length) li.append(group);
      return li;
    }
    function bucket(label, nodes) {
      tree.append(element('h3', `${label} (${nodes.length})`));
      const list = element('ul'); list.setAttribute('role', 'tree'); list.setAttribute('aria-label', label);
      for (const node of nodes) list.append(draw(node, new Set()));
      tree.append(list);
      if (!nodes.length) tree.append(element('p', 'Không có'));
    }
    const setAll = open => {
      tree.querySelectorAll('ul[role=group]').forEach(g => { g.hidden = !open; });
      tree.querySelectorAll('.v2-toggle').forEach(toggle => {
        toggle.textContent = open ? '▾' : '▸';
        toggle.setAttribute('aria-expanded', String(open));
      });
    };
    const toolbar = element('div', null, 'v2-toolbar');
    const expandAll = element('button', 'Mở rộng tất cả', 'secondary'); expandAll.type = 'button'; expandAll.onclick = () => setAll(true);
    const collapseAll = element('button', 'Thu gọn tất cả', 'secondary'); collapseAll.type = 'button'; collapseAll.onclick = () => setAll(false);
    toolbar.append(expandAll, collapseAll);
    tree.append(toolbar);
    bucket('Mục gốc, chưa có cha', forest.roots);
    bucket('Chưa xác định vị trí (cha chưa được phát)', forest.unresolved);
    const stages = document.getElementById('v2Stages'); stages.replaceChildren();
    for (const stage of model.stages) stages.append(element('li', `${stage.id} — ${stage.status}: ${stage.evidence}`));
    renderPlacementDiagnostics(model.audit?.placementExecution, model.audit?.headingAliasStates);
    document.getElementById('v2Diagnostics').textContent = JSON.stringify({ executionId: model.executionId,
      outcome: model.outcome, sourceKind: model.sourceKind, summary: model.summary,
      checkpoints: model.checkpoints, provenance: model.provenance, audit: model.audit }, null, 2);
  }
  function renderPlacementDiagnostics(placement, aliasStates) {
    const host = document.getElementById('v2PlacementDiagnostics');
    if (!host || !placement) return;
    host.replaceChildren(element('h3', 'Hierarchy placement — quyết định từng heading'));
    const decisions = placement.decisions || [];
    if (decisions.length) {
      const table = element('table', null, 'v2-audit-table');
      const head = element('tr');
      for (const label of ['Alias', 'Cha đề xuất', 'Verifier', 'Cơ sở', 'Alias chặn', 'Bằng chứng']) head.append(element('th', label));
      table.append(head);
      for (const d of decisions) {
        const row = element('tr');
        for (const value of [d.alias, d.proposedParentAlias, d.verificationStatus, d.resolutionBasis,
          (d.blockingAliases || []).join(', '), (d.evidenceRefs || []).join(', ')]) row.append(element('td', value ?? '—'));
        table.append(row);
      }
      host.append(table);
    }
    const traced = new Map((placement.sourceTrace || []).map(s => [s.alias, s]));
    const states = Object.entries(aliasStates || {}).map(([alias, state]) => ({
      alias, sourceId: traced.get(alias)?.sourceId ?? alias,
      initialSelection: state.initialSelection, finalSelection: state.finalSelection,
      verificationMembership: state.verificationMembership, bindingStatus: state.bindingStatus,
      placementResolution: traced.get(alias)?.placementResolution ?? 'not-bound'
    }));
    const excluded = states.filter(s => s.finalSelection === 'NOT_SELECT');
    host.append(element('p', `Nguồn: ${states.length} dòng có trạng thái; selection đánh dấu NOT_SELECT=${excluded.length}. Các dòng này chỉ là bằng chứng nguồn, không được phục hồi thành heading.`));
    if (states.length) {
      const details = element('details'); details.append(element('summary', 'Trạng thái upstream theo dòng nguồn'));
      const filter = element('input'); filter.type = 'search'; filter.placeholder = 'Lọc theo alias, trạng thái hoặc source ID';
      filter.setAttribute('aria-label', 'Lọc trạng thái upstream');
      const tableHost = element('div');
      const drawStates = () => {
        const table = element('table', null, 'v2-audit-table');
        const head = element('tr');
        for (const label of ['Alias', 'Source ID', 'Selection ban đầu', 'Selection cuối', 'Membership', 'Binding', 'Hierarchy']) head.append(element('th', label));
        table.append(head);
        const needle = filter.value.trim().toLowerCase();
        for (const s of states) {
          const values = [s.alias, s.sourceId, s.initialSelection, s.finalSelection, s.verificationMembership, s.bindingStatus, s.placementResolution];
          if (needle && !values.some(value => String(value ?? '').toLowerCase().includes(needle))) continue;
          const row = element('tr');
          for (const value of values) row.append(element('td', value ?? '—'));
          table.append(row);
        }
        tableHost.replaceChildren(table);
      };
      filter.addEventListener('input', drawStates);
      details.addEventListener('toggle', () => { if (details.open && !tableHost.childElementCount) drawStates(); });
      details.append(filter, tableHost); host.append(details);
    }
    if (placement.failures?.length) host.append(element('p', 'Placement protocol failures: ' + placement.failures.join('; ')));
  }
  function reset() {
    document.getElementById('pipelineV2')?.classList.add('hidden');
    document.getElementById('v2Diagnostics')?.replaceChildren();
    document.getElementById('v2PlacementDiagnostics')?.replaceChildren();
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
  const api = { buildForest, render, reset, showFailure, renderPlacementDiagnostics };
  if (typeof module !== 'undefined') module.exports = api;
  global.WebPipelineV2 = api;
})(typeof window !== 'undefined' ? window : globalThis);
