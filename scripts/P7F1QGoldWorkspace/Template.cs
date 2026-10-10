internal static class Template
{
    // Self-contained offline review page. No network access, no external scripts, fonts or styles.
    // State (edits, approval) lives only in this browser (localStorage) and is exported as JSON + command lines.
    public const string Html = """
<!doctype html>
<html lang="vi">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>__TITLE__</title>
<style>
:root { --bg:#ffffff; --fg:#1d1d1f; --muted:#6b6b70; --line:#d9d9de; --panel:#f6f6f8; --e:#1a9850; --r:#2166ac; --o:#969696; --flag:#e67800; --user:#762a83; --sel:#ffd400; --chg:#c2185b; }
@media (prefers-color-scheme: dark) { :root { --bg:#16161a; --fg:#ececf1; --muted:#a0a0aa; --line:#33333b; --panel:#1f1f25; } }
* { box-sizing: border-box; }
body { margin:0; font:14px/1.45 system-ui, "Segoe UI", Arial, sans-serif; background:var(--bg); color:var(--fg); }
header { padding:10px 16px; border-bottom:1px solid var(--line); background:var(--panel); position:sticky; top:0; z-index:5; }
header h1 { font-size:17px; margin:0 0 4px; }
.meta { color:var(--muted); font-size:12px; word-break:break-all; }
.bar { display:flex; flex-wrap:wrap; gap:10px; align-items:center; margin-top:8px; }
.bar label { font-size:13px; }
.badge { display:inline-block; padding:1px 8px; border-radius:10px; font-size:12px; font-weight:600; color:#fff; }
.b-draft { background:#888; } .b-ok { background:var(--e); } .b-chg { background:var(--chg); }
main { display:grid; grid-template-columns: minmax(0, 1.15fr) minmax(0, 1fr); gap:0; height: calc(100vh - 118px); }
@media (max-width: 1000px) { main { grid-template-columns: 1fr; height:auto; } .pane { height:auto !important; } }
.pane { overflow:auto; height:100%; padding:10px 12px; }
.pane + .pane { border-left:1px solid var(--line); }
.pagebox { margin:0 0 18px; }
.pagebox h2 { font-size:14px; margin:4px 0 6px; }
.pagewrap { position:relative; border:1px solid var(--line); background:#fff; }
.pagewrap img { display:block; width:100%; height:auto; }
.pagewrap svg { position:absolute; inset:0; width:100%; height:100%; }
svg rect.box { cursor:pointer; }
svg text { font: 7px system-ui, Arial, sans-serif; fill:#fff; pointer-events:none; }
table { border-collapse:collapse; width:100%; font-size:12.5px; }
th, td { border-bottom:1px solid var(--line); padding:4px 5px; vertical-align:top; text-align:left; }
th { position:sticky; top:0; background:var(--panel); z-index:2; }
tr.sel td { background: color-mix(in srgb, var(--sel) 35%, transparent); }
tr.changed td:first-child { border-left:4px solid var(--chg); }
td.txt { max-width:340px; word-break:break-word; }
td.why { color:var(--muted); font-size:11.5px; max-width:260px; }
.lab { font-weight:700; } .lab-E { color:var(--e); } .lab-R { color:var(--r); } .lab-O { color:var(--o); } .lab-X { color:var(--flag); }
select, input[type=text], textarea, button { font:inherit; color:inherit; background:var(--bg); border:1px solid var(--line); border-radius:6px; padding:3px 6px; }
button { cursor:pointer; } button.primary { background:var(--e); color:#fff; border-color:var(--e); } button:disabled { opacity:.5; cursor:not-allowed; }
.side { border:1px solid var(--line); border-radius:8px; padding:10px; margin:0 0 12px; background:var(--panel); }
.side h3 { margin:0 0 6px; font-size:14px; }
textarea { width:100%; min-height:90px; font-family: ui-monospace, Consolas, monospace; font-size:12px; }
.legend span { display:inline-block; margin-right:10px; font-size:12px; }
.sw { display:inline-block; width:10px; height:10px; border:2px solid; margin-right:4px; vertical-align:-1px; }
.small { font-size:12px; color:var(--muted); }
</style>
</head>
<body>
<header>
  <h1 id="title"></h1>
  <div class="meta" id="meta"></div>
  <div class="bar">
    <span id="status"></span>
    <label>Zoom <input id="zoom" type="range" min="60" max="400" step="10" value="100"> <span id="zv">100%</span></label>
    <label>Nhãn <select id="flabel"><option value="">tất cả</option><option>E</option><option>R</option><option>O</option><option value="X">loại trừ</option></select></label>
    <label>Lọc <select id="fflag"><option value="">tất cả</option><option value="flag">đang gắn cờ</option><option value="changed">đã sửa</option><option value="user">quyết định của người dùng</option></select></label>
    <label>Trang <select id="fpage"><option value="">tất cả</option></select></label>
    <label>Tìm <input id="fq" type="text" size="16" placeholder="alias hoặc chữ"></label>
    <label><input id="showO" type="checkbox" checked> hiện khung OTHER</label>
    <span class="legend"><span><i class="sw" style="border-color:var(--e)"></i>E</span><span><i class="sw" style="border-color:var(--r)"></i>R</span><span><i class="sw" style="border-color:var(--o)"></i>O</span><span><i class="sw" style="border-color:var(--flag)"></i>cờ</span><span><i class="sw" style="border-color:var(--user)"></i>đã quyết</span><span><i class="sw" style="border-color:var(--chg)"></i>bạn vừa sửa</span></span>
  </div>
</header>
<main>
  <section class="pane" id="pages"></section>
  <section class="pane">
    <div class="side">
      <h3>Duyệt tài liệu</h3>
      <div class="small">Chỉ duyệt khi đã đối chiếu <b>toàn bộ</b> occurrence (kể cả OTHER) với ảnh trang. Mọi sửa nhãn sau khi duyệt sẽ huỷ phê duyệt.</div>
      <div style="margin:6px 0"><label><input id="allReviewed" type="checkbox"> Tôi đã rà soát toàn bộ <span id="nrows"></span> occurrence của tài liệu này trên ảnh trang</label></div>
      <div style="margin:6px 0"><label>Người duyệt <input id="reviewer" type="text" size="18"></label> <label>Ghi chú <input id="note" type="text" size="28"></label></div>
      <button id="approve" class="primary">APPROVE</button> <button id="revoke">Huỷ phê duyệt</button>
      <h3 style="margin-top:10px">Xuất / nhập</h3>
      <button id="export">Tải JSON quyết định</button> <label class="small">Nhập JSON đã xuất <input id="import" type="file" accept="application/json"></label>
      <div class="small" style="margin-top:6px">Lệnh tương đương (có thể dán vào cuộc trò chuyện):</div>
      <textarea id="commands" readonly></textarea>
      <div class="small" id="log"></div>
    </div>
    <table>
      <thead><tr><th>#</th><th>tr.</th><th>alias</th><th>chữ (parser)</th><th>font</th><th>nháp</th><th>hiện tại</th><th>lý do / ghi chú</th></tr></thead>
      <tbody id="rows"></tbody>
    </table>
  </section>
</main>
<script type="application/json" id="data">__DATA__</script>
<script>
"use strict";
const D = JSON.parse(document.getElementById("data").textContent);
const KEY = "p7f1q-gold-" + D.id + "-" + D.draftSha256;
const SVGNS = "http://www.w3.org/2000/svg";
let S = { edits: [], approval: null };
try { const raw = localStorage.getItem(KEY); if (raw) S = JSON.parse(raw); } catch (e) { /* storage unavailable: state lives in this tab only */ }
function save() { try { localStorage.setItem(KEY, JSON.stringify(S)); } catch (e) {} render(); }
const byAlias = new Map(D.rows.map(r => [r.alias, r]));
function current(r) { let v = r.draft === null ? "X" : r.draft; for (const e of S.edits) if (e.alias === r.alias) v = e.newLabel; return v; }
function changed(r) { return S.edits.some(e => e.alias === r.alias); }
function colour(lab) { return lab === "E" ? "var(--e)" : lab === "R" ? "var(--r)" : lab === "X" ? "var(--flag)" : "var(--o)"; }
function outline(r) { return changed(r) ? "var(--chg)" : r.flag === "USER_DECIDED" ? "var(--user)" : r.flag !== "NONE" ? "var(--flag)" : colour(current(r)); }
function esc(s) { return String(s ?? "").replace(/[&<>"]/g, c => ({ "&":"&amp;", "<":"&lt;", ">":"&gt;", '"':"&quot;" }[c])); }

document.getElementById("title").textContent = "Gold review " + D.id + " — " + D.sourceKey.split("/").pop();
document.getElementById("meta").textContent = "source SHA-256 " + D.sourceSha256 + " · alias universe " + D.sourceAliasUniverseSha256 + " · draft " + D.draftFile + " (" + D.draftSha256 + ") · " + D.draftStatus + " · DRAFT, không phải Gold đã duyệt";
document.getElementById("nrows").textContent = D.rows.length;
const fpage = document.getElementById("fpage");
for (const p of D.pages) { const o = document.createElement("option"); o.value = p.page; o.textContent = p.page + " (" + p.stratum + ")"; fpage.appendChild(o); }

// Pages with vector overlays (scale with zoom; text stays readable)
const pagesEl = document.getElementById("pages");
const rectByAlias = new Map();
for (const p of D.pages) {
  const box = document.createElement("div"); box.className = "pagebox"; box.dataset.page = p.page;
  box.innerHTML = "<h2>Trang " + p.page + " — " + esc(p.stratum) + "</h2>";
  const wrap = document.createElement("div"); wrap.className = "pagewrap";
  const img = document.createElement("img"); img.src = p.image; img.alt = "Trang " + p.page; wrap.appendChild(img);
  const svg = document.createElementNS(SVGNS, "svg"); svg.setAttribute("viewBox", "0 0 " + p.widthPt + " " + p.heightPt); svg.setAttribute("preserveAspectRatio", "none");
  for (const r of D.rows.filter(r => r.page === p.page && r.bbox)) {
    const [l, rr, b, t] = r.bbox;
    const rect = document.createElementNS(SVGNS, "rect");
    rect.setAttribute("class", "box"); rect.setAttribute("x", l); rect.setAttribute("y", p.heightPt - t); rect.setAttribute("width", Math.max(0.5, rr - l)); rect.setAttribute("height", Math.max(0.5, t - b));
    const title = document.createElementNS(SVGNS, "title"); rect.appendChild(title);
    rect.addEventListener("click", () => select(r.alias, true));
    const tagBg = document.createElementNS(SVGNS, "rect"); const tag = document.createElementNS(SVGNS, "text");
    const label = String(r.no); const w = 3.9 * label.length + 2;
    tagBg.setAttribute("x", Math.max(0, l - w - 1)); tagBg.setAttribute("y", p.heightPt - t); tagBg.setAttribute("width", w); tagBg.setAttribute("height", 8);
    tag.setAttribute("x", Math.max(0, l - w - 1) + 1); tag.setAttribute("y", p.heightPt - t + 6.6); tag.textContent = label;
    svg.appendChild(rect); svg.appendChild(tagBg); svg.appendChild(tag);
    rectByAlias.set(r.alias, { rect, tagBg, tag, title });
  }
  wrap.appendChild(svg); box.appendChild(wrap); pagesEl.appendChild(box);
}
const zoom = document.getElementById("zoom");
function applyZoom() { document.getElementById("zv").textContent = zoom.value + "%"; for (const w of document.querySelectorAll(".pagewrap")) w.style.width = zoom.value + "%"; }
zoom.addEventListener("input", applyZoom);

const tbody = document.getElementById("rows");
const trByAlias = new Map();
for (const r of D.rows) {
  const tr = document.createElement("tr"); tr.dataset.alias = r.alias;
  tr.innerHTML = "<td>" + r.no + "</td><td>" + r.page + "</td><td>" + esc(r.alias) + "</td><td class='txt'>" + esc(r.text) + "</td><td>" + esc(r.font) +
    "</td><td class='lab lab-" + (r.draft ?? "X") + "'>" + (r.draft ?? "loại trừ") + "</td><td class='cur'></td><td class='why'></td>";
  tr.addEventListener("click", ev => { if (!(ev.target instanceof HTMLSelectElement)) select(r.alias, false); });
  tbody.appendChild(tr); trByAlias.set(r.alias, tr);
}

function setLabel(r, value) {
  const old = current(r);
  if (value === old) return;
  const reason = window.prompt("Lý do đổi nhãn " + r.alias + " (" + old + " → " + value + "):\n" + r.text, "");
  if (!reason || !reason.trim()) { render(); return; }
  S.edits.push({ alias: r.alias, text: r.text, previousLabel: old, newLabel: value, reason: reason.trim(), at: new Date().toISOString() });
  if (S.approval) { S.approval = null; logLine("Phê duyệt đã bị huỷ vì có sửa nhãn sau khi duyệt."); }
  save();
}

let selected = null;
function select(alias, scrollRow) {
  if (selected) trByAlias.get(selected)?.classList.remove("sel");
  selected = alias; const tr = trByAlias.get(alias); tr?.classList.add("sel");
  if (scrollRow) tr?.scrollIntoView({ block: "center" });
  else { const g = rectByAlias.get(alias); g?.rect.scrollIntoView({ block: "center", inline: "center" }); }
  render();
}

function visible(r) {
  const fl = document.getElementById("flabel").value, ff = document.getElementById("fflag").value, fp = document.getElementById("fpage").value, q = document.getElementById("fq").value.trim().toLowerCase();
  if (fl && current(r) !== fl) return false;
  if (ff === "flag" && !(r.flag === "REVIEW_FOCUS" || r.flag === "DECISION_NEEDED")) return false;
  if (ff === "changed" && !changed(r)) return false;
  if (ff === "user" && r.flag !== "USER_DECIDED") return false;
  if (fp && String(r.page) !== fp) return false;
  if (q && !(r.alias.toLowerCase().includes(q) || r.text.toLowerCase().includes(q))) return false;
  return true;
}

function render() {
  const showO = document.getElementById("showO").checked;
  for (const r of D.rows) {
    const cur = current(r), tr = trByAlias.get(r.alias);
    tr.style.display = visible(r) ? "" : "none";
    tr.classList.toggle("changed", changed(r));
    const cell = tr.querySelector(".cur");
    if (r.draft === null) cell.innerHTML = "<span class='lab lab-X'>loại trừ</span>";
    else if (!cell.querySelector("select")) {
      const sel = document.createElement("select");
      for (const v of ["E", "R", "O"]) { const o = document.createElement("option"); o.value = v; o.textContent = v; sel.appendChild(o); }
      sel.addEventListener("change", () => setLabel(r, sel.value)); cell.appendChild(sel);
    }
    const sel = cell.querySelector("select"); if (sel) { sel.value = cur; sel.className = "lab lab-" + cur; }
    const edits = S.edits.filter(e => e.alias === r.alias);
    tr.querySelector(".why").innerHTML = esc(r.rationale) + (r.flag !== "NONE" ? " <b>[" + esc(r.flag) + "]</b>" : "") +
      edits.map(e => "<br><span style='color:var(--chg)'>✎ " + esc(e.previousLabel) + "→" + esc(e.newLabel) + ": " + esc(e.reason) + "</span>").join("");
    const g = rectByAlias.get(r.alias);
    if (g) {
      const col = colour(cur), out = outline(r), sel2 = selected === r.alias;
      g.rect.setAttribute("fill", col); g.rect.setAttribute("fill-opacity", cur === "O" ? 0.10 : 0.22);
      g.rect.setAttribute("stroke", sel2 ? "var(--sel)" : out); g.rect.setAttribute("stroke-width", sel2 ? 2.2 : (r.flag !== "NONE" || changed(r) ? 1.2 : 0.6));
      g.tagBg.setAttribute("fill", out); g.title.textContent = "#" + r.no + " " + r.alias + " [" + cur + "] " + r.text;
      const hide = !showO && cur === "O" && !sel2 && r.flag === "NONE" && !changed(r);
      for (const el of [g.rect, g.tagBg, g.tag]) el.style.display = hide ? "none" : "";
    }
  }
  const counts = { E: 0, R: 0, O: 0, X: 0 }; for (const r of D.rows) counts[current(r)]++;
  const st = document.getElementById("status");
  st.innerHTML = (S.approval ? "<span class='badge b-ok'>ĐÃ DUYỆT (cục bộ) bởi " + esc(S.approval.by) + "</span>" : "<span class='badge b-draft'>CHƯA DUYỆT</span>") +
    (S.edits.length ? " <span class='badge b-chg'>" + S.edits.length + " sửa</span>" : "") +
    " <span class='small'>E " + counts.E + " · R " + counts.R + " · O " + counts.O + " · loại trừ " + counts.X + " · tổng " + D.rows.length + "</span>";
  document.getElementById("approve").disabled = !(document.getElementById("allReviewed").checked && document.getElementById("reviewer").value.trim());
  document.getElementById("commands").value = commands().join("\n");
}

function commands() {
  const out = [];
  for (const e of S.edits) out.push(D.id + " " + e.alias + " -> " + e.newLabel + " because " + e.reason.replace(/\s+/g, " "));
  if (S.approval) out.push("APPROVE " + D.id + " by " + S.approval.by + (S.approval.note ? " because " + S.approval.note : ""));
  return out;
}
function logLine(s) { document.getElementById("log").textContent = s; }

document.getElementById("approve").addEventListener("click", () => {
  S.approval = { approved: true, by: document.getElementById("reviewer").value.trim(), note: document.getElementById("note").value.trim(),
    reviewedAllOccurrences: true, occurrences: D.rows.length, editsAtApproval: S.edits.length, at: new Date().toISOString() };
  save(); logLine("Đã ghi phê duyệt cục bộ. Hãy xuất JSON hoặc gửi lệnh APPROVE để áp dụng.");
});
document.getElementById("revoke").addEventListener("click", () => { S.approval = null; save(); logLine("Đã huỷ phê duyệt."); });
for (const id of ["allReviewed", "reviewer", "flabel", "fflag", "fpage", "fq", "showO"]) document.getElementById(id).addEventListener("input", render);

document.getElementById("export").addEventListener("click", () => {
  const out = { version: "P7_F1Q_HELDOUT_GOLD_REVIEW_DECISIONS_V1", id: D.id, sourceKey: D.sourceKey, sourceSha256: D.sourceSha256,
    sourceAliasUniverseSha256: D.sourceAliasUniverseSha256, draftFile: D.draftFile, draftSha256: D.draftSha256, exportedAt: new Date().toISOString(),
    edits: S.edits, approval: S.approval, commands: commands(),
    finalLabels: D.rows.map(r => ({ alias: r.alias, label: current(r) === "X" ? null : current(r), excluded: current(r) === "X" })) };
  const blob = new Blob([JSON.stringify(out, null, 2)], { type: "application/json" });
  const a = document.createElement("a"); a.href = URL.createObjectURL(blob); a.download = D.id + ".gold-review-decisions.json"; a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 2000);
});
document.getElementById("import").addEventListener("change", async ev => {
  const f = ev.target.files[0]; if (!f) return;
  try {
    const x = JSON.parse(await f.text());
    if (x.id !== D.id || x.draftSha256 !== D.draftSha256) { alert("File không khớp tài liệu hoặc bản nháp này."); return; }
    if (!x.edits.every(e => byAlias.has(e.alias))) { alert("File chứa alias không thuộc tài liệu."); return; }
    S = { edits: x.edits, approval: x.approval }; save(); logLine("Đã nhập " + x.edits.length + " sửa.");
  } catch (e) { alert("Không đọc được JSON: " + e.message); }
});
applyZoom(); render();
</script>
</body>
</html>
""";
}
