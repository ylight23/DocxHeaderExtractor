"""SRC044_SOURCE_ONLY_GOLD_REVIEW_V1 - reviewer's working tool.

Reads only the original PDF's structured-lane atoms with their layout facts (atom-layout-facts.tsv). Never the
engine's blind proposals, never SRC-044's Gold file or any count it holds, never a converted DOCX. Emits the
reviewer's candidate list: HEADING, NON_HEADING (only occurrences that are set apart), AMBIGUOUS - each with
sourceParts. Decisions the user took on SRC-042 (the next year's IDA report) and SRC-041, DOC-0133, and the frozen
financial policy are applied as precedent and say so.

This report sets its hierarchy one step smaller than SRC-042 (sections 14pt; the lowest subsections 10pt bold; most
note titles 10pt bold, under 11pt note sub-headings), so levels are read by function - a standalone bold label at the
text margin over its own prose or list - not by a size taken from another document.
"""
import json, re, sys, collections

SP = sys.argv[1]
OUT = sys.argv[2]
R = [l.split('\t') for l in open(SP + '/atom-layout-facts.tsv', encoding='utf-8').read().split('\n') if l]
A = {r[0]: r for r in R}
IDX = {r[0]: i for i, r in enumerate(R)}
page = lambda a: int(A[a][1])
text = lambda a: A[a][10].strip()
size = lambda a: float(A[a][4])
bold = lambda a: float(A[a][5]) >= 0.5
italic = lambda a: float(A[a][6]) >= 0.5
left = lambda a: float(A[a][7])
ROWSEG = collections.Counter((r[1], r[2]) for r in R)
single = lambda a: ROWSEG[(A[a][1], A[a][2])] == 1
alone = lambda a: single(a) or (A[a][3] == '0' and all(float(r[7]) > 300 for r in R
                                                         if r[1] == A[a][1] and r[2] == A[a][2] and r[0] != a))
CAPTION_RX = re.compile(r'(Table|Figure|Box)\s+[A-Z]?\d+(?:\.\d+)*')

items = []
claimed = set()


def whole(a):
    return {"sourceAlias": a, "selectionMode": "WHOLE_ALIAS", "verbatimText": None, "occurrence": None}


def add(section, aliases, verdict, pattern, reason, axes):
    for a in aliases:
        assert a not in claimed, a
        claimed.add(a)
    items.append({"section": section, "parts": [whole(a) for a in aliases], "text": ' '.join(text(a) for a in aliases),
                  "verdict": verdict, "pattern": pattern, "reason": reason, "axes": axes, "page": page(aliases[0])})


def ax(functions, primary, scope, roles=("REGION_OPENER",), title="TITLE", info=None):
    return {"semanticFunctions": list(functions), "primaryFunction": primary, "scope": scope,
            "occurrenceRoles": list(roles), "titleRelation": title, "informationType": info}


SEC = ax(["STRUCTURE"], "STRUCTURE", "SECTION")
SECT = ax(["IDENTITY", "STRUCTURE"], "STRUCTURE", "SECTION")
PART = ax(["IDENTITY", "STRUCTURE"], "IDENTITY", "DOCUMENT_PART")
NOTES_PART = ax(["IDENTITY", "STRUCTURE"], "STRUCTURE", "DOCUMENT_PART")
NOTE = ax(["IDENTITY", "STRUCTURE"], "STRUCTURE", "NOTE")
STATEMENT = ax(["IDENTITY"], "IDENTITY", "FINANCIAL_STATEMENT")
TOC = ax(["IDENTITY", "STRUCTURE"], "IDENTITY", "TOC")
DOC = ax(["IDENTITY"], "IDENTITY", "DOCUMENT")
EMB = ax(["IDENTITY"], "IDENTITY", "EMBEDDED_ARTIFACT")
CAPTION = ax(["IDENTITY"], "IDENTITY", "TABLE", ("CAPTION",))
CHART = ax(["INFORMATION"], "INFORMATION", "TABLE", ("LOCAL_LABEL",), "NONE")
NAV = ax(["STRUCTURE"], "STRUCTURE", "TOC", ("NAVIGATION",), "NONE")
FURN = ax(["INFORMATION"], "INFORMATION", "DOCUMENT", ("PAGE_FURNITURE",), "NONE")
META = lambda info, scope="DOCUMENT": ax(["INFORMATION"], "INFORMATION", scope, ("METADATA",), "NONE", info)
P33 = "precedent DOC-0133 (user-decided 2026-09-25)"
P41 = "precedent SRC-041 (user-decided 2026-09-25)"
P42 = "precedent SRC-042 (user-decided 2026-09-25)"
POL = "FINANCIAL_PROCUREMENT_HEADING_POLICY_V1"
H, N, Q = "HEADING", "NON_HEADING", "AMBIGUOUS"


def section_of(p):
    return "FRONT" if p <= 4 else "MDA" if p <= 58 else "FS_PART" if p <= 67 else "STATEMENTS" if p <= 81 else "NOTES"


# ---------------- cover (p1) ----------------
add("FRONT", ["L0000:S0"], N, "COVER_BLOCK", "issuer above the cover title: " + P42, META("ISSUER_METADATA"))
add("FRONT", ["L0001:S0", "L0002:S0", "L0003:S0"], H, "COVER_BLOCK", "the cover title, one claim of three lines: " + P42, DOC)
add("FRONT", ["L0004:S0"], N, "COVER_BLOCK", "reporting date under the cover title: " + P42, META("TEMPORAL_METADATA"))

# ---------------- MD&A contents pages (p3-4) ----------------
add("FRONT", ["L0005:S0"], H, "A3_PRECEDENT_PART_LABEL_ON_CONTENTS_PAGE",
    "the MD&A label at the top of its contents page opens the part: " + P41 + " (A3); " + P42, PART)
add("FRONT", ["L0043:S0"], N, "RUNNING_HEADER", "the same line atop the next contents page: " + P41 + " (A3)", FURN)
for a in ["L0006:S0", "L0044:S0", "L0045:S0", "L0074:S0", "L0091:S0"]:
    add("FRONT", [a], H, "TOC_OPENER", "opens a contents list or sub-list (Contents / Tables / Figures / Box): " + P42, TOC)

# ---------------- explicit occurrence decisions by precedent ----------------
add("MDA", ["L0950:S0"], N, "S042_A1_PRECEDENT_BOX_TITLE",
    "8pt bold title of the box the section 'Financing Principles' (just above) refers to ('described in Box 1'): an object title, not a second opener of that region: " + P42 + " (S042_A1, revised)",
    ax(["IDENTITY"], "IDENTITY", "EMBEDDED_ARTIFACT", ("CAPTION",)))
add("NOTES", ["L3361:S0"], N, "CAPTION_CONTINUATION", "second line of the Table B2 caption", CAPTION)

# ---------------- body headings, read by function ----------------
WRAPS = {"L3182:S0": ["L3183:S0"]}
TAIL = {t for v in WRAPS.values() for t in v}


def heading_aliases(a):
    return [a] + WRAPS.get(a, [])


for r in R:
    a = r[0]
    if a in claimed or a in TAIL:
        continue
    p, t, s = page(a), text(a), size(a)
    if p < 5 or 58 < p < 82 or not bold(a) or not alone(a) or left(a) > 80 or s < 10.0:
        continue
    if CAPTION_RX.match(t):
        continue
    sec = section_of(p)
    if t.startswith("Section ") and s >= 14.0:
        add(sec, heading_aliases(a), H, "MDA_SECTION_TITLE", "MD&A section title (14pt here): " + P41 + "; " + P42, SECT)
    elif t == "Appendix":
        add(sec, heading_aliases(a), H, "APPENDIX_TITLE", "title opening the appendix: " + P41, PART)
    elif t.startswith("NOTE ") or t == "PURPOSE AND AFFILIATED ORGANIZATIONS":
        add(sec, heading_aliases(a), H, "NOTE_TITLE",
            f"note title opening a complete note ({s:g}pt here - by function, whatever its size against the sub-headings): " + POL + "; " + P41, NOTE)
    elif s in (12.0, 11.0, 10.0):
        add(sec, heading_aliases(a), H, "NOTE_SUBHEADING" if sec == "NOTES" else "MDA_SUBHEADING",
            f"{s:g}pt bold standalone label at the margin over its own prose or list: " + P33 + " (P6); " + P41 + "; " + P42, SEC)

# ---------------- numbered captions: false by the caption rule ----------------
for r in R:
    a = r[0]
    if a in claimed or page(a) < 5 or not bold(a) or size(a) < 9.5:
        continue
    if CAPTION_RX.match(text(a)):
        add(section_of(page(a)), [a], N, "NUMBERED_CAPTION",
            "numbered table/figure title naming the table or chart below it, whatever its size: " + POL + " caption rule; " + P33 + " (P3, a heading-sized caption); " + P41, CAPTION)

# ---------------- financial statements part (p59-67) ----------------
add("FS_PART", ["L2231:S0"], N, "PART_TITLE_BLOCK", "issuer above the part title: " + P42, META("ISSUER_METADATA", "DOCUMENT_PART"))
add("FS_PART", ["L2232:S0"], H, "PART_TITLE_BLOCK", "title of the financial statements part, on its contents page: " + P42, PART)
add("FS_PART", ["L2233:S0"], N, "PART_TITLE_BLOCK", "period date under the part title: " + P42, META("TEMPORAL_METADATA", "DOCUMENT_PART"))
add("FS_PART", ["L2242:S0"], H, "A5_PRECEDENT_CONTENTS_GROUP_LABEL",
    "'Supplementary Information' opening a sub-group of indented contents entries: " + P41 + " (A5); " + P42, TOC)
for r in R:
    a = r[0]
    if a not in claimed and page(a) == 59 and bold(a) and size(a) == 10.0 and A[a][3] == '0' and not single(a):
        add("FS_PART", [a], N, "CONTENTS_ENTRY", "contents entry with its page number: " + P42, NAV)
add("FS_PART", ["L2247:S0", "L2248:S0"], H, "EMBEDDED_REPORT", "the management report's title, two lines: " + P42, EMB)
add("FS_PART", ["L2251:S0", "L2252:S0"], H, "EMBEDDED_REPORT", "the ICFR audit report's title, two lines: " + P42, EMB)
add("FS_PART", ["L2255:S0"], H, "EMBEDDED_REPORT", "the auditor's report title: " + P42, EMB)
add("FS_PART", ["L2258:S0"], H, "A2_PRECEDENT_REPEATED_TITLE",
    "the auditor's report title again on a later page of the same report: " + P41 + " (A2); " + P42, EMB)
add("FS_PART", ["L2260:S0"], N, "PART_TITLE_BLOCK", "issuer above the statements' title page: " + P42, META("ISSUER_METADATA", "DOCUMENT_PART"))
add("FS_PART", ["L2261:S0"], H, "PART_TITLE_BLOCK", "title page of the financial statements: " + P42, PART)
add("FS_PART", ["L2262:S0"], N, "PART_TITLE_BLOCK", "period date on the title page: " + P42, META("TEMPORAL_METADATA", "DOCUMENT_PART"))

# ---------------- statements (p68-81) ----------------
for aliases in [["L2264:S0"], ["L2335:S0"], ["L2369:S0"], ["L2379:S0"], ["L2388:S0"], ["L2453:S0"], ["L2589:S0", "L2590:S0"]]:
    add("STATEMENTS", aliases, H, "STATEMENT_TITLE", "title of a financial statement: " + P33 + "; " + P42, STATEMENT)
for aliases in [["L2437:S0"], ["L2510:S0"], ["L2567:S0"], ["L2638:S0", "L2639:S0"], ["L2686:S0", "L2687:S0"], ["L2738:S0", "L2739:S0"], ["L2786:S0", "L2787:S0"]]:
    add("STATEMENTS", aliases, H, "A2_PRECEDENT_REPEATED_TITLE",
        "a statement's title repeated at the top of its next page: a display-title occurrence of the continuing statement: " + P41 + " (A2); " + P42, STATEMENT)
add("NOTES", ["L2822:S0"], H, "NOTES_PART", "opens the notes: " + P42, NOTES_PART)

# ---------------- set-apart non-headings ----------------
for r in R:
    a = r[0]
    if a in claimed:
        continue
    p, t, s = page(a), text(a), size(a)
    if 68 <= p <= 81 and italic(a) and s == 10.0 and not bold(a):
        add("STATEMENTS", [a], N, "STATEMENT_SUBTITLE", "period or unit line under a statement title: " + P42, META("TEMPORAL_METADATA", "FINANCIAL_STATEMENT"))
    elif p >= 5 and bold(a) and s >= 10.0 and (left(a) > 80 or not alone(a)) and not re.match(r'[\d\s,.\-–—$()%•]+$', t):
        add(section_of(p), [a], N, "CHART_OR_TABLE_INTERNAL", "bold label inside a chart or table (beside other cells or indented into the figure): " + P42, CHART)

json.dump({"items": items}, open(OUT, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
c = collections.Counter(i["verdict"] for i in items)
print(dict(c))
print(collections.Counter(i["pattern"] for i in items if i["verdict"] == H))
print(collections.Counter(i["pattern"] for i in items if i["verdict"] == N))
print([i["text"] for i in items if i["verdict"] == Q])
