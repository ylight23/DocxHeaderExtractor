"""SRC054_SOURCE_ONLY_GOLD_REVIEW_V1 - reviewer's working tool.

Reads only the original PDF's structured-lane atoms with what their glyphs say (atom-glyph-facts.tsv: effective point
size and font names per atom) and the lane's line facts (atom-layout-facts.tsv). Never the engine's blind proposals,
never SRC-054's Gold file or any count it holds, never a converted DOCX. Emits the reviewer's candidate list:
HEADING, NON_HEADING (only occurrences that are set apart), AMBIGUOUS - each with sourceParts.

SRC-054 is SRC-053's sibling (the IBRD information statement to SRC-053's IDA one): Times, body 10pt, sections 12pt,
sub-sections 11pt, labels 10pt bold and - in the notes - 10pt bold italic. Its tables are set at body size too, so a
bold label inside a numbered table is told from a heading by where it sits (a table region runs from a numbered
caption until running prose resumes), not by its size. Levels are read by function - a standalone label at the text
margin over its own prose or list. Decisions the user took on SRC-053, SRC-042, SRC-041, SRC-029 and DOC-0133, and the
frozen financial policy, are applied as precedent and say so.
"""
import json, re, sys, collections

SP = sys.argv[1]
OUT = sys.argv[2]
G = [l.split('\t') for l in open(SP + '/atom-glyph-facts.tsv', encoding='utf-8').read().split('\n') if l]
A = {r[0]: r for r in G}
IDX = {r[0]: i for i, r in enumerate(G)}


def fonts(a):
    out = []
    for f in A[a][5].split(','):
        if ':' in f:
            n, c = f.rsplit(':', 1)
            out.append((n, int(c)))
    return out


def share(a, marks):
    fs = fonts(a)
    total = sum(c for _, c in fs) or 1
    return sum(c for n, c in fs if any(m in n for m in marks)) / total


page = lambda a: int(A[a][1])
text = lambda a: A[a][9].strip()
size = lambda a: float(A[a][4])
bold = lambda a: share(a, ("Bold", "Black")) >= 0.5
italic = lambda a: share(a, ("Italic",)) >= 0.5
left = lambda a: float(A[a][6])
right = lambda a: float(A[a][7])
ROWSEG = collections.Counter((r[1], r[2]) for r in G)
single = lambda a: ROWSEG[(A[a][1], A[a][2])] == 1
words = lambda a: len(text(a).split())
CAPTION_RX = re.compile(r'(Table|Figure|Box)\s+[A-Z]?\d+(?:\.\d+)*')
LEADERS = re.compile(r'(\.\s){4,}')

items = []
claimed = set()


def whole(a):
    return {"sourceAlias": a, "selectionMode": "WHOLE_ALIAS", "verbatimText": None, "occurrence": None}


def verbatim(a, t):
    assert text(a).count(t) == 1, (a, t)
    return {"sourceAlias": a, "selectionMode": "VERBATIM_TEXT", "verbatimText": t, "occurrence": None}


def add(section, aliases, verdict, pattern, reason, axes, parts=None):
    for a in aliases:
        assert a not in claimed, a
        claimed.add(a)
    parts = parts or [whole(a) for a in aliases]
    items.append({"section": section, "parts": parts,
                  "text": ' '.join(p["verbatimText"] or text(p["sourceAlias"]) for p in parts),
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
BODY = ax(["INFORMATION"], "INFORMATION", "SECTION", ("BODY",), "NONE")
META = lambda info, scope="DOCUMENT": ax(["INFORMATION"], "INFORMATION", scope, ("METADATA",), "NONE", info)
P29 = "precedent SRC-029 (user-decided 2026-09-25)"
P33 = "precedent DOC-0133 (user-decided 2026-09-25)"
P41 = "precedent SRC-041 (user-decided 2026-09-25)"
P42 = "precedent SRC-042 (user-decided 2026-09-25)"
P53 = "precedent SRC-053 (user-decided 2026-09-26)"
POL = "FINANCIAL_PROCUREMENT_HEADING_POLICY_V1"
H, N, Q = "HEADING", "NON_HEADING", "AMBIGUOUS"


def section_of(p):
    return ("FRONT" if p <= 3 else "MDA" if p <= 88 else "FS_PART" if p <= 99 else "STATEMENTS" if p <= 113
            else "NOTES" if p <= 169 else "BACK")


def is_prose(b):
    return single(b) and words(b) >= 5 and not LEADERS.search(text(b)) and left(b) <= 110


def opens_prose(a):
    """The first following atom that is not a standalone bold or italic label is a line of running prose."""
    i = IDX[a] + 1
    while i < len(G) and i - IDX[a] <= 3 and single(G[i][0]) and (bold(G[i][0]) or italic(G[i][0])) and words(G[i][0]) <= 8:
        i += 1
    return i < len(G) and is_prose(G[i][0])


def starts_block(a):
    """The line before ends its own block: a finished sentence or label, a short last line, a bold label, or a new page."""
    b = G[IDX[a] - 1][0]
    return page(b) != page(a) or bold(b) or text(b).endswith(('.', ':', ';')) or right(b) < 400


def opens_list(a):
    b = G[IDX[a] + 1][0]
    return single(b) and words(b) >= 4 and not LEADERS.search(text(b)) and left(b) <= 110


def leads_to_prose(j):
    """A chain of standalone labels (bold or italic, no leaders) that ends in a plain line of prose."""
    k = j
    while k < len(G) and k - j <= 3 and single(G[k][0]) and (bold(G[k][0]) or italic(G[k][0])) and not LEADERS.search(text(G[k][0])):
        k += 1
    if k == j or k >= len(G):
        return False
    x = G[k][0]
    return single(x) and not bold(x) and not italic(x) and words(x) >= 5 and not LEADERS.search(text(x)) and left(x) <= 110


# ---------------- table regions: a numbered caption until running prose resumes ----------------
IN_TABLE = set()
for i, r in enumerate(G):
    a = r[0]
    if not (bold(a) and CAPTION_RX.match(text(a)) and page(a) < 100):
        continue
    j = i + 1
    while j < len(G) and page(G[j][0]) == page(a):
        b = G[j][0]
        # The region ends where running prose resumes - a plain line of text with no leaders (table rows here carry
        # dot leaders to their figures) - or at a bold label that such prose follows directly: a heading resuming.
        plain = lambda x: single(x) and not bold(x) and not italic(x) and words(x) >= 5 and not LEADERS.search(text(x)) and left(x) <= 110
        if plain(b) or (single(b) and (bold(b) or italic(b)) and leads_to_prose(j)):
            break
        IN_TABLE.add(b)
        j += 1

# ---------------- cover (p1) and back cover (p170) ----------------
add("FRONT", ["L0000:S0"], H, "S053_Q3_PRECEDENT_COVER_TITLE",
    "14pt bold 'Information Statement' above the 24pt issuer: the cover title, one line: " + P53 + " (S053_Q3, title only)", DOC)
add("FRONT", ["L0001:S0", "L0002:S0"], N, "S053_Q3_PRECEDENT_COVER_TITLE",
    "the 24pt issuer name under the cover title, two lines: issuer metadata: " + P53 + " (S053_Q3)", META("ISSUER_METADATA"))
add("FRONT", ["L0016:S0"], H, "FRONT_SECTION_TITLE", "centered bold caps title of the cover page's own section over its paragraph: " + P53, SECT)
add("FRONT", ["L0032:S0"], H, "FRONT_SECTION_TITLE", "centered bold caps title of the summary pages: " + P53, SECT)
add("FRONT", ["L0033:S0"], N, "SECTION_SUBTITLE", "bold-italic 'as of' line under the summary title: " + P53, META("TEMPORAL_METADATA", "SECTION"))
for a in ["L0111:S0", "L0112:S0"]:
    add("FRONT", [a], N, "QUALIFICATION_NOTE", "bold centered note closing the summary ('The above information is qualified ...'): " + P53, BODY)
add("BACK", ["L6307:S0"], H, "S053_Q1_PRECEDENT_BACK_COVER_TITLE",
    "10pt bold centered 'Information Statement' atop the back cover: a display-title occurrence of the document: " + P53 + " (S053_Q1)", DOC)
add("BACK", ["L6308:S0", "L6309:S0"], N, "COVER_BLOCK", "issuer under the back-cover title, two lines: " + P53, META("ISSUER_METADATA"))
add("BACK", ["L6321:S0"], H, "TOC_OPENER", "opens the contents list: " + P53, TOC)

# ---------------- MD&A: explicit occurrences ----------------
add("MDA", ["L0378:S0"], H, "MDA_SUBHEADING",
    "the bold sub-heading 'Equity-to-Loans Ratio' over its left-column prose; the same atom runs on into the neighbouring chart's unit line "
    "('Ratio in percentages'), which is no part of the title: " + P33 + " (P6); " + P53, SEC,
    parts=[verbatim("L0378:S0", "Equity-to-Loans Ratio")])
add("MDA", ["L1770:S0"], N, "RUN_IN_LEAD", "bold committee name that runs on into its sentence across the line: " + P53, BODY)
for a, why in [("L2869:S0", ["L2870:S0"]), ("L3097:S0", ["L3098:S0"])]:
    add("MDA", [a] + why, H, "MDA_SECTION_TITLE", "12pt bold section title over two centered lines: " + P41 + "; " + P53, SECT)
add("MDA", ["L3129:S0"], H, "MDA_SECTION_TITLE", "12pt bold section title of the appendix ('XXI: APPENDIX'): " + P53, SECT)
PLAIN_LABELS = ["L0234:S0", "L0248:S0", "L0253:S0", "L0509:S0", "L0583:S0", "L0588:S0", "L0728:S0", "L0736:S0",
                "L1973:S0", "L1994:S0", "L2009:S0", "L2038:S0", "L2049:S0", "L2430:S0", "L2437:S0"]
for a in PLAIN_LABELS:
    add("MDA", [a], H, "S054_Q1_PLAIN_STANDALONE_LABEL",
        "user decision 2026-09-26 (S054_Q1, TRUE): a short standalone line in plain Times-Roman at the margin, after a finished paragraph "
        "or at a page top, over its own prose - the lowest label level of this MD&A (its IDA sibling SRC-053 set the same labels 9pt bold). "
        "Function decides, as in S053_Q2, even with no typographic mark", SEC)
add("MDA", ["L1744:S0"], H, "MDA_SUBHEADING",
    "10pt bold standalone label opening its region - the committee figure below it, then its prose: " + P33 + " (P6); " + P53, SEC)
for a in ["L3130:S0", "L3184:S0"]:
    add("MDA", [a], H, "APPENDIX_SUBHEADING", "bold title of an appendix list (glossary / abbreviations) that the list belongs to: " + P53, SEC)
add("MDA", ["L3233:S0"], N, "S041_A4_PRECEDENT_APPENDIX_TABLE_TITLE",
    "unnumbered title naming only the table directly below it: an object title, equivalent to a caption: " + P41 + " (A4)", CAPTION)
FIGURE_INTERNAL = {"L0200:S0", "L0202:S0", "L2023:S0", "L2395:S0", "L2412:S0", "L2413:S0", "L2741:S0"}

# ---------------- financial statements part (p89-99) ----------------
add("FS_PART", ["L3255:S0"], N, "PAGE_NOTICE", "'[THIS PAGE INTENTIONALLY LEFT BLANK]': " + P42, FURN)
add("FS_PART", ["L3257:S0", "L3258:S0"], N, "PART_TITLE_BLOCK", "issuer above the part title, two lines: " + P42 + "; " + P53, META("ISSUER_METADATA", "DOCUMENT_PART"))
add("FS_PART", ["L3259:S0", "L3260:S0"], H, "PART_TITLE_BLOCK",
    "title of the financial statements part: 'FINANCIAL STATEMENTS AND INTERNAL CONTROL' and 'REPORTS', whose atom runs on into the "
    "period date ('JUNE 30, 2025'), which is no part of the title: " + P53, PART,
    parts=[whole("L3259:S0"), verbatim("L3260:S0", "REPORTS")])
for r in G:
    a = r[0]
    if a not in claimed and page(a) == 90 and bold(a) and A[a][3] == '0' and not single(a):
        add("FS_PART", [a], N, "CONTENTS_ENTRY", "contents entry with its page number: " + P42, NAV)
add("FS_PART", ["L3278:S0"], H, "EMBEDDED_REPORT", "title of management's assurance letter (centered, regular weight): " + P53, EMB)
add("FS_PART", ["L3319:S0", "L3320:S0"], H, "EMBEDDED_REPORT", "title of management's ICFR report, two lines: " + P53, EMB)
add("FS_PART", ["L3377:S0"], H, "EMBEDDED_REPORT", "the auditor's ICFR report title: " + P53, EMB)
add("FS_PART", ["L3438:S0"], H, "EMBEDDED_REPORT", "the auditor's report title: " + P53, EMB)
for a in ["L3472:S0", "L3509:S0"]:
    add("FS_PART", [a], N, "RUNNING_HEADER",
        "'Independent Auditor's Report' in body type at the top of the report's continuation pages - a running header, not the display "
        "title (which is set bold in capitals): " + P41 + " (A3)", FURN)
for a in ["L3369:S0", "L3430:S0"]:
    add("FS_PART", [a], N, "SIGNATURE", "the audit firm's letterhead name above its report", META("AUTHORSHIP_METADATA", "EMBEDDED_ARTIFACT"))
add("FS_PART", ["L3523:S0", "L3524:S0"], N, "PART_TITLE_BLOCK", "issuer on the statements' title page, two lines: " + P42, META("ISSUER_METADATA", "DOCUMENT_PART"))
add("FS_PART", ["L3525:S0"], H, "PART_TITLE_BLOCK", "title page of the financial statements: " + P42, PART)
add("FS_PART", ["L3526:S0"], N, "PART_TITLE_BLOCK", "period date on the title page: " + P42, META("TEMPORAL_METADATA", "DOCUMENT_PART"))

# ---------------- statements (p100-113) ----------------
for aliases in [["L3528:S0"], ["L3596:S0"], ["L3632:S0"], ["L3646:S0"], ["L3684:S0"], ["L3741:S0"], ["L3858:S0", "L3859:S0"]]:
    add("STATEMENTS", aliases, H, "STATEMENT_TITLE", "title of a financial statement: " + P33 + "; " + P42, STATEMENT)
for aliases in [["L3562:S0"], ["L3804:S0"], ["L3906:S0", "L3907:S0"], ["L3952:S0", "L3953:S0"], ["L3998:S0", "L3999:S0"],
                ["L4043:S0", "L4044:S0"], ["L4089:S0", "L4090:S0"]]:
    add("STATEMENTS", aliases, H, "A2_PRECEDENT_REPEATED_TITLE",
        "a statement's title repeated at the top of its next page: " + P41 + " (A2); " + P53, STATEMENT)
add("NOTES", ["L4122:S0"], H, "NOTES_PART", "opens the notes: " + P42, NOTES_PART)

# ---------------- body headings, read by function ----------------
WRAPS = {"L4416:S0": ["L4417:S0"], "L5538:S0": ["L5539:S0"]}
TAIL = {t for v in WRAPS.values() for t in v}
CAPTION_TAILS = {"L1578:S0", "L5701:S0", "L5803:S0"}

for r in G:
    a = r[0]
    if a in claimed or a in TAIL or a in CAPTION_TAILS or a in IN_TABLE or a in FIGURE_INTERNAL:
        continue
    p, t, s = page(a), text(a), size(a)
    if not (2 <= p <= 88 or 90 <= p <= 99 or 114 <= p <= 169):
        continue
    if not bold(a) or not single(a) or s < 9.5:
        continue
    sec = section_of(p)
    if t.startswith("SECTION ") and s >= 12.0:
        add(sec, [a], H, "MDA_SECTION_TITLE", "12pt bold section title (centered): " + P41 + "; " + P53, SECT)
        continue
    if left(a) > 95 or CAPTION_RX.match(t):
        continue
    if t.startswith("NOTE ") or t == "PURPOSE AND AFFILIATED ORGANIZATIONS":
        add(sec, [a], H, "NOTE_TITLE", "note title opening a complete note: " + POL + "; " + P41, NOTE)
    elif s >= 10.5 or opens_prose(a) or (t.endswith(":") and opens_list(a)):
        pattern = "NOTE_SUBHEADING" if sec == "NOTES" else "FS_SUBHEADING" if sec == "FS_PART" else "MDA_SUBHEADING"
        slant = " (bold italic: slant does not decide - " + P53 + " S053_Q2)" if italic(a) else ""
        add(sec, [a] + WRAPS.get(a, []), H, pattern,
            f"{s:g}pt bold standalone label at the margin over its own prose or list{slant}: " + P33 + " (P6); " + P41 + "; " + P53, SEC)

# ---------------- italic, not bold, standalone labels over their own prose ----------------
for r in G:
    a = r[0]
    if a in claimed or a in IN_TABLE:
        continue
    p = page(a)
    if (4 <= p <= 88 or 114 <= p <= 169) and italic(a) and not bold(a) and single(a) and size(a) >= 10.0 \
            and left(a) <= 90 and 1 <= words(a) <= 8 and not text(a).endswith(('.', ',')) and opens_prose(a) \
            and not re.match(r'(In|Expressed in|As of|For the)\b', text(a)) and starts_block(a):
        add(section_of(p), [a], H, "S053_Q2_PRECEDENT_ITALIC_LABEL",
            "10pt italic standalone label at the margin over its own prose: " + P53 + " (S053_Q2, function decides, not weight)", SEC)

# ---------------- numbered captions: false by the caption rule ----------------
for r in G:
    a = r[0]
    if a in claimed or not bold(a) or size(a) < 9.0:
        continue
    if CAPTION_RX.match(text(a)) or re.match(r'K8\.1$', text(a)):
        add(section_of(page(a)), [a], N, "NUMBERED_CAPTION",
            "numbered table/figure/box title naming the object below it, whatever its size: " + POL + " caption rule; " + P33 + " (P3); " + P41, CAPTION)
for a in sorted(CAPTION_TAILS):
    add(section_of(page(a)), [a], N, "CAPTION_CONTINUATION", "second line of the table caption above it", CAPTION)

# ---------------- set-apart non-headings ----------------
for r in G:
    a = r[0]
    if a in claimed or a in TAIL:
        continue
    p, t, s = page(a), text(a), size(a)
    if 100 <= p <= 113 and not bold(a) and single(a) and words(a) <= 12 and (italic(a) and s >= 9.5 or s >= 12.0):
        add("STATEMENTS", [a], N, "STATEMENT_SUBTITLE", "unit or period line under a statement title: " + P42, META("TEMPORAL_METADATA", "FINANCIAL_STATEMENT"))
    elif 100 <= p <= 113 and bold(a) and italic(a) and t.startswith("The Notes to Financial Statements"):
        add("STATEMENTS", [a], N, "STATEMENT_FOOTER", "the statement's closing reference to the notes: " + P53, FURN)
    elif p >= 2 and bold(a) and s >= 9.0 and not re.match(r'[\d\s,.\-–—$()%•]+$', t) and len(t) > 1:
        add(section_of(p), [a], N, "CHART_OR_TABLE_INTERNAL",
            "bold label inside a table, chart or box (row / group label, beside other cells, or over table rows rather than prose): "
            + POL + " (table row labels); " + P42, CHART)

json.dump({"items": items}, open(OUT, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
c = collections.Counter(i["verdict"] for i in items)
print(dict(c))
print(collections.Counter(i["pattern"] for i in items if i["verdict"] == H))
print(collections.Counter(i["pattern"] for i in items if i["verdict"] == N))
print([i["text"] for i in items if i["verdict"] == Q])
