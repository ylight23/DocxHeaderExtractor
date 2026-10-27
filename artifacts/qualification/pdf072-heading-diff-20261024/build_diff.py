"""Compare exported headings by source alias, keeping grouping and hierarchy changes distinct."""
import hashlib
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
OUT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT / "tools/qualification"))
from score_serial_calls import boundaries

read = lambda p: json.loads(p.read_text(encoding="utf-8-sig"))
OLD = ROOT / "artifacts/qualification/pdf072-full-page15-20261023"
CURRENT = ROOT / "artifacts/qualification/pdf072-adaptive-production-20261024"
TRIAL = ROOT / "artifacts/qualification/pdf072-parent-chain-repair-20261024/full"
facts = read(ROOT / "artifacts/qualification/pdf072-membership-audit-20261024/evidence/source-facts.json")
pages = {v["Alias"]: v["Page"] for v in facts["lines"]}

def exported(folder, file, old=False):
    rows = read(folder / file)
    if old:
        groups = read(folder / "selection.json")["groups"]
        headings = [dict(alias=h["Alias"], page=int(h["Page"]), title=h["Title"],
                         level=h["Level"] if isinstance(h["Level"], int) else None,
                         parent=h["Parent"], placement=h["Placement"], ordinal=i+1) for i, h in enumerate(rows)]
    else:
        _, groups = boundaries(read(folder / "run-result.json"))
        headings = [dict(alias=h["alias"], page=h["page"], title=h["title"], level=h["level"],
                         parent=h["parentAlias"], placement=h["placementResolution"], ordinal=i+1) for i, h in enumerate(rows)]
    by_alias = {h["alias"]: h for h in headings}
    groups = {g[0]: list(g) for g in groups}
    assert set(by_alias) == set(groups)
    for alias, h in by_alias.items():
        h["sourceParts"] = groups[alias]
    return by_alias

old = exported(OLD, "all-heading-results-with-placement.json", old=True)
current = exported(CURRENT, "headings.json")
run = read(TRIAL / "run-result.json")
_, trial_groups = boundaries(run)
decisions = {d["SourceId"]: d for d in run["placementExecution"]["Decisions"]}
trial = {}
for i, h in enumerate(run["outline"]["headings"]):
    d = decisions[h["sourceId"]]
    a = d["Alias"]
    trial[a] = dict(alias=a, page=pages[a], title=h["text"],
                    level=h.get("level") if d["Resolution"] in ["model-root", "model-parent-relation"] else None,
                    rawOutputLevel=h.get("level"), parent=d.get("ProposedParentAlias"),
                    placement=d["Resolution"], ordinal=i+1,
                    sourceParts=next(list(g) for g in trial_groups if g[0] == a))

def compare(before, after):
    owner_old = {p: a for a, h in before.items() for p in h["sourceParts"]}
    owner_new = {p: a for a, h in after.items() for p in h["sourceParts"]}
    result = []
    for a in sorted(set(before) | set(after)):
        b, n = before.get(a), after.get(a)
        changes = []
        if b is None:
            changes.append("tách thành heading riêng" if a in owner_old else "mới được chọn")
        elif n is None:
            changes.append("gộp vào heading khác" if a in owner_new else "không còn được chọn")
        else:
            for key, label in [("title", "nội dung"), ("sourceParts", "nhóm source"),
                               ("level", "level"), ("parent", "parent đề xuất"), ("placement", "placement")]:
                if b[key] != n[key]:
                    changes.append(label)
        result.append(dict(alias=a, page=(n or b)["page"], before=b, after=n, changes=changes,
                           beforeGroupOwner=owner_old.get(a), afterGroupOwner=owner_new.get(a)))
    shared = [v for v in result if v["before"] and v["after"]]
    return dict(beforeCount=len(before), afterCount=len(after), unionCount=len(result),
                exactUnchanged=sum(not v["changes"] for v in result),
                onlyBefore=[v["alias"] for v in result if v["before"] and not v["after"]],
                onlyAfter=[v["alias"] for v in result if v["after"] and not v["before"]],
                titleChanges=sum(v["before"]["title"] != v["after"]["title"] for v in shared),
                groupChanges=sum(v["before"]["sourceParts"] != v["after"]["sourceParts"] for v in shared),
                levelChanges=sum(v["before"]["level"] != v["after"]["level"] for v in shared),
                parentChanges=sum(v["before"]["parent"] != v["after"]["parent"] for v in shared),
                placementChanges=sum(v["before"]["placement"] != v["after"]["placement"] for v in shared), rows=result)

comparisons = {"oldVsOpenAdaptive": compare(old, current), "oldVsLatestUnpromotedTrial": compare(old, trial)}
inputs = [OLD / "all-heading-results-with-placement.json", OLD / "selection.json",
          CURRENT / "headings.json", CURRENT / "run-result.json", TRIAL / "run-result.json"]
artifact = dict(inputs=[dict(path=str(p.relative_to(ROOT)), sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in inputs],
                comparisons=comparisons,
                note="Aliases identify source occurrences. A missing standalone alias may remain selected inside another group. Parent columns are model proposals; unresolved proposals are not accepted ancestry. Gold has no hierarchy capability.")
(OUT / "diff.json").write_text(json.dumps(artifact, indent=2, ensure_ascii=False), encoding="utf-8")

def esc(s):
    return str(s).replace("|", "&#124;").replace("\n", " ")

def transition(b, n, key):
    value = lambda h: "∅" if h is None else "—" if h[key] is None else str(h[key])
    x, y = value(b), value(n)
    return esc(x if x == y else x + " → " + y)

text = ["# Diff từng heading PDF072", "",
        "Bản cũ: full-page15 ngày 23/10, 42 heading. Bản đang mở: adaptive-production ngày 24/10, 41 heading. "
        "Lượt thử mới nhất: source-bound + chain-repair, 40 heading, chưa promote. Hai so sánh được tách riêng.", "",
        "Đối chiếu theo alias nguồn, không theo số thứ tự. `∅` = không có heading riêng trong file; `—` = chưa có level. "
        "Parent là đề xuất của model; phải đọc cùng placement. `unresolved` chưa tạo quan hệ cha–con hợp lệ. "
        "Diff mô tả khác biệt, không chấm độ chính xác hierarchy vì Gold không có capability đó.", ""]
for name, title in [("oldVsOpenAdaptive", "1. Bản cũ → file adaptive đang mở"),
                    ("oldVsLatestUnpromotedTrial", "2. Bản cũ → lượt thử mới nhất, chưa promote")]:
    c = comparisons[name]
    text += ["## " + title, "", f"{c['beforeCount']} → {c['afterCount']} heading; "
             f"{c['exactUnchanged']} heading giữ nguyên hoàn toàn. Trong các alias chung: "
             f"{c['titleChanges']} đổi nội dung, {c['groupChanges']} đổi nhóm source, "
             f"{c['levelChanges']} đổi level, {c['parentChanges']} đổi parent đề xuất, "
             f"{c['placementChanges']} đổi trạng thái placement. Các số đếm có thể trùng một heading.", "",
             "| Alias | Trang | Heading: cũ → sau | Thay đổi | Level | Parent đề xuất | Placement |",
             "|---|---:|---|---|---|---|---|"]
    for r in c["rows"]:
        b, n = r["before"], r["after"]
        if b and n and b["title"] == n["title"]:
            title_text = esc(b["title"])
        else:
            title_text = "Cũ: " + esc(b["title"] if b else "∅") + "<br>Sau: " + esc(n["title"] if n else "∅")
        changes = ", ".join(r["changes"]) or "giữ nguyên"
        if b is None and r["beforeGroupOwner"]:
            changes += " từ " + r["beforeGroupOwner"]
        if n is None and r["afterGroupOwner"]:
            changes += " " + r["afterGroupOwner"]
        text.append("| " + " | ".join([r["alias"], str(r["page"]), title_text, changes,
                                         transition(b, n, "level"), transition(b, n, "parent"), transition(b, n, "placement")]) + " |")
    text += ["", "### Những thay đổi về lựa chọn và grouping", ""]
    for r in c["rows"]:
        b, n = r["before"], r["after"]
        if not b or not n or b["sourceParts"] != n["sourceParts"]:
            text.append(f"- `{r['alias']}`: source parts cũ " + esc(b["sourceParts"] if b else "∅") +
                        " → sau " + esc(n["sourceParts"] if n else "∅") + ". " + ", ".join(r["changes"]) + ".")
    text += [""]
text += ["Dữ liệu đầy đủ, số thứ tự và hash input: [diff.json](diff.json). "
         "Script chỉ đọc kết quả và ghi báo cáo, không sửa các file heading đầu vào."]
(OUT / "diff.md").write_text("\n".join(text) + "\n", encoding="utf-8")
print(json.dumps({k: {x: y for x, y in v.items() if x != "rows"} for k, v in comparisons.items()}, ensure_ascii=True))
