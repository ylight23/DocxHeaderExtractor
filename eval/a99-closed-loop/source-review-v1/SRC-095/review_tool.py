"""SRC095_SOURCE_ONLY_GOLD_REVIEW_V1 - reviewer's working tool.

Reads only the original PDF's structured-lane atoms with what their glyphs say (atom-glyph-facts.tsv). Never the
engine's blind proposals, never a converted DOCX; no Gold or count of this source exists. RFC 9114 (HTTP/3) as rendered
by xml2rfc: NotoSerif, body 10pt; sections 14.4pt bold, subsections 12pt bold, sub-subsections 10pt bold, all numbered
("4.1.", "A.2.") - where the renderer sets the number apart the number and the title are two segments of one row and
one claim of two parts. Front and back matter sections (Abstract, Status of This Memo, Copyright Notice, Table of
Contents, Acknowledgments, Index, Author's Address) are set as sections. Decisions the user took before (the financial
policy, SRC-041 A5, SRC-053/054) are applied as precedent where they apply; two patterns have none and are AMBIGUOUS.
"""
import json, re, sys, collections

SP = sys.argv[1]
OUT = sys.argv[2]
G = [l.split('\t') for l in open(SP + '/atom-glyph-facts.tsv', encoding='utf-8').read().split('\n') if l]
A = {r[0]: r for r in G}


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
left = lambda a: float(A[a][6])
ROWSEG = collections.Counter((r[1], r[2]) for r in G)
single = lambda a: ROWSEG[(A[a][1], A[a][2])] == 1
NUMBER = re.compile(r'^(?:\d+|[A-Z])(?:\.\d+)*\.$')          # a section number standing alone ("4.6.", "A.")
NUMBERED = re.compile(r'^(?:\d+|[A-Z]|Appendix [A-Z])(?:\.\d+)*\.\s+\S')

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
TOC = ax(["IDENTITY", "STRUCTURE"], "IDENTITY", "TOC")
DOC = ax(["IDENTITY"], "IDENTITY", "DOCUMENT")
NAV = ax(["STRUCTURE"], "STRUCTURE", "TOC", ("NAVIGATION",), "NONE")
CHART = ax(["INFORMATION"], "INFORMATION", "TABLE", ("LOCAL_LABEL",), "NONE")
BODY = ax(["INFORMATION"], "INFORMATION", "SECTION", ("BODY",), "NONE")
META = lambda info, scope="DOCUMENT": ax(["INFORMATION"], "INFORMATION", scope, ("METADATA",), "NONE", info)
POL = "FINANCIAL_PROCUREMENT_HEADING_POLICY_V1 (its general definition: part / section / subsection / appendix headings)"
P41 = "precedent SRC-041 (user-decided 2026-09-25)"
H, N, Q = "HEADING", "NON_HEADING", "AMBIGUOUS"


def section_of(p):
    return "FRONT" if p <= 4 else "BODY" if p <= 52 else "BACK"


# ---------------- title block (p1): no precedent ----------------
add("FRONT", ["L0007:S0"], N, "S095_Q1_RFC_TITLE_BLOCK",
    "user decision 2026-09-26 (S095_Q1): 'RFC 9114' is the document identifier, not a heading: METADATA / DOCUMENT_IDENTIFIER",
    META("DOCUMENT_IDENTIFIER"))
add("FRONT", ["L0008:S0"], H, "S095_Q1_RFC_TITLE_BLOCK",
    "user decision 2026-09-26 (S095_Q1): 'HTTP/3' is the document title: IDENTITY / DOCUMENT / TITLE", DOC)

# ---------------- front and back matter sections ----------------
for a in ["L0009:S0", "L0015:S0", "L0023:S0", "L1878:S0", "L1912:S0", "L2076:S0"]:
    add(section_of(page(a)), [a], H, "MATTER_SECTION", "14.4pt bold section title of the front or back matter over its own text: " + POL, SECT)
add("FRONT", ["L0034:S0"], H, "TOC_OPENER", "opens the contents list: " + P41, TOC)
add("BACK", ["L2077:S0"], N, "AUTHOR_LINE", "the author's name under 'Author's Address': authorship metadata, set bold as a name", META("AUTHORSHIP_METADATA", "SECTION"))

# ---------------- numbered sections: whole lines, and number | title split rows ----------------
for r in G:
    a = r[0]
    if a in claimed or page(a) <= 4 or not bold(a) or left(a) > 70:
        continue
    t = text(a)
    if single(a) and NUMBERED.match(t) and size(a) >= 10.0:
        level = t.split(' ')[0].rstrip('.').count('.') + 1
        add(section_of(page(a)), [a], H, "NUMBERED_SECTION",
            f"numbered section heading ({size(a):g}pt bold, level {level}) over its own text: " + POL, SECT if level == 1 else SEC)
    elif not single(a) and NUMBER.match(t) and A[a][3] == '0':
        title = a.replace(':S0', ':S1')
        if title in A and bold(title):
            add(section_of(page(a)), [a, title], H, "NUMBERED_SECTION_SPLIT",
                "a section number and its title set apart in two segments of one row: one heading of two parts: " + POL, SEC)
    elif single(a) and t.startswith("Appendix ") and size(a) >= 14:
        add(section_of(page(a)), [a], H, "APPENDIX_TITLE", "appendix title: " + POL, PART)

# ---------------- index group letters: no precedent ----------------
for r in G:
    a = r[0]
    if a in claimed or page(a) < 54:
        continue
    if single(a) and re.fullmatch(r'[A-Z]', text(a)) and not bold(a):
        add("BACK", [a], H, "S095_Q2_INDEX_GROUP_LETTER",
            "user decision 2026-09-26 (S095_Q2, TRUE): a single letter opening its own group of indented index entries, like the "
            "SRC-041 A5 contents group label", ax(["STRUCTURE"], "STRUCTURE", "TOC"))

# ---------------- set-apart non-headings ----------------
for r in G:
    a = r[0]
    if a in claimed:
        continue
    t = text(a)
    if page(a) <= 4 and 2 <= page(a) and size(a) == 10.0 and single(a) and (NUMBERED.match(t) or t.startswith("Appendix ")
                                                                           or t in ("Acknowledgments", "Index", "Author's Address")):
        add("FRONT", [a], N, "CONTENTS_ENTRY", "contents entry pointing to its section: " + POL + " (navigation entries)", NAV)
    elif bold(a) and size(a) < 10.0 and page(a) > 4:
        add(section_of(page(a)), [a], N, "BOILERPLATE_EMPHASIS", "9pt text with bold key words (BCP 14 boilerplate): body text", BODY)
    elif bold(a) and not single(a):
        add(section_of(page(a)), [a], N, "TABLE_OR_REFERENCE_LABEL",
            "a bold cell of a table header row, or a reference tag beside its citation: " + POL + " (table labels)", CHART)

json.dump({"items": items}, open(OUT, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
c = collections.Counter(i["verdict"] for i in items)
print(dict(c))
print(collections.Counter(i["pattern"] for i in items if i["verdict"] == H))
print(collections.Counter(i["pattern"] for i in items if i["verdict"] == N))
print([i["text"] for i in items if i["verdict"] == Q])
