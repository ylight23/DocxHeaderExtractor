(() => {
  const $ = id => document.getElementById(id);
  const LABELS = ['HEADING', 'NON_HEADING', 'UNCERTAIN', 'EXCLUDED'];
  let packet = null, sessionId = new URLSearchParams(location.search).get('session');
  let currentIndex = 0, frozen = false, saveTimer = null, saving = false, filter = 'ALL';

  function setNotice(message, error = false) { $('notice').textContent = message; $('notice').classList.toggle('error', error); }
  function effectiveLabel(row) { return row.finalAdjudicatedLabel || row.initialAdjudicatedLabel || row.adjudicatedLabel || null; }
  function parseJsonl(text) {
    const nodes = text.split(/\r?\n/).filter(line => line.trim()).map(line => JSON.parse(line));
    if (!nodes.length || nodes[0].recordType !== 'manifest') throw new Error('Review JSONL manifest is missing.');
    const manifest = nodes[0]; const occurrences = nodes.slice(1);
    if (manifest.predictionsIncluded) throw new Error('Source-first review cannot include prediction data.');
    if (occurrences.length !== Number(manifest.catalogOccurrenceCount)) throw new Error('Occurrence count does not match source catalog.');
    if (occurrences.some(row => row.recordType !== 'occurrence' || !row.sourceId)) throw new Error('Occurrence source identity is invalid.');
    return { manifest, occurrences };
  }
  function jsonl() { return [packet.manifest, ...packet.occurrences].map(row => JSON.stringify(row)).join('\n') + '\n'; }

  async function loadSession(id) {
    const response = await fetch(`/api/accuracy99/review/session/${encodeURIComponent(id)}`, { cache: 'no-store' });
    if (!response.ok) throw new Error((await response.json().catch(() => ({}))).message || 'Review session not found.');
    packet = parseJsonl(await response.text()); frozen = response.headers.get('X-DHX-Review-Status') === 'GOLD_FROZEN' || packet.manifest.reviewStatus === 'GOLD_FROZEN';
    currentIndex = 0; render(); setNotice(frozen ? 'GOLD_FROZEN is immutable. Review is read-only.' : 'Review session resumed.');
  }
  async function createSession(file) {
    const form = new FormData(); form.append('file', file);
    setNotice('Reading parser-owned source catalog…');
    const response = await fetch('/api/accuracy99/review/source', { method: 'POST', body: form });
    if (!response.ok) throw new Error((await response.json().catch(() => ({}))).message || 'Could not create review session.');
    sessionId = response.headers.get('X-DHX-Review-Session');
    if (!sessionId) throw new Error('Review session identity was not returned.');
    history.replaceState(null, '', `adjudication.html?session=${encodeURIComponent(sessionId)}`);
    packet = parseJsonl(await response.text()); frozen = packet.manifest.reviewStatus === 'GOLD_FROZEN'; currentIndex = 0; render();
    setNotice(frozen ? 'Existing GOLD_FROZEN review resumed read-only.' : 'Source catalog ready. Draft autosave is active.');
  }

  function validateRow(row) {
    const errors = [], label = effectiveLabel(row), text = row.rawSourceText || '';
    if (!label) errors.push('label required');
    if (label && !LABELS.includes(label)) errors.push('invalid label');
    if (label === 'HEADING') {
      if (!Number.isInteger(row.headingStart) || !Number.isInteger(row.headingEnd) || row.headingStart < 0 || row.headingEnd <= row.headingStart || row.headingEnd > text.length || text.slice(row.headingStart, row.headingEnd) !== row.headingText) errors.push('exact heading span required');
      if (!row.structuralType) errors.push('structural type required');
      if (row.levelReviewStatus === 'REVIEWED' ? !(Number.isInteger(row.level) && row.level >= 1 && row.level <= 9) : row.levelReviewStatus !== 'LEVEL_NOT_REVIEWED' || row.level != null) errors.push('level review required');
      if (!['ROOT', 'PARENT_UNKNOWN', 'PARENT_REVIEWED'].includes(row.parentReviewStatus)) errors.push('parent review required');
      if (row.parentReviewStatus === 'PARENT_REVIEWED' && !row.parentGoldId) errors.push('reviewed parent required');
    } else if (label && ['headingStart','headingEnd','headingText','structuralType','level','levelReviewStatus','parentGoldId','parentReviewStatus','goldHeadingId'].some(field => row[field] != null)) errors.push('non-heading fields must be cleared');
    if (label && !String(row.reviewer || '').trim()) errors.push('reviewer required');
    return errors;
  }
  function allErrors() { return packet.occurrences.flatMap(row => validateRow(row).map(error => `${row.sourceId}: ${error}`)); }
  function updateStatus() {
    if (!packet || frozen) return;
    packet.manifest.reviewStatus = allErrors().length === 0 ? 'REVIEW_COMPLETE' : 'REVIEW_DRAFT';
  }
  function scheduleSave() {
    if (!packet || frozen) return;
    updateStatus(); clearTimeout(saveTimer); setNotice(`Autosave pending · ${packet.manifest.reviewStatus}`);
    saveTimer = setTimeout(saveDraft, 350);
  }
  async function saveDraft() {
    if (!packet || frozen || saving) return;
    saving = true; setNotice(`Autosaving ${packet.manifest.reviewStatus}…`);
    try {
      const response = await fetch(`/api/accuracy99/review/session/${encodeURIComponent(sessionId)}`, { method: 'PUT', headers: { 'Content-Type': 'application/x-ndjson' }, body: jsonl() });
      if (!response.ok) throw new Error((await response.json().catch(() => ({}))).message || 'Autosave failed.');
      setNotice(`Autosaved · ${packet.manifest.reviewStatus}`);
    } catch (error) { setNotice(error.message, true); } finally { saving = false; }
  }
  function rows() { return packet?.occurrences || []; }
  function visibleIndices() {
    return rows().map((row, index) => ({ row, index })).filter(({ row }) =>
      filter === 'ALL' || (filter === 'UNREVIEWED' ? !effectiveLabel(row) :
      ['HEADING', 'NON_HEADING', 'UNCERTAIN', 'EXCLUDED'].includes(filter) ? effectiveLabel(row) === filter :
      (row.historicalProvenanceStatus || 'NONE') === filter)).map(item => item.index);
  }
  function render() {
    if (!packet) return;
    $('reviewView').hidden = false; const visible = visibleIndices(); if (!visible.length) { $('position').textContent = '0 / 0'; return; } if (!visible.includes(currentIndex)) currentIndex = visible[0]; const row = rows()[currentIndex];
    $('sessionTitle').textContent = `${packet.manifest.datasetId} · ${packet.manifest.documentId}`;
    $('sessionStatus').textContent = packet.manifest.reviewStatus; $('sessionStatus').className = `badge ${packet.manifest.reviewStatus === 'GOLD_FROZEN' ? 'exact' : ''}`;
    $('documentLabel').textContent = `DOCUMENT ${packet.manifest.datasetId}`; $('occurrenceTitle').textContent = `Occurrence ${row.sourceOrdinal}`; $('sourceIdentity').textContent = `${row.documentId} · ${row.sourceId}`;
    $('position').textContent = `${visible.indexOf(currentIndex) + 1} / ${visible.length}`; $('filter').value = filter; $('rawText').textContent = row.rawSourceText || '(empty source text)'; $('previousText').textContent = row.previousSourceText || '(none)'; $('nextText').textContent = row.nextSourceText || '(none)';
    const provenance = row.historicalProvenanceStatus || 'NONE'; $('provenance').textContent = provenance; $('provenance').className = `badge ${provenance === 'EXACT_REBOUND' ? 'exact' : provenance === 'AMBIGUOUS' || provenance === 'REVIEW_REQUIRED' ? 'review' : ''}`;
    const refs = Array.isArray(row.historicalPositiveReferences) ? row.historicalPositiveReferences.map(ref => typeof ref === 'string' ? ref : ref.historicalSourceId || ref.historicalText || JSON.stringify(ref)).join(', ') || '—' : '—';
    $('metadata').innerHTML = [['DocumentId',row.documentId],['SourceOrdinal',row.sourceOrdinal],['SourceId',row.sourceId],['SourceType',row.sourceType],['Page',row.page ?? '—'],['Raw text length',row.rawTextLength ?? row.rawSourceText.length],['Raw source span',`${row.rawSourceSpan?.start ?? 0}..${row.rawSourceSpan?.end ?? 0}`],['Historical references',refs]].map(([key,value]) => `<dt>${esc(key)}</dt><dd>${esc(value)}</dd>`).join('');
    const label = effectiveLabel(row); document.querySelectorAll('.label').forEach(button => button.classList.toggle('selected', button.dataset.label === label)); $('labelStatus').textContent = label ? `Current human label: ${label}` : 'No label selected.'; $('headingFields').hidden = label !== 'HEADING';
    $('headingStart').value = row.headingStart ?? ''; $('headingEnd').value = row.headingEnd ?? ''; $('headingText').textContent = row.headingText || 'Not selected.'; $('structuralType').value = row.structuralType || ''; $('level').value = row.level ?? ''; $('levelNotReviewed').checked = row.levelReviewStatus === 'LEVEL_NOT_REVIEWED'; $('parentStatus').value = row.parentReviewStatus || ''; $('reviewer').value = row.reviewer || ''; $('reviewNotes').value = row.reviewNotes || '';
    populateParents(row); $('parentGoldId').value = row.parentGoldId || ''; $('parentIdLabel').hidden = row.parentReviewStatus !== 'PARENT_REVIEWED';
    const errors = validateRow(row); $('currentErrors').className = `validation ${errors.length ? '' : 'ok'}`; $('currentErrors').innerHTML = errors.length ? `<strong>Not ready:</strong><ul>${errors.map(esc).map(error => `<li>${error}</li>`).join('')}</ul>` : 'Current occurrence passes local validation.';
    const reviewed = rows().filter(item => effectiveLabel(item)).length; $('progressSummary').textContent = `${reviewed}/${rows().length} reviewed · ${rows().length - reviewed} remaining`; $('progressBar').style.width = `${rows().length ? reviewed / rows().length * 100 : 0}%`; $('counts').innerHTML = LABELS.map(labelName => `<dt>${labelName}</dt><dd>${rows().filter(item => effectiveLabel(item) === labelName).length}</dd>`).join(''); $('finalizeReview').disabled = frozen; $('runCompare').hidden = !frozen; document.querySelectorAll('#reviewView input,#reviewView select,#reviewView textarea,.label,#useSelection,#clearHeadingFields').forEach(control => control.disabled = frozen);
  }
  function populateParents(row) { $('parentGoldId').innerHTML = '<option value="">Select heading…</option>' + rows().filter(candidate => candidate !== row && effectiveLabel(candidate) === 'HEADING' && candidate.goldHeadingId).map(candidate => `<option value="${esc(candidate.goldHeadingId)}">${esc(candidate.goldHeadingId)} · ${esc(candidate.headingText || '')}</option>`).join(''); }
  function mutate(callback) { if (frozen || !packet) return; callback(rows()[currentIndex]); render(); scheduleSave(); }
  function setLabel(label) { mutate(row => { row.adjudicatedLabel = label; if (label !== 'HEADING') ['headingStart','headingEnd','headingText','structuralType','level','levelReviewStatus','parentGoldId','parentReviewStatus','goldHeadingId'].forEach(field => row[field] = null); }); }
  async function refreshHeadingId(row) { if (row.adjudicatedLabel === 'HEADING' && Number.isInteger(row.headingStart) && Number.isInteger(row.headingEnd) && row.headingEnd > row.headingStart) row.goldHeadingId = await goldHeadingId(row); else row.goldHeadingId = null; }
  async function applySelection() { if (frozen) return; const selection = selectionOffsets($('rawText')); if (!selection || selection.start >= selection.end) return setNotice('Select a non-empty exact substring first.', true); const row = rows()[currentIndex]; row.headingStart = selection.start; row.headingEnd = selection.end; row.headingText = selection.selected; row.goldHeadingId = await goldHeadingId(row); render(); scheduleSave(); }
  function selectionOffsets(element) { const selection = window.getSelection(); if (!selection || !selection.rangeCount || !element.contains(selection.anchorNode) || !element.contains(selection.focusNode)) return null; const range = selection.getRangeAt(0), probe = document.createRange(); probe.selectNodeContents(element); const startRange = document.createRange(); startRange.setStart(probe.startContainer, probe.startOffset); startRange.setEnd(range.startContainer, range.startOffset); const endRange = document.createRange(); endRange.setStart(probe.startContainer, probe.startOffset); endRange.setEnd(range.endContainer, range.endOffset); const start = startRange.toString().length, end = endRange.toString().length; return { start: Math.min(start,end), end: Math.max(start,end), selected: selection.toString() }; }
  async function goldHeadingId(row) { const frame = [row.documentId,row.sourceId,row.headingStart,row.headingEnd].map(value => `${String(value ?? '').length}:${value ?? ''}|`).join(''); const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(frame)); return `gold-heading:${[...new Uint8Array(digest)].map(byte => byte.toString(16).padStart(2,'0')).join('')}`; }
  async function validatePacket() { if (!packet) return; const response = await fetch(`/api/accuracy99/review/session/${encodeURIComponent(sessionId)}/validate`, { method: 'POST' }); const result = await response.json(); setNotice(result.valid ? 'Gold validation PASS.' : `Gold validation BLOCKED: ${result.errors.join(', ')}`, !result.valid); }
  async function finalize() { if (!packet || frozen) return; await saveDraft(); const response = await fetch(`/api/accuracy99/review/session/${encodeURIComponent(sessionId)}/finalize`, { method: 'POST' }); const result = await response.json(); if (!response.ok) return setNotice(result.message || 'Finalize blocked.', true); frozen = true; packet.manifest.reviewStatus = 'GOLD_FROZEN'; render(); setNotice('GOLD_FROZEN created explicitly and is immutable.'); }
  async function compare() { if (!frozen) return; $('runCompare').disabled = true; setNotice('Running current pipeline and comparing independent metrics…'); try { const response = await fetch(`/api/accuracy99/review/session/${encodeURIComponent(sessionId)}/compare`, { method: 'POST' }); const result = await response.json(); if (!response.ok) throw new Error(result.message || 'Run & Compare failed.'); renderCompare(result); setNotice('Run & Compare complete · provider calls 0.'); } catch (error) { setNotice(error.message, true); } finally { $('runCompare').disabled = false; } }
  function renderCompare(result) { $('comparePanel').hidden = false; const metricNames = [['Heading precision','headingPrecision'],['Heading recall','headingRecall'],['F1','f1'],['Exact-span accuracy','exactSpanAccuracy'],['Level accuracy','levelAccuracy'],['Parent accuracy','parentAccuracy']]; $('compareMetrics').innerHTML = metricNames.map(([label,key]) => `<div class="metric"><strong>${pct(result.metrics[key])}</strong><small>${label}</small></div>`).join(''); $('compareRoute').textContent = `Runtime: Web → PipelineDocumentExtractionTool → DocumentProcessingService → AuthorityExtractionPipeline → ValidatedStructure · provider calls ${result.providerCalls}`; $('compareCounts').textContent = `TP ${result.counts.tp} · FP ${result.counts.fp} · FN ${result.counts.fn}`; $('compareDetails').textContent = JSON.stringify({tp:result.tp,fp:result.fp,fn:result.fn}, null, 2); }
  function pct(value) { return `${(Number(value || 0) * 100).toFixed(1)}%`; }
  function esc(value) { return String(value ?? '').replace(/[&<>"']/g, char => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[char])); }
  $('docxFile').addEventListener('change', event => event.target.files[0] && createSession(event.target.files[0]).catch(error => setNotice(error.message, true)));
  $('previous').onclick = () => { const visible = visibleIndices(); if (visible.length) currentIndex = visible[(visible.indexOf(currentIndex) - 1 + visible.length) % visible.length]; render(); }; $('next').onclick = () => { const visible = visibleIndices(); if (visible.length) currentIndex = visible[(visible.indexOf(currentIndex) + 1) % visible.length]; render(); }; $('nextUnreviewed').onclick = () => { filter = 'ALL'; const next = rows().findIndex((row,index) => index > currentIndex && !effectiveLabel(row)); currentIndex = next >= 0 ? next : rows().findIndex(row => !effectiveLabel(row)); if (currentIndex < 0) currentIndex = 0; render(); };
  document.querySelectorAll('.label').forEach(button => button.onclick = () => setLabel(button.dataset.label)); $('useSelection').onclick = applySelection; $('clearHeadingFields').onclick = () => mutate(row => ['headingStart','headingEnd','headingText','structuralType','level','levelReviewStatus','parentGoldId','parentReviewStatus','goldHeadingId'].forEach(field => row[field] = null)); $('validatePacket').onclick = validatePacket; $('finalizeReview').onclick = finalize; $('runCompare').onclick = compare;
  [['headingStart','headingStart'],['headingEnd','headingEnd'],['level','level'],['reviewer','reviewer'],['reviewNotes','reviewNotes']].forEach(([id,field]) => $(id).addEventListener('input', async event => { if (frozen || !packet) return; const row = rows()[currentIndex]; row[field] = event.target.value === '' ? null : (id === 'level' || id.startsWith('heading') ? Number(event.target.value) : event.target.value); if (field === 'level') row.levelReviewStatus = row.level == null ? null : 'REVIEWED'; if (field === 'headingStart' || field === 'headingEnd') { row.headingText = Number.isInteger(row.headingStart) && Number.isInteger(row.headingEnd) ? row.rawSourceText.slice(row.headingStart,row.headingEnd) : null; await refreshHeadingId(row); } render(); scheduleSave(); }));
  $('structuralType').onchange = event => mutate(row => row.structuralType = event.target.value || null); $('parentStatus').onchange = event => mutate(row => { row.parentReviewStatus = event.target.value || null; if (row.parentReviewStatus !== 'PARENT_REVIEWED') row.parentGoldId = null; }); $('parentGoldId').onchange = event => mutate(row => row.parentGoldId = event.target.value || null); $('levelNotReviewed').onchange = event => mutate(row => { row.levelReviewStatus = event.target.checked ? 'LEVEL_NOT_REVIEWED' : null; row.level = null; });
  $('filter').onchange = event => { filter = event.target.value; const visible = visibleIndices(); currentIndex = visible[0] ?? 0; render(); };
  document.addEventListener('keydown', event => { if (event.target.matches('input,textarea,select') || frozen) return; const key = event.key.toLowerCase(); if (LABELS.find(label => label[0].toLowerCase() === key)) setLabel(LABELS.find(label => label[0].toLowerCase() === key)); else if (event.key === 'ArrowLeft') $('previous').click(); else if (event.key === 'ArrowRight') $('next').click(); });
  if (sessionId) loadSession(sessionId).catch(error => setNotice(error.message, true));
})();
