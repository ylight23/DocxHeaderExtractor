"""SRC042_SOURCE_ONLY_GOLD_REVIEW_V1 - reviewer's working tool.

Reads only the original PDF's structured-lane atoms with their layout facts (atom-layout-facts.tsv). Never the
engine's blind proposals, never SRC-042's Gold file or any count it holds, never a converted DOCX. Emits the
reviewer's candidate list: HEADING, NON_HEADING (only occurrences that are set apart), AMBIGUOUS - each with
sourceParts. Decisions the user took on SRC-041 (the IBRD annual report, this IDA report's sibling) and DOC-0133,
and the frozen financial policy, are applied as precedent and say so.
"""
import json, re, sys, collections

SP = sys.argv[1]
OUT = sys.argv[2]
R = [l.split('\t') for l in open(SP + '/atom-layout-facts.tsv', encoding='utf-8').read().split('\n') if l]
A = {r[0]: r for r in R}
page = lambda a: int(A[a][1])
text = lambda a: A[a][10].strip()
size = lambda a: float(A[a][4])
bold = lambda a: float(A[a][5]) >= 0.5
italic = lambda a: float(A[a][6]) >= 0.5
left = lambda a: float(A[a][7])
right = lambda a: float(A[a][8])
ROWSEG = collections.Counter((r[1], r[2]) for r in R)
single = lambda a: ROWSEG[(A[a][1], A[a][2])] == 1
alone = lambda a: single(a) or (A[a][3] == '0' and all(float(r[7]) > 300 for r in R
                                                         if r[1] == A[a][1] and r[2] == A[a][2] and r[0] != a))

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
POL = "FINANCIAL_PROCUREMENT_HEADING_POLICY_V1"
H, N, Q = "HEADING", "NON_HEADING", "AMBIGUOUS"


def section_of(p):
    return "FRONT" if p <= 5 else "MDA" if p <= 66 else "FS_PART" if p <= 75 else "STATEMENTS" if p <= 90 else "NOTES"


# ---------------- cover (p1) ----------------
add("FRONT", ["L0000:S0"], N, "COVER_BLOCK", "issuer above the cover title: " + P33 + " (P1); " + P41, META("ISSUER_METADATA"))
add("FRONT", ["L0001:S0", "L0002:S0", "L0003:S0"], H, "COVER_BLOCK", "the cover title, one claim of three lines: " + P41, DOC)
add("FRONT", ["L0004:S0"], N, "COVER_BLOCK", "reporting date under the cover title: " + P41, META("TEMPORAL_METADATA"))

# ---------------- MD&A contents pages (p3-5) ----------------
add("FRONT", ["L0005:S0"], H, "A3_PRECEDENT_PART_LABEL_ON_CONTENTS_PAGE",
    "the MD&A label at the top of its contents page opens the part: " + P41 + " (A3)", PART)
for a in ["L0044:S0", "L0093:S0"]:
    add("FRONT", [a], N, "RUNNING_HEADER", "the same line atop the following contents pages: " + P41 + " (A3)", FURN)
for a in ["L0006:S0", "L0045:S0", "L0046:S0", "L0074:S0", "L0094:S0"]:
    add("FRONT", [a], H, "TOC_OPENER", "opens a contents list or sub-list: " + P33 + "; " + P41, TOC)

# ---------------- explicit occurrence decisions by precedent ----------------
add("MDA", ["L0803:S0"], H, "MDA_SUBHEADING",
    "10pt bold standalone colon-ended label opening its list of adjustments: " + P41 + " (the same label, at 12pt) and " + P33 + " (P6, a 10pt bold label opening its region)", SEC)
add("MDA", ["L1629:S0"], N, "TABLE_PART_TITLE", "between a numbered table caption and that table's unit line: part of the table", CHART)
add("MDA", ["L2562:S0"], N, "BLANK_PAGE_NOTICE", "a notice that the page is blank: it identifies and organizes no region (" + POL + " definition)", FURN)

# ---------------- body headings by the document's own hierarchy ----------------
WRAPS = {"L3215:S0": ["L3216:S0"], "L3348:S0": ["L3349:S0"], "L3599:S0": ["L3600:S0", "L3601:S0"],
         "L3776:S0": ["L3777:S0"], "L4606:S0": ["L4607:S0"]}
TAIL = {t for v in WRAPS.values() for t in v}


def heading_aliases(a):
    return [a] + WRAPS.get(a, [])


for r in R:
    a = r[0]
    if a in claimed or a in TAIL:
        continue
    p, t, s = page(a), text(a), size(a)
    if p < 6 or p > 66 and p < 91 or not alone(a) or left(a) > 80:
        continue
    sec = section_of(p)
    if bold(a) and s in (18.0, 16.0, 15.0) and p <= 66:
        if t == "Appendix":
            add(sec, heading_aliases(a), H, "APPENDIX_TITLE", "18pt title opening the appendix: " + P41, PART)
        elif t.startswith("Section "):
            add(sec, heading_aliases(a), H, "MDA_SECTION_TITLE", "MD&A section title (15-18pt): " + P41, SECT)
    elif bold(a) and s == 14.0 and t.startswith("NOTE "):
        add(sec, heading_aliases(a), H, "NOTE_TITLE", "note title opening a complete note: " + POL + "; " + P33 + "; " + P41, NOTE)
    elif bold(a) and s == 14.0 and t == "PURPOSE AND AFFILIATED ORGANIZATIONS":
        add(sec, heading_aliases(a), H, "NOTE_TITLE", "the unlettered opening note: " + P41, NOTE)
    elif bold(a) and s in (14.0, 12.0, 11.0):
        add(sec, heading_aliases(a), H, "NOTE_SUBHEADING" if sec == "NOTES" else "MDA_SUBHEADING",
            f"{s:g}pt bold standalone label over its own prose or data: " + P33 + "; " + P41, SEC)
    elif italic(a) and not bold(a) and s == 11.0 and right(a) < 450:
        add(sec, [a], H, "ITALIC_LOCAL_LABEL", "11pt italic standalone label opening its paragraph: " + P41 + " (and DOC-0133 P7)", SEC)

# ---------------- numbered captions: false by the caption rule; one box holds prose ----------------
for r in R:
    a = r[0]
    if a in claimed or page(a) < 6 or not bold(a) or not 9.5 <= size(a) <= 10.5:
        continue
    t = text(a)
    if not (re.match(r'(Table|Figure|Box)\s+[A-Z]?\d+(?:\.\d+)*', t)):
        continue
    if t.startswith("Box 2:"):
        add(section_of(page(a)), [a], H, "S042_A1_BOX_OF_PROSE",
            "user decision 2026-09-25: the five principles under it are semantic units that belong to it - the title establishes a region (an embedded artifact), it does not only name an object. The other box titles name the table or diagram below them and stay captions. Classify occurrences, not strings: no rule on the 'Box N:' prefix",
            ax(["IDENTITY", "STRUCTURE"], "IDENTITY", "EMBEDDED_ARTIFACT"))
    else:
        add(section_of(page(a)), [a], N, "NUMBERED_CAPTION",
            "numbered table/figure/box title naming the table, chart or list-table below it: " + POL + " caption rule; " + P41, CAPTION)

# ---------------- financial statements part (p67-75) ----------------
add("FS_PART", ["L2564:S0"], N, "PART_TITLE_BLOCK", "issuer above the part title: " + P41, META("ISSUER_METADATA", "DOCUMENT_PART"))
add("FS_PART", ["L2565:S0"], H, "PART_TITLE_BLOCK", "title of the financial statements part, on its contents page: " + P41, PART)
add("FS_PART", ["L2566:S0"], N, "PART_TITLE_BLOCK", "period date under the part title: " + P41, META("TEMPORAL_METADATA", "DOCUMENT_PART"))
add("FS_PART", ["L2575:S0"], H, "A5_PRECEDENT_CONTENTS_GROUP_LABEL",
    "'Supplementary Information' opening a sub-group of indented contents entries: " + P41 + " (A5)", TOC)
for r in R:
    a = r[0]
    if a not in claimed and page(a) == 67 and bold(a) and size(a) == 10.0 and A[a][3] == '0' and not single(a):
        add("FS_PART", [a], N, "CONTENTS_ENTRY", "contents entry with its page number: " + P41, NAV)
add("FS_PART", ["L2580:S0", "L2581:S0"], H, "EMBEDDED_REPORT", "the management report's title, two lines: " + P41, EMB)
add("FS_PART", ["L2584:S0", "L2585:S0"], H, "EMBEDDED_REPORT", "the ICFR audit report's title, two lines: " + P41, EMB)
add("FS_PART", ["L2588:S0"], H, "EMBEDDED_REPORT", "the auditor's report title: " + P41, EMB)
add("FS_PART", ["L2591:S0"], H, "A2_PRECEDENT_REPEATED_TITLE",
    "the auditor's report title again on a later page of the same report: a display-title occurrence of the continuing report: " + P41 + " (A2)", EMB)
add("FS_PART", ["L2593:S0"], N, "PART_TITLE_BLOCK", "issuer above the statements' title page: " + P41, META("ISSUER_METADATA", "DOCUMENT_PART"))
add("FS_PART", ["L2594:S0"], H, "PART_TITLE_BLOCK", "title page of the financial statements: " + P41, PART)
add("FS_PART", ["L2595:S0"], N, "PART_TITLE_BLOCK", "period date on the title page: " + P41, META("TEMPORAL_METADATA", "DOCUMENT_PART"))

# ---------------- statements (p76-90) ----------------
for aliases in [["L2597:S0"], ["L2668:S0"], ["L2702:S0"], ["L2714:S0"], ["L2767:S0"], ["L2832:S0"], ["L2968:S0", "L2969:S0"]]:
    add("STATEMENTS", aliases, H, "STATEMENT_TITLE", "title of a financial statement: " + P33 + "; " + P41, STATEMENT)
for aliases in [["L2816:S0"], ["L2890:S0"], ["L2946:S0"], ["L3017:S0", "L3018:S0"], ["L3065:S0", "L3066:S0"], ["L3117:S0", "L3118:S0"], ["L3165:S0", "L3166:S0"]]:
    add("STATEMENTS", aliases, H, "A2_PRECEDENT_REPEATED_TITLE",
        "a statement's title repeated at the top of its next page (here without '(CONTINUED)'): a display-title occurrence of the continuing statement: " + P41 + " (A2)", STATEMENT)
add("NOTES", ["L3202:S0"], H, "NOTES_PART", "opens the notes: " + P33 + "; " + P41, NOTES_PART)

# ---------------- set-apart non-headings ----------------
for r in R:
    a = r[0]
    if a in claimed:
        continue
    p, t, s = page(a), text(a), size(a)
    if 76 <= p <= 90 and italic(a) and s == 10.0 and not bold(a):
        add("STATEMENTS", [a], N, "STATEMENT_SUBTITLE", "period or unit line under a statement title: " + P41, META("TEMPORAL_METADATA", "FINANCIAL_STATEMENT"))
    elif p >= 6 and bold(a) and s >= 10.0 and (left(a) > 80 or not alone(a)) and not re.match(r'[\d\s,.\-–—$()%•]+$', t):
        add(section_of(p), [a], N, "CHART_OR_TABLE_INTERNAL", "bold label inside a chart or table (beside other cells or indented into the figure): " + P41, CHART)
    elif p >= 6 and bold(a) and 9.5 <= s <= 10.5 and left(a) <= 80 and alone(a) and not re.match(r'[\d\s,.\-–—$()%•]+$', t):
        add(section_of(p), [a], N, "CAPTION_CONTINUATION", "second line of a caption or chart title", CAPTION)

json.dump({"items": items}, open(OUT, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
c = collections.Counter(i["verdict"] for i in items)
print(dict(c))
print(collections.Counter(i["pattern"] for i in items if i["verdict"] == H))
print(collections.Counter(i["pattern"] for i in items if i["verdict"] == N))
print([i["text"] for i in items if i["verdict"] == Q])
print('caption continuations:', [i["text"] for i in items if i["pattern"] == "CAPTION_CONTINUATION"])
