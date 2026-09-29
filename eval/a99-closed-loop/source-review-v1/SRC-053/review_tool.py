"""SRC053_SOURCE_ONLY_GOLD_REVIEW_V1 - reviewer's working tool.

Reads only the original PDF's structured-lane atoms with what their glyphs say (atom-glyph-facts.tsv: effective point
size and font names per atom) and the lane's own line facts (atom-layout-facts.tsv). Never the engine's blind
proposals, never SRC-053's Gold file or any count it holds, never a converted DOCX. Emits the reviewer's candidate
list: HEADING, NON_HEADING (only occurrences that are set apart), AMBIGUOUS - each with sourceParts.

This PDF sets its type size through the text matrix (Tf 1), so the lane's line facts read size 1.0 and no bold for
every atom. The glyph facts carry the real typography: Times-Roman / Times-Bold / Times-Italic / Times-BoldItalic,
body 10pt (9pt in tables and in the statements, 8pt on the summary pages). Levels are read by function - a standalone
label at the text margin over its own prose or list - not by a size taken from another document. Decisions the user
took on SRC-042, SRC-041, SRC-029 and DOC-0133, and the frozen financial policy, are applied as precedent and say so.
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
bold = lambda a: share(a, ("Bold",)) >= 0.5
italic = lambda a: share(a, ("Italic",)) >= 0.5
left = lambda a: float(A[a][6])
ROWSEG = collections.Counter((r[1], r[2]) for r in G)
single = lambda a: ROWSEG[(A[a][1], A[a][2])] == 1
words = lambda a: len(text(a).split())
CAPTION_RX = re.compile(r'(Table|Figure|Box)\s+[A-Z]?\d+(?:\.\d+)*')
LEADERS = re.compile(r'(\.\s){4,}')

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
LIST_ITEM = ax(["INFORMATION"], "INFORMATION", "LIST", ("LOCAL_LABEL",), "NONE")
NAV = ax(["STRUCTURE"], "STRUCTURE", "TOC", ("NAVIGATION",), "NONE")
FURN = ax(["INFORMATION"], "INFORMATION", "DOCUMENT", ("PAGE_FURNITURE",), "NONE")
BODY = ax(["INFORMATION"], "INFORMATION", "SECTION", ("BODY",), "NONE")
META = lambda info, scope="DOCUMENT": ax(["INFORMATION"], "INFORMATION", scope, ("METADATA",), "NONE", info)
P29 = "precedent SRC-029 (user-decided 2026-09-25)"
P33 = "precedent DOC-0133 (user-decided 2026-09-25)"
P41 = "precedent SRC-041 (user-decided 2026-09-25)"
P42 = "precedent SRC-042 (user-decided 2026-09-25)"
POL = "FINANCIAL_PROCUREMENT_HEADING_POLICY_V1"
H, N, Q = "HEADING", "NON_HEADING", "AMBIGUOUS"
U = "user decision 2026-09-26"


def section_of(p):
    return ("FRONT" if p <= 3 else "MDA" if p <= 68 else "FS_PART" if p <= 77 else "STATEMENTS" if p <= 91
            else "NOTES" if p <= 131 else "BACK")


def opens_prose(a):
    """The first following atom that is not bold is a line of running prose (a single segment, 6+ words, no leaders)."""
    i = IDX[a] + 1
    while i < len(G) and bold(G[i][0]) and i - IDX[a] <= 3:
        i += 1
    if i >= len(G):
        return False
    b = G[i][0]
    return single(b) and words(b) >= 6 and not LEADERS.search(text(b)) and left(b) <= 110


def opens_list(a):
    """A colon-ended label over running items: the next atom is a single-segment line of 4+ words with no leaders."""
    b = G[IDX[a] + 1][0]
    return single(b) and words(b) >= 4 and not LEADERS.search(text(b)) and left(b) <= 110


# ---------------- cover (p1) ----------------
add("FRONT", ["L0000:S0"], H, "S053_Q3_COVER_TITLE_ABOVE_LARGER_ISSUER",
    U + " (S053_Q3, title only): 14pt bold 'Information Statement' is the cover title, one claim of one line; the 24pt issuer "
    "below it is metadata, as in the SRC-042 precedent, whatever its size", DOC)
add("FRONT", ["L0001:S0"], N, "S053_Q3_COVER_TITLE_ABOVE_LARGER_ISSUER",
    U + " (S053_Q3): the 24pt issuer name under the cover title is issuer metadata, not a title part", META("ISSUER_METADATA"))
add("FRONT", ["L0040:S0"], H, "FRONT_SECTION_TITLE",
    "centered bold caps title of the cover page's own section over its paragraph; the back-cover contents list names it (p1)", SECT)
add("FRONT", ["L0049:S0"], N, "COVER_BLOCK", "date of the information statement: " + P42, META("TEMPORAL_METADATA"))
add("FRONT", ["L0050:S0"], H, "FRONT_SECTION_TITLE", "centered bold caps title of the summary pages; the contents list names it (p2)", SECT)
add("FRONT", ["L0051:S0"], N, "SECTION_SUBTITLE", "bold-italic 'as of' line under the summary title: " + P42 + " (period metadata)",
    META("TEMPORAL_METADATA", "SECTION"))
add("FRONT", ["L0121:S0"], N, "QUALIFICATION_NOTE", "8pt bold centered note closing the summary ('The above information is qualified ...')", BODY)
add("FRONT", ["L0122:S0"], N, "QUALIFICATION_NOTE", "second line of the same note", BODY)

# ---------------- explicit occurrence decisions ----------------
add("MDA", ["L1080:S0"], N, "S042_A1_PRECEDENT_BOX_TITLE",
    "title of the box the section 'Financing Principles' (just above) sets out: an object title, not a second opener of that region: "
    + P42 + " (S042_A1, revised)", ax(["IDENTITY"], "IDENTITY", "EMBEDDED_ARTIFACT", ("CAPTION",)))
for a, why in [("L0572:S0", "the WHR sub-window under GROW (its sibling windows are 10pt bold)"),
               ("L2128:S0", "'Interest Rate Risk' under the 10.5pt 'Market Risk'"),
               ("L2188:S0", "'Exchange Rate Risk' under the 10.5pt 'Market Risk'")]:
    add("MDA", [a], H, "S053_Q2_ITALIC_STANDALONE_LABEL",
        U + " (S053_Q2, TRUE): a 10pt italic standalone label at the margin over its own prose - " + why + ". Function decides, "
        "not weight", SEC)
add("NOTES", ["L4804:S0"], H, "NOTE_SUBHEADING", "10pt bold-italic standalone label over its own prose (Note H): " + P33 + " (P6)", SEC)
add("MDA", ["L0828:S0"], N, "S053_Q4_BULLETED_LABEL_OVER_SUBLIST",
    "the third bullet of a list whose other bullets are run-in items ('Activities directly funded ...: These are ...'); this one is only "
    "'Other Adjustments:' and its content is an enumerated sub-list (i., ii.). " + U + " (S053_Q4, FALSE): a list item among list items; "
    "its sub-list is the item's own content",
    LIST_ITEM)
add("MDA", ["L1718:S0"], N, "RUN_IN_LEAD", "bold committee name that runs on into its sentence across the line: body prose with a bold lead", BODY)
add("FS_PART", ["L2694:S0"], N, "PAGE_NOTICE", "'This Page intentionally left blank': " + P42, FURN)

# ---------------- appendix, financial statements part (p67-77) ----------------
add("FS_PART", ["L2696:S0"], N, "PART_TITLE_BLOCK", "issuer above the part title: " + P42, META("ISSUER_METADATA", "DOCUMENT_PART"))
add("FS_PART", ["L2697:S0", "L2698:S0"], H, "PART_TITLE_BLOCK", "title of the financial statements part, two lines, on its contents page: " + P42, PART)
add("FS_PART", ["L2699:S0"], N, "PART_TITLE_BLOCK", "period date under the part title (18pt): " + P42, META("TEMPORAL_METADATA", "DOCUMENT_PART"))
add("FS_PART", ["L2709:S0"], H, "A5_PRECEDENT_CONTENTS_GROUP_LABEL",
    "'Supplementary Information' opening a sub-group of indented contents entries: " + P41 + " (A5); " + P42, TOC)
add("FS_PART", ["L2717:S0"], H, "EMBEDDED_REPORT", "title of management's assurance letter (centered, regular weight): " + P42, EMB)
add("FS_PART", ["L2759:S0", "L2760:S0"], H, "EMBEDDED_REPORT", "title of management's ICFR report, two lines (centered, regular weight): " + P42, EMB)
add("FS_PART", ["L2817:S0", "L2818:S0"], H, "EMBEDDED_REPORT", "title of the auditor's ICFR report, two lines: " + P42, EMB)
add("FS_PART", ["L2878:S0"], H, "EMBEDDED_REPORT", "the auditor's report title: " + P42, EMB)
for a in ["L2809:S0", "L2870:S0"]:
    add("FS_PART", [a], N, "SIGNATURE", "the audit firm's signature under its report", META("AUTHORSHIP_METADATA", "EMBEDDED_ARTIFACT"))

# ---------------- body headings, read by function ----------------
WRAPS = {"L3690:S0": ["L3691:S0"], "L3930:S0": ["L3931:S0"], "L4908:S0": ["L4909:S0"]}
TAIL = {t for v in WRAPS.values() for t in v}
CAPTION_TAILS = {"L1602:S0", "L4109:S0", "L5129:S0"}
FIGURE_INTERNAL = {"L0534:S0", "L0535:S0", "L1202:S0", "L1214:S0", "L1865:S0", "L2180:S0", "L2313:S0"}

for r in G:
    a = r[0]
    if a in claimed or a in TAIL:
        continue
    p, t, s = page(a), text(a), size(a)
    if not (2 <= p <= 68 or 70 <= p <= 77 or 92 <= p <= 131):
        continue
    if not bold(a) or not single(a) or s < 9.0 or a in FIGURE_INTERNAL or a in CAPTION_TAILS:
        continue
    sec = section_of(p)
    if t.startswith("SECTION ") and s >= 12.0:
        add(sec, [a], H, "MDA_SECTION_TITLE", "12pt bold section title (centered): " + P41 + "; " + P42, SECT)
        continue
    if left(a) > 95:
        continue
    if CAPTION_RX.match(t):
        continue
    if t.startswith("NOTE ") or t == "PURPOSE AND AFFILIATED ORGANIZATIONS":
        add(sec, [a], H, "NOTE_TITLE", "note title opening a complete note: " + POL + "; " + P41, NOTE)
    elif t == "NOTES TO FINANCIAL STATEMENTS":
        add(sec, [a], H, "NOTES_PART", "opens the notes: " + P42, NOTES_PART)
    elif s >= 10.5 or opens_prose(a) or (t.endswith(":") and opens_list(a)):
        pattern = "NOTE_SUBHEADING" if sec == "NOTES" else "FS_SUBHEADING" if sec == "FS_PART" else "MDA_SUBHEADING"
        add(sec, [a] + WRAPS.get(a, []), H, pattern,
            f"{s:g}pt bold standalone label at the margin over its own prose or list: " + P33 + " (P6); " + P41 + "; " + P42, SEC)

# ---------------- statements (p78-91) ----------------
for aliases in [["L2955:S0"], ["L3026:S0"], ["L3059:S0"], ["L3072:S0"], ["L3113:S0"], ["L3171:S0"], ["L3317:S0", "L3318:S0"]]:
    add("STATEMENTS", aliases, H, "STATEMENT_TITLE", "title of a financial statement: " + P33 + "; " + P42, STATEMENT)
for aliases in [["L2985:S0"], ["L3224:S0"], ["L3277:S0"], ["L3366:S0", "L3367:S0"], ["L3414:S0", "L3415:S0"], ["L3461:S0", "L3462:S0"], ["L3509:S0", "L3510:S0"]]:
    add("STATEMENTS", aliases, H, "A2_PRECEDENT_REPEATED_TITLE",
        "a statement's title repeated at the top of its next page: a display-title occurrence of the continuing statement: " + P41 + " (A2); " + P42, STATEMENT)

# ---------------- back cover (p132) ----------------
add("BACK", ["L5185:S0"], H, "S053_Q1_BACK_COVER_TITLE_BLOCK",
    U + " (S053_Q1, TRUE): 10pt bold centered 'Information Statement' atop the back cover is a display-title occurrence of the document; "
    "repetition does not decide heading status", DOC)
add("BACK", ["L5186:S0"], N, "COVER_BLOCK", "issuer under the back-cover title: " + P42, META("ISSUER_METADATA"))
add("BACK", ["L5220:S0"], H, "TOC_OPENER", "opens the contents list: " + P42, TOC)

# ---------------- numbered captions: false by the caption rule ----------------
for r in G:
    a = r[0]
    if a in claimed or not bold(a) or size(a) < 9.0:
        continue
    if CAPTION_RX.match(text(a)):
        add(section_of(page(a)), [a], N, "NUMBERED_CAPTION",
            "numbered table/figure/box title naming the object below it, whatever its size: " + POL + " caption rule; " + P33 + " (P3); " + P41, CAPTION)
for a in ["L1602:S0", "L4109:S0", "L5129:S0"]:
    add(section_of(page(a)), [a], N, "CAPTION_CONTINUATION", "second line of the table caption above it", CAPTION)

# ---------------- set-apart non-headings ----------------
for r in G:
    a = r[0]
    if a in claimed or a in TAIL:
        continue
    p, t, s = page(a), text(a), size(a)
    if 78 <= p <= 91 and not bold(a) and single(a) and words(a) <= 12 and (italic(a) and s >= 9.5 or s >= 12.0):
        add("STATEMENTS", [a], N, "STATEMENT_SUBTITLE", "unit or period line under a statement title: " + P42, META("TEMPORAL_METADATA", "FINANCIAL_STATEMENT"))
    elif 78 <= p <= 91 and bold(a) and italic(a) and t.startswith("The Notes to Financial Statements"):
        add("STATEMENTS", [a], N, "STATEMENT_FOOTER", "the statement's closing reference to the notes", FURN)
    elif p == 70 and bold(a) and s >= 9.0 and A[a][3] == '0':
        add("FS_PART", [a], N, "CONTENTS_ENTRY", "contents entry with its page number: " + P42, NAV)
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
