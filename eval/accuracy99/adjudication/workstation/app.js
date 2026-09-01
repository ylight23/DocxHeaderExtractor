(() => {
  'use strict';

  const LABELS = ['HEADING', 'NON_HEADING', 'UNCERTAIN', 'EXCLUDED'];
  const STRUCTURAL_TYPES = new Set(['Title', 'Subtitle', 'Heading', 'ListItem', 'Caption', 'TableTitle', 'FigureTitle', 'Figure', 'Table']);
  const packets = new Map();
  let currentDatasetId = null;
  let currentIndex = 0;
  let filter = 'ALL';

  const $ = id => document.getElementById(id);
  const esc = value => String(value ?? '').replace(/[&<>"']/g, ch => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[ch]));
  const effectiveLabel = row => row.finalAdjudicatedLabel || row.initialAdjudicatedLabel || row.adjudicatedLabel || null;
  const rows = () => packets.get(currentDatasetId)?.occurrences || [];
  const currentRow = () => rows()[currentIndex];
  const setNotice = (message = '', error = false) => { $('notice').textContent = message; $('notice').className = error ? 'notice error' : 'notice'; };

  function parsePacket(text, fileName) {
    const lines = text.split(/\r?\n/).filter(line => line.trim());
    if (!lines.length) throw new Error(`${fileName}: empty packet`);
    const manifest = JSON.parse(lines[0]);
    if (manifest.recordType !== 'manifest' || !manifest.datasetId) throw new Error(`${fileName}: invalid manifest`);
    const occurrences = lines.slice(1).map((line, offset) => {
      const row = JSON.parse(line);
      if (row.recordType !== 'occurrence') throw new Error(`${fileName}: invalid occurrence line ${offset + 2}`);
      return row;
    });
    if (occurrences.length !== Number(manifest.catalogOccurrenceCount)) throw new Error(`${fileName}: occurrence count does not match manifest`);
    const ids = new Set();
    for (const row of occurrences) {
      if (ids.has(row.sourceId)) throw new Error(`${fileName}: duplicate SourceId ${row.sourceId}`);
      ids.add(row.sourceId);
    }
    if (manifest.predictionsIncluded) throw new Error(`${fileName}: production predictions are not allowed in source-first packets`);
    return { fileName, manifest, occurrences };
  }

  function importFiles(fileList) {
    const files = [...fileList];
    if (!files.length) return;
    let pending = files.length;
    let imported = 0;
    for (const file of files) {
      const reader = new FileReader();
      reader.onload = () => {
        try {
          const packet = parsePacket(reader.result, file.name);
          packets.set(packet.manifest.datasetId, packet);
          if (!currentDatasetId) currentDatasetId = packet.manifest.datasetId;
          imported++;
        } catch (error) { setNotice(error.message, true); }
        if (!--pending) { renderPacketList(); render(); setNotice(imported ? `${imported} packet(s) loaded in memory. Original files are unchanged.` : ''); }
      };
      reader.onerror = () => { if (!--pending) setNotice('Could not read one or more packet files.', true); };
      reader.readAsText(file);
    }
  }

  function renderPacketList() {
    $('emptyState').hidden = packets.size > 0;
    $('packetList').innerHTML = [...packets.values()].map(packet => {
      const reviewed = packet.occurrences.filter(row => effectiveLabel(row)).length;
      const active = packet.manifest.datasetId === currentDatasetId ? ' active' : '';
      return `<button type="button" class="packet-item${active}" data-dataset="${esc(packet.manifest.datasetId)}"><span class="packet-name">${esc(packet.manifest.datasetId)}</span><span class="packet-count">${reviewed}/${packet.occurrences.length}</span></button>`;
    }).join('');
    document.querySelectorAll('.packet-item').forEach(button => button.addEventListener('click', () => {
      currentDatasetId = button.dataset.dataset;
      currentIndex = 0;
      renderPacketList(); render();
    }));
  }

  function render() {
    const packet = packets.get(currentDatasetId);
    if (!packet) { $('reviewView').hidden = true; $('progressSummary').textContent = 'No packet loaded.'; $('counts').innerHTML = ''; $('progressBar').style.width = '0%'; return; }
    $('reviewView').hidden = false;
    currentIndex = Math.max(0, Math.min(currentIndex, packet.occurrences.length - 1));
    const row = packet.occurrences[currentIndex];
    $('documentLabel').textContent = `DOCUMENT ${packet.manifest.datasetId}`;
    $('occurrenceTitle').textContent = `Occurrence ${row.sourceOrdinal}`;
    $('sourceIdentity').textContent = row.sourceId;
    const visible = visibleIndices(packet);
    const visiblePosition = Math.max(0, visible.indexOf(currentIndex));
    $('position').textContent = `${visiblePosition + 1} / ${visible.length}`;
    $('rawText').textContent = row.rawSourceText || '(empty source text)';
    $('previousText').textContent = row.previousSourceText || '(none)';
    $('nextText').textContent = row.nextSourceText || '(none)';
    const provenance = row.historicalProvenanceStatus || 'NONE';
    $('provenance').textContent = provenance;
    $('provenance').className = `badge ${provenance === 'EXACT_REBOUND' ? 'exact' : provenance === 'AMBIGUOUS' ? 'ambiguous' : provenance === 'REVIEW_REQUIRED' ? 'review' : ''}`;
    $('metadata').innerHTML = [
      ['DocumentId', row.documentId], ['SourceOrdinal', row.sourceOrdinal], ['SourceType', row.sourceType], ['Page', row.page ?? '—'],
      ['Raw text length', row.rawTextLength], ['Raw source span', `${row.rawSourceSpan?.start ?? 0}..${row.rawSourceSpan?.end ?? 0}`],
    ].map(([key, value]) => `<dt>${esc(key)}</dt><dd>${esc(value)}</dd>`).join('');
    const label = effectiveLabel(row);
    document.querySelectorAll('.label').forEach(button => button.classList.toggle('selected', button.dataset.label === label));
    $('labelStatus').textContent = label ? `Current human label: ${label}` : 'No label selected.';
    $('headingFields').hidden = label !== 'HEADING';
    populateFields(row);
    renderProgress(packet);
    renderDocumentStatus(packet);
    validateCurrent(false);
    renderPacketList();
  }

  function visibleIndices(packet) {
    return packet.occurrences.map((row, index) => ({ row, index })).filter(({ row }) => {
      const label = effectiveLabel(row);
      if (filter === 'UNREVIEWED') return !label;
      if (filter === 'HEADING') return label === 'HEADING';
      if (filter === 'UNCERTAIN') return label === 'UNCERTAIN';
      if (filter === 'REVIEW_REQUIRED') return row.historicalProvenanceStatus === 'REVIEW_REQUIRED';
      if (filter === 'AMBIGUOUS') return row.historicalProvenanceStatus === 'AMBIGUOUS';
      return true;
    }).map(({ index }) => index);
  }

  function populateFields(row) {
    $('headingStart').value = row.headingStart ?? '';
    $('headingEnd').value = row.headingEnd ?? '';
    $('structuralType').value = row.structuralType ?? '';
    $('level').value = row.level ?? '';
    $('levelNotReviewed').checked = row.levelReviewStatus === 'LEVEL_NOT_REVIEWED';
    $('reviewer').value = row.reviewer ?? '';
    $('parentStatus').value = row.parentReviewStatus ?? '';
    $('parentIdLabel').hidden = row.parentReviewStatus !== 'PARENT_REVIEWED';
    populateParentChoices(row);
    $('parentGoldId').value = row.parentGoldId ?? '';
    $('reviewNotes').value = row.reviewNotes ?? '';
    updateHeadingText(row);
  }

  function populateParentChoices(row) {
    const options = rows().filter(candidate => candidate !== row && effectiveLabel(candidate) === 'HEADING' && candidate.goldHeadingId).map(candidate =>
      `<option value="${esc(candidate.goldHeadingId)}">${esc(candidate.goldHeadingId)} · ${esc((candidate.headingText || '').slice(0, 90))} · ordinal ${esc(candidate.sourceOrdinal)}</option>`).join('');
    $('parentGoldId').innerHTML = '<option value="">Select heading…</option>' + options;
  }

  function renderProgress(packet) {
    const list = packet.occurrences;
    const reviewed = list.filter(row => effectiveLabel(row)).length;
    const heading = list.filter(row => effectiveLabel(row) === 'HEADING').length;
    const count = label => list.filter(row => effectiveLabel(row) === label).length;
    const spanIncomplete = list.filter(row => effectiveLabel(row) === 'HEADING' && (row.headingStart == null || row.headingEnd == null || !row.headingText)).length;
    const levelIncomplete = list.filter(row => effectiveLabel(row) === 'HEADING' && !['REVIEWED', 'LEVEL_NOT_REVIEWED'].includes(row.levelReviewStatus)).length;
    const parentIncomplete = list.filter(row => effectiveLabel(row) === 'HEADING' && !['ROOT', 'PARENT_REVIEWED', 'PARENT_UNKNOWN'].includes(row.parentReviewStatus)).length;
    const percent = list.length ? (reviewed / list.length) * 100 : 0;
    $('progressSummary').innerHTML = `<strong>${esc(packet.manifest.datasetId)}</strong><br>Current: ${currentIndex + 1} / ${list.length}<br>Reviewed: ${reviewed} · Remaining: ${list.length - reviewed}`;
    $('progressBar').style.width = `${percent}%`;
    $('counts').innerHTML = [
      ['HEADING', heading], ['NON_HEADING', count('NON_HEADING')], ['UNCERTAIN', count('UNCERTAIN')], ['EXCLUDED', count('EXCLUDED')],
      ['Heading spans incomplete', spanIncomplete], ['Levels incomplete', levelIncomplete], ['Parents incomplete', parentIncomplete],
    ].map(([label, value]) => `<dt>${label}</dt><dd>${value}</dd>`).join('');
  }

  function renderDocumentStatus(packet) {
    const label = packet.occurrences.filter(row => effectiveLabel(row)).length;
    const errors = packet.occurrences.reduce((total, row) => total + validateRow(row, packet.occurrences).length, 0);
    const ready = label === packet.occurrences.length && errors === 0 && packet.manifest.reviewStatus === 'REVIEW_COMPLETE';
    $('documentStatus').innerHTML = `<p><strong>${esc(packet.manifest.datasetId)}: ${ready ? 'READY' : 'INCOMPLETE'}</strong> · ${label}/${packet.occurrences.length} classified · ${errors} current validation issue(s)</p><p class="muted">Export never overwrites the imported file. Complete all rows and set the manifest to REVIEW_COMPLETE before C2 import.</p>`;
  }

  function updateHeadingText(row) {
    const start = Number(row.headingStart), end = Number(row.headingEnd);
    $('headingText').textContent = Number.isInteger(start) && Number.isInteger(end) && start >= 0 && end > start ? row.rawSourceText.slice(start, end) : 'Not selected.';
  }

  function setField(name, value) { const row = currentRow(); if (!row) return; row[name] = value === '' ? null : value; updateHeadingText(row); validateCurrent(false); renderProgress(packets.get(currentDatasetId)); renderDocumentStatus(packets.get(currentDatasetId)); }

  async function sha256(value) {
    const data = new TextEncoder().encode(value);
    const digest = await crypto.subtle.digest('SHA-256', data);
    return [...new Uint8Array(digest)].map(byte => byte.toString(16).padStart(2, '0')).join('');
  }

  async function goldHeadingId(row) {
    const framed = [row.documentId, row.sourceId, row.headingStart, row.headingEnd].map(value => `${String(value ?? '').length}:${value ?? ''}|`).join('');
    return `gold-heading:${await sha256(framed)}`;
  }

  function selectionOffsets(container) {
    const selection = window.getSelection();
    if (!selection || selection.rangeCount === 0 || selection.isCollapsed) return null;
    const range = selection.getRangeAt(0);
    if (!container.contains(range.commonAncestorContainer)) return null;
    const before = range.cloneRange(); before.selectNodeContents(container); before.setEnd(range.startContainer, range.startOffset);
    const selected = range.toString();
    const start = before.toString().length;
    const end = start + selected.length;
    return { start, end, selected };
  }

  async function applySelection() {
    const row = currentRow(); const selected = selectionOffsets($('rawText'));
    if (!row || !selected || selected.start >= selected.end) { setNotice('Select a non-empty exact substring in Raw source text first.', true); return; }
    row.headingStart = selected.start; row.headingEnd = selected.end; row.headingText = selected.selected; row.goldHeadingId = await goldHeadingId(row);
    setNotice('Human-selected heading span applied.'); render();
  }

  function setLabel(label) {
    const row = currentRow(); if (!row) return;
    row.adjudicatedLabel = label;
    if (label !== 'HEADING') { /* Keep fields visible in validation if stale; a human can clear them explicitly. */ }
    render();
  }

  function clearHeadingFields() {
    const row = currentRow(); if (!row) return;
    ['headingStart','headingEnd','headingText','structuralType','level','levelReviewStatus','parentGoldId','parentReviewStatus','goldHeadingId'].forEach(field => row[field] = null);
    render();
  }

  function validateRow(row, allRows) {
    const errors = []; const label = effectiveLabel(row); const text = row.rawSourceText || '';
    if (!label) errors.push('label required');
    if (label && !LABELS.includes(label)) errors.push('invalid label');
    if (label === 'HEADING') {
      if (!Number.isInteger(row.headingStart) || !Number.isInteger(row.headingEnd) || row.headingStart < 0 || row.headingStart >= row.headingEnd || row.headingEnd > text.length) errors.push('invalid heading span');
      else if (text.slice(row.headingStart, row.headingEnd) !== row.headingText) errors.push('heading text does not match span');
      if (!STRUCTURAL_TYPES.has(row.structuralType)) errors.push('structural type required');
      if (!(row.levelReviewStatus === 'LEVEL_NOT_REVIEWED' && row.level == null) && !(row.levelReviewStatus === 'REVIEWED' && Number.isInteger(row.level) && row.level >= 1)) errors.push('level review required');
      if (!['ROOT','PARENT_REVIEWED','PARENT_UNKNOWN'].includes(row.parentReviewStatus)) errors.push('parent review required');
      if (row.parentReviewStatus === 'PARENT_REVIEWED') {
        const parent = allRows.find(candidate => candidate.goldHeadingId === row.parentGoldId && effectiveLabel(candidate) === 'HEADING');
        if (!parent) errors.push('parent heading not found');
        else if (parent.documentId !== row.documentId) errors.push('parent document mismatch');
        else if (parent === row) errors.push('heading cannot parent itself');
      } else if (row.parentGoldId != null) errors.push('parent id contradicts status');
    } else if (label && ['NON_HEADING','UNCERTAIN','EXCLUDED'].includes(label)) {
      if (['headingStart','headingEnd','headingText','structuralType','level','levelReviewStatus','parentGoldId','parentReviewStatus','goldHeadingId'].some(field => row[field] != null)) errors.push('non-heading fields must be cleared');
    }
    if (label && !String(row.reviewer || '').trim()) errors.push('reviewer required');
    return errors;
  }

  function validateCurrent(showNotice) {
    const row = currentRow(); if (!row) return [];
    const errors = validateRow(row, rows());
    $('validation').className = errors.length ? 'validation' : 'validation ok';
    $('validation').innerHTML = errors.length ? `<strong>Not import-ready:</strong><ul>${errors.map(error => `<li>${esc(error)}</li>`).join('')}</ul>` : (effectiveLabel(row) ? 'Current occurrence passes local validation.' : 'Choose a human label to begin.');
    if (showNotice) setNotice(errors.length ? `${errors.length} validation issue(s) in ${row.sourceId}.` : 'Current packet rows validated locally.');
    return errors;
  }

  function validatePacket() {
    const packet = packets.get(currentDatasetId); if (!packet) return;
    const errors = packet.occurrences.flatMap(row => validateRow(row, packet.occurrences).map(error => `${row.sourceId}: ${error}`));
    const manifestError = packet.manifest.reviewStatus !== 'REVIEW_COMPLETE' ? 'manifest: reviewStatus must be REVIEW_COMPLETE before import' : null;
    if (manifestError) errors.push(manifestError);
    $('validation').className = errors.length ? 'validation' : 'validation ok';
    $('validation').innerHTML = errors.length ? `<strong>Packet is not ready:</strong><ul>${errors.slice(0, 30).map(error => `<li>${esc(error)}</li>`).join('')}${errors.length > 30 ? `<li>…and ${errors.length - 30} more</li>` : ''}</ul>` : 'Packet passes all workstation validation gates.';
    setNotice(errors.length ? `${errors.length} packet issue(s) found.` : 'Packet passes all workstation validation gates.');
  }

  function markComplete() {
    const packet = packets.get(currentDatasetId); if (!packet) return;
    const errors = packet.occurrences.flatMap(row => validateRow(row, packet.occurrences));
    if (errors.length) { setNotice('Resolve all occurrence validation issues before marking REVIEW_COMPLETE.', true); validatePacket(); return; }
    packet.manifest.reviewStatus = 'REVIEW_COMPLETE';
    render(); setNotice(`${packet.manifest.datasetId} marked REVIEW_COMPLETE in memory. Export it for Phase C2.`);
  }

  function exportPacket(packet) {
    const errors = packet.occurrences.flatMap(row => validateRow(row, packet.occurrences));
    if (errors.length || packet.manifest.reviewStatus !== 'REVIEW_COMPLETE') { setNotice(`Cannot export ${packet.manifest.datasetId} as ready: complete labels, fields, reviewer, and manifest status first.`, true); return; }
    const lines = [JSON.stringify(packet.manifest), ...packet.occurrences.map(row => JSON.stringify(row))].join('\n') + '\n';
    const blob = new Blob([lines], { type: 'application/x-ndjson' }); const url = URL.createObjectURL(blob); const anchor = document.createElement('a');
    anchor.href = url; anchor.download = `${packet.manifest.datasetId}.review.completed.jsonl`; anchor.click(); URL.revokeObjectURL(url);
    setNotice(`Exported ${packet.manifest.datasetId}.review.completed.jsonl. The original packet was not overwritten.`);
  }

  function exportAll() { if (!packets.size) return setNotice('Load packet(s) first.', true); for (const packet of packets.values()) exportPacket(packet); }

  function move(delta) {
    const packet = packets.get(currentDatasetId); if (!packet) return;
    const visible = visibleIndices(packet); if (!visible.length) return;
    let position = visible.indexOf(currentIndex); if (position < 0) position = delta > 0 ? -1 : visible.length;
    currentIndex = visible[Math.max(0, Math.min(visible.length - 1, position + delta))]; render();
  }

  $('packetFiles').addEventListener('change', event => importFiles(event.target.files));
  $('previous').addEventListener('click', () => move(-1)); $('next').addEventListener('click', () => move(1));
  $('filter').addEventListener('change', event => { filter = event.target.value; const packet = packets.get(currentDatasetId); const visible = packet ? visibleIndices(packet) : []; if (visible.length && !visible.includes(currentIndex)) currentIndex = visible[0]; render(); });
  $('useSelection').addEventListener('click', applySelection); $('clearHeadingFields').addEventListener('click', clearHeadingFields);
  $('validatePacket').addEventListener('click', validatePacket); $('exportCurrent').addEventListener('click', () => { const packet = packets.get(currentDatasetId); if (packet) exportPacket(packet); else setNotice('Load packet(s) first.', true); }); $('exportAll').addEventListener('click', exportAll);
  $('markComplete').addEventListener('click', markComplete);
  document.querySelectorAll('.label').forEach(button => button.addEventListener('click', () => setLabel(button.dataset.label)));
  $('headingStart').addEventListener('change', event => { const row = currentRow(); if (!row) return; row.headingStart = event.target.value === '' ? null : Number(event.target.value); row.headingText = Number.isInteger(row.headingStart) && Number.isInteger(row.headingEnd) ? row.rawSourceText.slice(row.headingStart, row.headingEnd) : null; goldHeadingId(row).then(id => { row.goldHeadingId = id; render(); }); });
  $('headingEnd').addEventListener('change', event => { const row = currentRow(); if (!row) return; row.headingEnd = event.target.value === '' ? null : Number(event.target.value); row.headingText = Number.isInteger(row.headingStart) && Number.isInteger(row.headingEnd) ? row.rawSourceText.slice(row.headingStart, row.headingEnd) : null; goldHeadingId(row).then(id => { row.goldHeadingId = id; render(); }); });
  $('structuralType').addEventListener('change', event => setField('structuralType', event.target.value));
  $('level').addEventListener('change', event => { const row = currentRow(); if (row) { row.level = event.target.value === '' ? null : Number(event.target.value); row.levelReviewStatus = row.level == null ? null : 'REVIEWED'; render(); } });
  $('levelNotReviewed').addEventListener('change', event => { const row = currentRow(); if (row) { row.levelReviewStatus = event.target.checked ? 'LEVEL_NOT_REVIEWED' : (row.level == null ? null : 'REVIEWED'); if (event.target.checked) row.level = null; render(); } });
  $('parentStatus').addEventListener('change', event => { const row = currentRow(); if (row) { row.parentReviewStatus = event.target.value || null; if (row.parentReviewStatus !== 'PARENT_REVIEWED') row.parentGoldId = null; render(); } });
  $('parentGoldId').addEventListener('change', event => setField('parentGoldId', event.target.value));
  $('reviewer').addEventListener('change', event => setField('reviewer', event.target.value.trim())); $('reviewNotes').addEventListener('change', event => setField('reviewNotes', event.target.value));
  document.addEventListener('keydown', event => { if (event.target.matches('input,textarea,select')) return; const key = event.key.toLowerCase(); if (key === 'h') setLabel('HEADING'); else if (key === 'n') setLabel('NON_HEADING'); else if (key === 'u') setLabel('UNCERTAIN'); else if (key === 'x') setLabel('EXCLUDED'); else if (event.key === 'ArrowLeft') move(-1); else if (event.key === 'ArrowRight') move(1); });
})();
