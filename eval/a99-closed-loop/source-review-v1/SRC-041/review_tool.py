"""SRC041_SOURCE_ONLY_GOLD_REVIEW_V1 - reviewer's working tool.

Reads only the original PDF's structured-lane atoms with their layout facts (atom-layout-facts.tsv, dumped
by the diagnostics test). Never the engine proposals (10b3317), never SRC-041's Gold file or its count-only
total, never the retired DOCX. Emits the reviewer's candidate list: HEADING, NON_HEADING (only occurrences
that are set apart), AMBIGUOUS - each with sourceParts. Decisions the user took on DOC-0133 (the quarterly
sibling of this annual report) and the frozen financial policy are applied as precedent and say so.
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
# Alone in its column: the only segment of its row, or its row's first segment with every other one in the
# right-hand column (a chart title or unit line beside a left-margin heading).
alone = lambda a: single(a) or (A[a][3] == '0' and all(float(r[7]) > 300 for r in R
                                                         if r[1] == A[a][1] and r[2] == A[a][2] and r[0] != a))

items = []
claimed = set()


def whole(a):
    return {"sourceAlias": a, "selectionMode": "WHOLE_ALIAS", "verbatimText": None, "occurrence": None}


def add(section, aliases, verdict, pattern, reason, axes, expected=None):
    parts = [whole(a) for a in aliases]
    for a in aliases:
        assert a not in claimed, a
        claimed.add(a)
    t = ' '.join(text(a) for a in aliases)
    if expected is not None:
        assert re.sub(r'\s+', '', t) == re.sub(r'\s+', '', expected), (t, expected)
    items.append({"section": section, "parts": parts, "text": t, "verdict": verdict, "pattern": pattern,
                  "reason": reason, "axes": axes, "page": page(aliases[0])})


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
POL = "FINANCIAL_PROCUREMENT_HEADING_POLICY_V1"
H, N, Q = "HEADING", "NON_HEADING", "AMBIGUOUS"


def section_of(p):
    return "FRONT" if p <= 5 else "MDA" if p <= 74 else "FS_PART" if p <= 83 else "STATEMENTS" if p <= 96 else "NOTES"


# ---------------- cover (p1): P1 precedent - title lines one claim; issuer and date are metadata ----------------
add("FRONT", ["L0000:S0", "L0001:S0"], N, "COVER_BLOCK", "issuer above the cover title: " + P33 + " (P1)", META("ISSUER_METADATA"),
    "International Bank forReconstruction and Development")
add("FRONT", ["L0002:S0", "L0003:S0", "L0004:S0"], H, "COVER_BLOCK", "the cover title, one claim of three lines: " + P33 + " (P1)", DOC,
    "Management’s Discussion & Analysis and Financial Statements")
add("FRONT", ["L0005:S0"], N, "COVER_BLOCK", "reporting date under the cover title: " + P33 + " (P1)", META("TEMPORAL_METADATA"))

# ---------------- MD&A contents pages (p3-5) ----------------
add("FRONT", ["L0006:S0"], H, "S041_A3_PART_LABEL_ON_CONTENTS_PAGE",
    "user decision 2026-09-25: this occurrence opens the MD&A part and its contents region; the same text atop p4-p5 is a separate occurrence (running header, false). Classify occurrences, not strings",
    PART)
for a in ["L0057:S0", "L0105:S0"]:
    add("FRONT", [a], N, "RUNNING_HEADER", "the same line on the following contents pages: running header", FURN)
for a in ["L0007:S0", "L0058:S0", "L0059:S0", "L0106:S0", "L0126:S0"]:
    add("FRONT", [a], H, "TOC_OPENER", "opens a contents list or sub-list (Contents / Tables / Figures / Boxes): " + P33, TOC)

# ---------------- body headings by the document's own hierarchy ----------------
# Wrapped titles: the line fills its measure and the next line, set the same way, completes it.
WRAPS = {
    "L0897:S0": ["L0898:S0"], "L2757:S0": ["L2758:S0"], "L3773:S0": ["L3774:S0"], "L3810:S0": ["L3811:S0"],
    "L4066:S0": ["L4067:S0", "L4068:S0"], "L4282:S0": ["L4283:S0"], "L5026:S0": ["L5027:S0"], "L5221:S0": ["L5222:S0"],
}
TAIL = {t for v in WRAPS.values() for t in v}


def heading_aliases(a):
    return [a] + WRAPS.get(a, [])


for r in R:
    a = r[0]
    if a in claimed or a in TAIL:
        continue
    p, t, s = page(a), text(a), size(a)
    if p < 6 or not alone(a) or left(a) > 80:
        continue
    sec = section_of(p)
    if bold(a) and s in (18.0, 15.0):
        if t == "Appendix":
            add(sec, heading_aliases(a), H, "APPENDIX_TITLE", "18pt title opening the appendix", PART)
        else:
            add(sec, heading_aliases(a), H, "MDA_SECTION_TITLE", "MD&A section title (18pt; Section XIV set at 15pt)", SECT)
    elif bold(a) and s == 14.0 and t.startswith("NOTE "):
        add(sec, heading_aliases(a), H, "NOTE_TITLE", "note title opening a complete note: " + POL + "; " + P33, NOTE)
    elif bold(a) and s == 14.0 and t == "PURPOSE AND AFFILIATED ORGANIZATIONS":
        add(sec, heading_aliases(a), H, "NOTE_TITLE", "the unlettered opening note, set as the lettered notes are", NOTE)
    elif bold(a) and s == 14.0 and t.startswith("Eligible Borrowing Member Countries"):
        add(sec, [a], N, "S041_A4_APPENDIX_TABLE_TITLE",
            "user decision 2026-09-25: names only the list/object directly below it and opens no independent region: an ordinary local object title, equivalent to a caption (a decision on this occurrence's scope, not a rule about titles over lists)",
            CAPTION)
    elif bold(a) and s in (14.0, 12.0, 11.0):
        add(sec, heading_aliases(a), H, "NOTE_SUBHEADING" if sec == "NOTES" else "MDA_SUBHEADING",
            f"{s:g}pt bold standalone label over its own prose or data: " + P33, SEC)
    elif italic(a) and not bold(a) and s == 11.0 and right(a) < 320:
        add(sec, [a], H, "ITALIC_LOCAL_LABEL",
            "11pt italic standalone label opening its paragraph (a note-local label: " + P33 + ", P7)", SEC)

# ---------------- numbered table / figure captions: false by the caption rule ----------------
for r in R:
    a = r[0]
    if a in claimed or page(a) < 6 or not bold(a) or not 9.5 <= size(a) <= 10.5:
        continue
    t = text(a)
    m = re.match(r'(Table|Figure|Box)\s+[A-Z]?\d+(?:\.\d+)*', t) or re.match(r'K\d+\.\d+$', t)
    if not m:
        continue
    if t.startswith("Box"):
        add(section_of(page(a)), [a], N, "NUMBERED_CAPTION",
            "numbered box title; every box holds a table or a diagram it names: " + POL + " caption rule", CAPTION)
    else:
        add(section_of(page(a)), [a], N, "NUMBERED_CAPTION", "numbered table/figure title: " + POL + " caption rule", CAPTION)

# ---------------- financial statements part (p75-83) ----------------
add("FS_PART", ["L3158:S0"], N, "PART_TITLE_BLOCK", "issuer above the part title: " + P33 + " (P2)", META("ISSUER_METADATA", "DOCUMENT_PART"))
add("FS_PART", ["L3159:S0"], H, "PART_TITLE_BLOCK", "title of the financial statements part, on its contents page: " + P33 + " (P2)", PART)
add("FS_PART", ["L3160:S0"], N, "PART_TITLE_BLOCK", "period date under the part title: " + P33 + " (P2)", META("TEMPORAL_METADATA", "DOCUMENT_PART"))
for a in ["L3161:S0", "L3162:S0", "L3163:S0", "L3164:S0", "L3165:S0", "L3166:S0", "L3167:S0", "L3168:S0", "L3170:S0", "L3171:S0", "L3172:S0"]:
    add("FS_PART", [a], N, "CONTENTS_ENTRY", "contents entry with its page number: " + P33, NAV)
add("FS_PART", ["L3169:S0"], H, "S041_A5_CONTENTS_GROUP_LABEL",
    "user decision 2026-09-25: not a navigation entry pointing elsewhere: it opens a sub-group of two indented entries, like the Tables / Figures / Boxes openers",
    TOC)
add("FS_PART", ["L3174:S0", "L3175:S0"], H, "EMBEDDED_REPORT", "the management report's title, one claim of two lines: " + P33 + " (embedded report)", EMB)
add("FS_PART", ["L3178:S0", "L3179:S0"], H, "EMBEDDED_REPORT", "the ICFR audit report's title, one claim of two lines: " + P33, EMB)
add("FS_PART", ["L3182:S0"], H, "EMBEDDED_REPORT", "the auditor's report title: " + P33, EMB)
add("FS_PART", ["L3185:S0"], H, "S041_A2_CONTINUED_TITLE",
    "user decision 2026-09-25: a real display-title occurrence of the continuing report; whether it is a REPEAT or a CONTINUATION is derived by identity resolution later and does not affect isHeading",
    EMB)
add("FS_PART", ["L3187:S0"], N, "PART_TITLE_BLOCK", "issuer above the statements' title page: " + P33 + " (P2)", META("ISSUER_METADATA", "DOCUMENT_PART"))
add("FS_PART", ["L3188:S0"], H, "PART_TITLE_BLOCK", "title page of the financial statements themselves: " + P33 + " (P2)", PART)
add("FS_PART", ["L3189:S0"], N, "PART_TITLE_BLOCK", "period date on the title page: " + P33 + " (P2)", META("TEMPORAL_METADATA", "DOCUMENT_PART"))

# ---------------- statements (p84-96) ----------------
STATEMENTS = [["L3191:S0"], ["L3259:S0"], ["L3296:S0"], ["L3311:S0"], ["L3355:S0"], ["L3418:S0"], ["L3526:S0", "L3527:S0"]]
for aliases in STATEMENTS:
    add("STATEMENTS", aliases, H, "STATEMENT_TITLE", "title of a financial statement: " + P33 + "; " + POL, STATEMENT)
for aliases in [["L3476:S0"], ["L3585:S0", "L3586:S0"], ["L3644:S0", "L3645:S0"], ["L3703:S0", "L3704:S0"]]:
    add("STATEMENTS", aliases, H, "S041_A2_CONTINUED_TITLE",
        "user decision 2026-09-25: a real display-title occurrence of the continuing statement; REPEAT / CONTINUATION is derived by identity resolution later and does not affect isHeading",
        STATEMENT)
add("NOTES", ["L3760:S0"], H, "NOTES_PART", "opens the notes: " + P33, NOTES_PART)

# ---------------- set-apart non-headings (only the ones that look like headings) ----------------
for r in R:
    a = r[0]
    if a in claimed:
        continue
    p, t, s = page(a), text(a), size(a)
    if p >= 84 and p <= 96 and italic(a) and s == 10.0 and not bold(a):
        add("STATEMENTS", [a], N, "STATEMENT_SUBTITLE", "period or unit line under a statement title: " + P33 + " (unit lines and period headings are false)",
            META("TEMPORAL_METADATA", "FINANCIAL_STATEMENT"))
    elif p >= 6 and bold(a) and s >= 10.0 and (left(a) > 80 or not single(a)) and not re.match(r'[\d\s,.\-–—$()%]+$', t):
        add(section_of(p), [a], N, "CHART_OR_TABLE_INTERNAL", "bold label inside a chart or table (beside other cells or indented into the figure): " + P33, CHART)
    elif p >= 6 and bold(a) and s == 10.0 and a in {"L1655:S0", "L2508:S0", "L5388:S0", "L5494:S0", "L5964:S0"}:
        add(section_of(p), [a], N, "CAPTION_CONTINUATION", "second line of a caption or chart title", CAPTION)

json.dump({"items": items}, open(OUT, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
c = collections.Counter(i["verdict"] for i in items)
print(dict(c))
print(collections.Counter(i["pattern"] for i in items if i["verdict"] == H))
print(collections.Counter(i["pattern"] for i in items if i["verdict"] == Q))
