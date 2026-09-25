"""SRC029_SOURCE_ONLY_GOLD_REVIEW_V1 - reviewer's working tool.

Reads only the PDF's structured-lane atoms with their layout facts (dumped by the diagnostics test),
never the engine proposals, never the old Gold or the retired DOCX. Emits the reviewer's candidate
list: CLEAR_HEADING, CLEAR_NON_HEADING (only those set apart), AMBIGUOUS, each with sourceParts.
"""
import json, re, sys, collections

SP = sys.argv[1]
OUT = sys.argv[2]
R = [l.split('\t') for l in open(SP + '/atom-layout-facts.tsv', encoding='utf-8').read().split('\n') if l]
A = {r[0]: r for r in R}
IDX = {r[0]: i for i, r in enumerate(R)}
page = lambda a: int(A[a][1])
text = lambda a: A[a][10].strip()
bold = lambda a: float(A[a][5]) >= 0.5
prefix = lambda a: A[a][11].strip()
size = lambda a: float(A[a][4])
left = lambda a: float(A[a][7])

items = []
claimed = set()


def whole(a):
    return {"sourceAlias": a, "selectionMode": "WHOLE_ALIAS", "verbatimText": None, "occurrence": None}


def atom_span(a, t):
    """The stretch of the atom's own text that reads t (spacing aside), so the binder finds it verbatim."""
    raw = A[a][10]
    if t in raw:
        return t
    want = re.sub(r'\s+', '', t)
    for start in range(len(raw)):
        got = ''
        for end in range(start, len(raw)):
            if not raw[end].isspace():
                got += raw[end]
            if got == want:
                return raw[start:end + 1]
            if not want.startswith(got):
                break
    raise AssertionError((a, t, raw))


def verb(a, t):
    span = atom_span(a, t)
    part = {"sourceAlias": a, "selectionMode": "VERBATIM_TEXT", "verbatimText": span, "occurrence": None}
    if A[a][10].count(span) > 1:
        part["occurrence"] = 1                          # the label is the atom's lead: its first occurrence
    return part


def add(section, parts, verdict, pattern, reason, axes, expected=None):
    for p in parts:
        assert p["sourceAlias"] not in claimed, p
        claimed.add(p["sourceAlias"])
    t = ' '.join(p["verbatimText"] or text(p["sourceAlias"]) for p in parts)
    if expected is not None:
        assert re.sub(r'\s+', '', t) == re.sub(r'\s+', '', expected), (t, expected)
    items.append({"section": section, "parts": parts, "text": t, "verdict": verdict, "pattern": pattern,
                  "reason": reason, "axes": axes, "page": page(parts[0]["sourceAlias"])})


def ax(functions, primary, scope, roles=("REGION_OPENER",), title="TITLE", info=None):
    return {"semanticFunctions": list(functions), "primaryFunction": primary, "scope": scope,
            "occurrenceRoles": list(roles), "titleRelation": title, "informationType": info}


SEC = ax(["STRUCTURE"], "STRUCTURE", "SECTION")
PART = ax(["IDENTITY", "STRUCTURE"], "STRUCTURE", "DOCUMENT_PART")
SECT = ax(["IDENTITY", "STRUCTURE"], "STRUCTURE", "SECTION")
FORM = ax(["IDENTITY", "STRUCTURE"], "IDENTITY", "FORM")
TOC = ax(["IDENTITY", "STRUCTURE"], "IDENTITY", "TOC")
DOC = ax(["IDENTITY"], "IDENTITY", "DOCUMENT")
EMB = ax(["IDENTITY"], "IDENTITY", "EMBEDDED_ARTIFACT")
CLAUSE = ax(["STRUCTURE"], "STRUCTURE", "CLAUSE")
CAPTION = ax(["IDENTITY"], "IDENTITY", "TABLE", ("CAPTION",))
META = lambda info: ax(["INFORMATION"], "INFORMATION", "DOCUMENT", ("METADATA",), "NONE", info)
REV = ax(["INFORMATION", "STRUCTURE"], "INFORMATION", "REVISION_ENTRY", ("REGION_OPENER",), "NONE", "TEMPORAL_METADATA")
P23 = "precedent DOC-0123 (user-decided 2026-09-24/25)"
U = "user decision 2026-09-25: "
H, N, Q = "HEADING", "NON_HEADING", "AMBIGUOUS"

# ---------------- front matter ----------------
add("FRONT", [whole("L0000:S0"), whole("L0000:S1"), whole("L0001:S0"), whole("L0002:S0"), whole("L0003:S0"), whole("L0004:S0"), whole("L0005:S0")],
    H, "COVER_TITLE_BLOCK", "cover title block: " + P23, DOC,
    "STANDARD PROCUREMENT DOCUMENT Request for Proposals Works Design and Build (Single-Stage Request for Proposals, after Initial Selection)")
add("FRONT", [whole("L0006:S0"), whole("L0007:S0"), whole("L0008:S0")], N, "S029_A1_COVER_APPLICABILITY_QUALIFIER",
    "user decision 2026-09-25: applicability metadata: says which projects the document is for, identifies no new artifact and opens no region; not part of the cover title claim",
    META("APPLICABILITY_METADATA"))
add("FRONT", [whole("L0009:S0")], N, "CONNECTIVE", "'AND' joining two qualifiers", META(None))
add("FRONT", [whole("L0012:S0")], N, "COVER_DATE", "cover date: " + P23 + " (cover date is metadata)", META("TEMPORAL_METADATA"))
add("FRONT", [whole("L0017:S0")], H, "SECTION_OPENER", "opens the revision history: " + P23, SEC, "Revisions")
for a in ["L0018:S0", "L0023:S0"]:
    add("FRONT", [whole(a)], H, "REVISION_DATE_OPENER", "revision date opening its revision note: " + P23, REV)
add("FRONT", [whole("L0027:S0")], H, "SECTION_OPENER", "opens the preface: " + P23, SEC, "Preface")
add("FRONT", [whole("L0104:S0")], H, "DOCUMENT_TITLE", "title page of the SPD: " + P23, DOC)
add("FRONT", [whole("L0105:S0")], H, "SECTION_OPENER", "opens the summary: " + P23, SEC)
add("FRONT", [whole("L0106:S0")], H, "SUMMARY_ENTRY", "summary entry heading its explanatory block: " + P23, SEC)
add("FRONT", [whole("L0107:S0"), whole("L0108:S0")], H, "SUMMARY_ENTRY", "summary entry of two lines: " + P23, SEC)
for a in ["L0113:S0", "L0136:S0", "L0146:S0"]:
    add("FRONT", [whole(a)], H, "SUMMARY_PART_LABEL", "summary part label heading its block: " + P23, PART)
for a in ["L0114:S0", "L0120:S0", "L0124:S0", "L0127:S0", "L0130:S0", "L0133:S0", "L0137:S0", "L0147:S0", "L0153:S0", "L0159:S0"]:
    add("FRONT", [whole(a)], H, "SUMMARY_SECTION_LABEL", "summary section label heading its explanatory block: " + P23, SECT)
add("FRONT", [whole("L0163:S0")], H, "EMBEDDED_TITLE", "opens the notice template: " + P23, EMB)
add("FRONT", [whole(a) for a in ["L0164:S0", "L0165:S0", "L0166:S0", "L0167:S0"]], H, "EMBEDDED_TITLE_BLOCK",
    "the notice's title block, one claim of four parts: " + P23, EMB)
add("FRONT", [whole(a) for a in ["L0259:S0", "L0260:S0", "L0261:S0", "L0262:S0"]], H, "DOCUMENT_TITLE",
    "cover title of the RFP document proper: " + P23, DOC)
add("FRONT", [whole("L0263:S0")], N, "FIELD_LABEL", "'Procurement of:' field", ax(["INFORMATION"], "INFORMATION", "FORM_FIELD", ("FIELD_LABEL",), "NONE"))
add("FRONT", [whole("L0273:S0")], H, "TOC_OPENER", "opens the table of contents: " + P23, TOC)

# ---------------- parts and sections (large type) ----------------
add("PART_1", [whole("L0288:S0")], H, "PART_TITLE", "part title", PART)
add("SECTION_I", [whole("L0290:S0")], H, "SECTION_TITLE", "section title", SECT)
add("SECTION_I", [whole("L0291:S0")], H, "TOC_OPENER", "opens the section's contents: " + P23, TOC)
add("SECTION_I", [whole("L0360:S0")], H, "SECTION_TITLE", "section title repeated after its contents: " + P23, SECT)
for a in ["L0361:S0", "L0570:S0", "L0677:S0", "L1009:S0", "L1085:S0", "L1136:S0", "L1175:S0", "L1260:S0", "L1314:S0", "L1422:S0", "L1497:S0"]:
    add("SECTION_I", [whole(a)], H, "ITP_GROUP", "ITP group heading A-K", SEC)

# ITP clauses: the margin column's label, bold whole atoms or the bold lead of an atom fused with body text.
def label_of(a):
    if prefix(a):
        return prefix(a)                                 # the bold lead of an atom fused with body text
    if bold(a):
        return re.sub(r'\s+\d+\.\d+$', '', text(a))    # "7. Clarification of 7.1" -> label + body sub-clause number
    return 

itp = []
cur = None
for r in R:
    a = r[0]
    if not (16 <= page(a) <= 46) or left(a) >= 112 or a in claimed or size(a) >= 13:
        continue
    lab = label_of(a)
    if lab and re.match(r'^\d+\.\s*\S', lab):
        cur = [a]
        itp.append(cur)
    elif lab and cur is not None and page(a) == page(cur[-1]) and IDX[a] - IDX[cur[-1]] <= 3:
        cur.append(a)
    else:
        cur = None
for group in itp:
    parts = [whole(a) if label_of(a) == text(a) else verb(a, label_of(a)) for a in group]
    add("SECTION_I", parts, H, "ITP_CLAUSE_TITLE", "numbered ITP clause title in the margin column: " + P23, CLAUSE)

# Section II - PDS
add("SECTION_II", [whole("L1601:S0")], H, "SECTION_TITLE", "section title", SECT)
add("SECTION_II", [verb("L1611:S0", "A. General")], H, "PDS_GROUP", "PDS group heading, sharing its atom with the column header 'Reference': " + P23, SEC)
for a in ["L1638:S0", "L1672:S0", "L1813:S0", "L1840:S0", "L1857:S0", "L1889:S0", "L1895:S0", "L1941:S0", "L1951:S0"]:
    add("SECTION_II", [whole(a)], H, "PDS_GROUP", "PDS group heading: " + P23, SEC)

# Section III
add("SECTION_III", [whole("L1976:S0")], H, "SECTION_TITLE", "section title", SECT)
add("SECTION_III", [whole("L1977:S0")], H, "TOC_OPENER", "opens the local contents: " + P23, TOC)
for a in ["L1986:S0", "L1987:S0", "L1988:S0", "L1991:S0", "L2001:S0", "L2009:S0", "L2014:S0", "L2027:S0", "L2032:S0",
          "L2061:S0", "L2062:S0", "L2091:S0", "L2094:S0", "L2105:S0", "L2124:S0", "L2130:S0", "L2146:S0", "L2148:S0"]:
    add("SECTION_III", [whole(a)], H, "CRITERIA_HEADING", "evaluation-criteria heading", SEC)

# Section IV - forms
add("SECTION_IV", [whole("L2170:S0")], H, "SECTION_TITLE", "section title", SECT)
add("SECTION_IV", [whole("L2171:S0")], H, "TOC_OPENER", "opens the forms list: " + P23, TOC)
form_titles = ["L2222:S0", "L2223:S0", "L2312:S0", "L2381:S0", "L2382:S0", "L2425:S0", "L2507:S0", "L2539:S0", "L2641:S0", "L2657:S0",
               "L2667:S0", "L2678:S0", "L2688:S0", "L2703:S0", "L2717:S0", "L2745:S0", "L2756:S0", "L2786:S0", "L2816:S0", "L2829:S0",
               "L2950:S0", "L2966:S0", "L2971:S0", "L2979:S0", "L2987:S0", "L2989:S0", "L3026:S0", "L3027:S0", "L3074:S0", "L3084:S0",
               "L3086:S0", "L3115:S0", "L3207:S0", "L3307:S0", "L3325:S0", "L3336:S0", "L3340:S0", "L3376:S0"]
for a in form_titles:
    add("SECTION_IV", [whole(a)], H, "FORM_TITLE", "form or form-group title", FORM)
for pair in [("L2738:S0", "L2739:S0"), ("L3075:S0", "L3076:S0"), ("L3140:S0", "L3141:S0"), ("L3265:S0", "L3266:S0"), ("L3411:S0", "L3412:S0")]:
    add("SECTION_IV", [whole(a) for a in pair], H, "FORM_TITLE", "form title wrapped over two lines, one claim", FORM)
for a in ["L2432:S0", "L2447:S0", "L2467:S0", "L2515:S0", "L2527:S0"]:
    add("SECTION_IV", [whole(a)], N, "TABLE_CAPTION", "names the one table below it: " + P23, CAPTION)
for a in ["L2468:S0", "L2496:S0"]:
    add("SECTION_IV", [whole(a)], N, "TABLE_CAPTION", "'Table: Alternative ...' caption: " + P23, CAPTION)
for a in ["L2978:S0", "L2988:S0", "L3025:S0", "L3085:S0", "L3114:S0", "L3139:S0", "L3206:S0", "L3264:S0", "L3306:S0", "L3324:S0"]:
    add("SECTION_IV", [whole(a)], N, "FORM_CODE", "form code line above the form title: " + P23, ax(["IDENTITY"], "IDENTITY", "FORM", ("METADATA",), "NONE"))
for a in ["L2548:S0", "L2557:S0", "L2590:S0", "L2614:S0"]:
    add("SECTION_IV", [whole(a)], H, "FORM_LOCAL_HEADING", "daywork local heading: " + P23, SEC)
add("SECTION_IV", [whole("L2842:S0")], H, "EMBEDDED_TITLE", "title of the embedded code of conduct: " + P23, EMB)
for a in ["L2857:S0", "L2893:S0", "L2911:S0"]:
    add("SECTION_IV", [whole(a)], H, "FORM_LOCAL_HEADING", "code of conduct local heading: " + P23, SEC)
add("SECTION_IV", [whole("L2926:S0"), whole("L2927:S0"), whole("L2928:S0")], H, "FORM_LOCAL_HEADING",
    "attachment heading of three lines, one claim: " + P23, SEC)
add("SECTION_IV", [whole("L2939:S0")], H, "LIST_OPENER", "bold numbered subheading opening its examples: " + P23,
    ax(["STRUCTURE"], "STRUCTURE", "LIST", ("REGION_OPENER", "LOCAL_LABEL")))
add("SECTION_IV", [whole("L2930:S0")], H, "S029_A2_LIST_LEAD_IN_SIBLING",
    "user decision 2026-09-25: opens a coherent list region; sentence-like wording does not make it body prose",
    ax(["STRUCTURE"], "STRUCTURE", "LIST", ("REGION_OPENER", "LOCAL_LABEL")))
add("SECTION_IV", [whole("L3052:S0")], H, "FORM_LOCAL_HEADING", "opens the declaration part of the resume form: " + P23, SEC)
add("SECTION_IV", [whole("L3216:S0")], H, "S029_A3_INNER_FORM_TITLE",
    "user decision 2026-09-25: the declaration's own title at the form boundary; a repeat keeps its heading status",
    ax(["IDENTITY"], "IDENTITY", "EMBEDDED_ARTIFACT"))
add("SECTION_IV", [whole("L3275:S0")], H, "S029_A3_INNER_FORM_TITLE",
    "user decision 2026-09-25: the declaration's title; the claim is the title line only",
    ax(["IDENTITY"], "IDENTITY", "EMBEDDED_ARTIFACT"))
add("SECTION_IV", [whole("L3276:S0"), whole("L3277:S0")], N, "S029_A3_INNER_FORM_TITLE",
    "user decision 2026-09-25: qualification lines under the declaration title ('in accordance with ... Document'): context, not part of the title",
    META(None))

# Sections V, VI
add("SECTION_V", [whole("L3440:S0")], H, "SECTION_TITLE", "section title", SECT)
add("SECTION_V", [whole("L3441:S0"), whole("L3442:S0")], H, "SECTION_SUBHEADING", "the section's only subheading, two lines: " + P23, SEC)
add("SECTION_VI", [whole("L3450:S0")], H, "SECTION_TITLE", "section title", SECT)
add("SECTION_VI", [whole("L3451:S0")], N, "EDITORIAL_NOTE", "bracketed instruction: " + P23, ax(["INFORMATION"], "INFORMATION", "NOTE", ("BODY_CONTENT",), "NONE"))
for a in ["L3452:S0", "L3455:S0"]:
    add("SECTION_VI", [whole(a)], H, "NUMBERED_HEADING", "numbered F&C heading: " + P23, SEC)

# Part 2, Section VII
add("PART_2", [whole("L3533:S0")], H, "PART_TITLE", "part title", PART)
add("SECTION_VII", [whole("L3535:S0")], H, "SECTION_TITLE", "section title", SECT)
add("SECTION_VII", [whole("L3536:S0")], H, "TOC_OPENER", "opens the section's contents: " + P23, TOC)
for a in ["L3546:S0", "L3686:S0", "L3820:S0", "L3823:S0", "L3840:S0", "L3884:S0", "L3889:S0", "L3891:S0"]:
    add("SECTION_VII", [whole(a)], H, "ER_HEADING", "employer's requirements heading", SEC)
add("SECTION_VII", [whole("L3547:S0")], H, "SECTION_OPENER", "opens the preparation notes: " + P23, SEC)
for a in ["L3759:S0", "L3762:S0", "L3809:S0", "L3812:S0"]:
    add("SECTION_VII", [whole(a)], H, "ES_REQUIREMENT_HEADING", "ES requirement heading: " + P23, SEC)
for a in ["L3766:S0", "L3779:S0", "L3796:S0"]:
    add("SECTION_VII", [whole(a)], H, "S029_A4_BULLETED_SUBHEADING",
        "user decision 2026-09-25: standalone label opening its own paragraph; the bullet is not a decision gate",
        ax(["STRUCTURE"], "STRUCTURE", "SECTION", ("REGION_OPENER", "LOCAL_LABEL")))
add("SECTION_VII", [whole("L3843:S0")], N, "TABLE_TITLE_REPEAT", "repeats the heading above and names the table: " + P23, CAPTION)
for a in ["L3851:S0", "L3861:S0"]:
    add("SECTION_VII", [whole(a)], N, "TABLE_GROUP_LABEL", "row-group label inside the personnel table: " + P23,
        ax(["STRUCTURE"], "STRUCTURE", "TABLE", ("LOCAL_LABEL",), "NONE"))

# Part 3, VIII, IX
add("PART_3", [whole("L3893:S0"), whole("L3894:S0")], H, "PART_TITLE", "part title of two lines", PART)
add("SECTION_VIII", [whole("L3896:S0")], H, "SECTION_TITLE", "section title", SECT)
add("SECTION_VIII", [whole("L3908:S0")], N, "METADATA", "FIDIC publisher line: " + P23, META(None))
add("SECTION_IX", [whole("L3916:S0")], H, "SECTION_TITLE", "section title", SECT)
for a in ["L3920:S0", "L5966:S0", "L6052:S0", "L6190:S0"]:
    add("SECTION_IX", [whole(a)], H, "REPEATED_DOCUMENT_TITLE", "'Particular Conditions' opening its Part: " + P23, DOC)
for a in ["L3921:S0", "L4193:S0", "L5967:S0", "L6053:S0"]:
    add("SECTION_IX", [whole(a)], H, "PART_HEADING", "part heading", PART)
add("SECTION_IX", [whole("L6191:S0"), whole("L6192:S0")], H, "PART_HEADING", "part heading of two lines", PART)
for a in ["L4173:S0", "L4181:S0"]:
    add("SECTION_IX", [whole(a)], N, "TABLE_CAPTION", "'Table: Summary of ...' caption: " + P23, CAPTION)

# PC Part B: "Sub-Clause n" and its title lines in the left column (bold, or the bold lead of an atom fused with body text).
INSTRUCTION = re.compile(r'\b(are added|is replaced|is deleted|The following)\b')
pc = []
cur = None
for r in R:
    a = r[0]
    if not (156 <= page(a) <= 197) or left(a) >= 112 or a in claimed or IDX[a] >= IDX["L5880:S0"]:
        continue
    lab = prefix(a) or (text(a) if bold(a) else '')
    if lab and INSTRUCTION.search(lab):
        cur = None
        continue
    if lab and re.match(r'^Sub-?Clause\s+[\d.]', lab):
        cur = [(a, lab)]
        pc.append(cur)
    elif lab and cur is not None and IDX[a] - IDX[cur[-1][0]] <= 3 and page(a) == page(cur[-1][0]):
        cur.append((a, lab))
    else:
        cur = None
fused = {"L5718:S0": "Intellectual and Industrial"}   # a title line fused with the body's closing quotation
# Titles continued at the top of the next page: named explicitly, never discovered by proximity.
continued = {"Sub-Clause 14.3": ["L5523:S0", "L5524:S0"], "Sub-Clause 21.10": ["L5850:S0", "L5851:S0", "L5852:S0"]}
for group in pc:
    parts = []
    for a, lab in group:
        lab = fused.get(a, lab)
        parts.append(whole(a) if lab == text(a) else verb(a, lab))
    head = parts[0]["verbatimText"] or text(parts[0]["sourceAlias"])
    reason = "PC sub-clause number and title: " + P23
    if head in continued:
        parts += [whole(a) for a in continued[head]]
        reason += "; " + U + "the title continues at the top of the next page (explicit parts)"
    add("SECTION_IX", parts, H, "PC_SUBCLAUSE_TITLE", reason, CLAUSE)
for a, t in [("L4808:S1", None), ("L4819:S0", None), ("L4828:S0", None)]:
    add("SECTION_IX", [whole(a)], H, "NUMBERED_SUBCLAUSE_HEADING", "numbered sub-clause heading: " + P23, SEC)
nxt = R[IDX["L4840:S0"] + 1][0]
add("SECTION_IX", [whole("L4840:S0"), whole(nxt)], H, "NUMBERED_SUBCLAUSE_HEADING", "numbered sub-clause heading of two lines: " + P23, SEC)

# DAAB appendix (its GC modification table) and the rest of Part B
add("SECTION_IX", [whole("L5880:S0")], H, "APPENDIX_HEADING", "appendix heading: " + P23, EMB)
add("SECTION_IX", [verb("L5881:S0", "Title")], H, "DAAB_CLAUSE_LABEL", "DAAB clause label fused with its modification text: " + P23, CLAUSE)
add("SECTION_IX", [verb("L5884:S0", "1. Definitions")], H, "DAAB_CLAUSE_LABEL", "DAAB clause label fused with its modification text (not bold in the PDF): " + P23, CLAUSE)
add("SECTION_IX", [whole("L5908:S0"), whole("L5908:S1")], H, "DAAB_CLAUSE_LABEL", "DAAB clause number and label in adjacent cells: " + P23, CLAUSE)
num3 = [r[0] for r in R if r[0].startswith("L5909:") and r[0] != "L5909:S1"]
add("SECTION_IX", ([whole(num3[0])] if num3 else []) + [verb("L5909:S1", "Warranties")], H, "DAAB_CLAUSE_LABEL",
    "DAAB clause label fused with its modification text: " + P23, CLAUSE)
for a, t in [("L5934:S0", "7. Confidentiality"), ("L5937:S0", "9. Fees and Expenses")]:
    add("SECTION_IX", [verb(a, t)], H, "DAAB_CLAUSE_LABEL", "DAAB clause label fused with its modification text: " + P23, CLAUSE)
add("SECTION_IX", [whole("L5941:S0"), whole("L5942:S0")], H, "DAAB_CLAUSE_LABEL", "DAAB clause label of two lines", CLAUSE)
for a in ["L5943:S0", "L5961:S0"]:
    add("SECTION_IX", [whole(a)], H, "DAAB_CLAUSE_LABEL", "names the DAAB document the following rows modify, like the clause labels above it", EMB)

# Parts C-E
add("SECTION_IX", [whole("L5968:S0")], N, "EDITORIAL_NOTE", "parenthetical instruction: " + P23, ax(["INFORMATION"], "INFORMATION", "NOTE", ("BODY_CONTENT",), "NONE"))
for a in ["L5969:S1", "L5972:S1"]:
    row = [r[0] for r in R if r[0].startswith(a.split(':')[0] + ":") and r[0] != a]
    add("SECTION_IX", ([whole(row[0])] if row else []) + [whole(a)], H, "NUMBERED_HEADING", "numbered F&C heading: " + P23, SEC)
add("SECTION_IX", [whole("L6054:S0")], H, "LIST_OPENER", "opens the metrics list: " + P23, SEC)
add("SECTION_IX", [whole("L6058:S0")], H, "LIST_OPENER", "opens the regular-reporting metrics (not bold): " + P23, SEC)
add("SECTION_IX", [whole("L6199:S0")], H, "FORM_LOCAL_HEADING", "declaration heading inside Part E: " + P23, SEC)

# Section X
add("SECTION_X", [whole("L6238:S0")], H, "SECTION_TITLE", "section title", SECT)
add("SECTION_X", [whole("L6239:S0")], H, "TOC_OPENER", "opens the forms list: " + P23, TOC)
for a in ["L6249:S0", "L6393:S0", "L6455:S0", "L6476:S0", "L6516:S0", "L6557:S0"]:
    add("SECTION_X", [whole(a)], H, "FORM_TITLE", "contract form title", FORM)
add("SECTION_X", [whole("L6264:S0")], H, "REPEATED_FORM_TITLE", "the notification's own title at the form boundary: " + P23, EMB)
for pair in [("L6608:S0", "L6609:S0"), ("L6650:S0", "L6651:S0"), ("L6700:S0", "L6701:S0")]:
    add("SECTION_X", [whole(a) for a in pair], H, "FORM_TITLE", "form title with its subtitle as a title part, one claim: " + P23, FORM)
add("SECTION_X", [whole("L6278:S0")], H, "NOTIFICATION_LOCAL_HEADING", "numbered local heading of the notification: " + P23, SEC)
add("SECTION_X", [verb("L6284:S0", "2. Other Proposers")], H, "NOTIFICATION_LOCAL_HEADING", "numbered local heading fused with its instruction: " + P23, SEC)
add("SECTION_X", [verb("L6313:S0", "3. Reason/s why your Proposal was unsuccessful")], H, "NOTIFICATION_LOCAL_HEADING", "numbered local heading fused with its instruction: " + P23, SEC)
for a in ["L6320:S0", "L6345:S0", "L6376:S0"]:
    add("SECTION_X", [whole(a)], H, "NOTIFICATION_LOCAL_HEADING", "numbered local heading of the notification: " + P23, SEC)
add("SECTION_X", [whole("L6414:S0")], H, "FORM_LOCAL_HEADING", "opens the ownership details: " + P23, SEC)
add("SECTION_X", [whole("L6515:S0")], N, "STRAY_MARK", "a lone dash", META(None))

json.dump({"items": items}, open(OUT, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
c = collections.Counter(i["verdict"] for i in items)
print(c, "ITP clauses", len(itp), "PC sub-clauses", len(pc))
nums = sorted(int(re.match(r'(\d+)', ' '.join(p['verbatimText'] or text(p['sourceAlias']) for p in g and [whole(x) if label_of(x) == text(x) else verb(x, label_of(x)) for x in g])).group(1)) for g in itp)
print("ITP numbers missing:", sorted(set(range(1, 56)) - set(nums)), "dups:", [n for n, k in collections.Counter(nums).items() if k > 1])
