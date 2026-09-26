"""SRC089_SOURCE_ONLY_GOLD_REVIEW_V1 - reviewer's working tool.

Reads only the original PDF's structured-lane atoms with their layout facts (atom-layout-facts.tsv) and what their glyphs
say (atom-glyph-facts.tsv). Never the engine's blind proposals, never a converted DOCX, never the outline JSON; no Gold or
count of this source exists. Decree 195/2013/ND-CP (English translation): Times New Roman 12pt throughout. A title block
(bold 'DECREE' over two regular capital lines), five chapters ('Chapter I' bold at the margin over its bold capital
title, centred, on the next line), 26 articles ('Article N.' bold, then the article's title in regular weight, wrapping
onto lines that begin in lower case), clauses ('1 .') and points ('a/') as body, a signature block, a footnote.
Decisions the user took before (the financial policy, SRC-041/053/054/095) are applied as precedent where they apply;
three patterns have none and are AMBIGUOUS.
"""
import json, re, sys, collections

SP = sys.argv[1]
OUT = sys.argv[2]
L = [l.split('\t') for l in open(SP + '/atom-layout-facts.tsv', encoding='utf-8').read().split('\n') if l]
G = {r[0]: r for r in (l.split('\t') for l in open(SP + '/atom-glyph-facts.tsv', encoding='utf-8').read().split('\n') if l)}
A = {r[0]: r for r in L}
ORDER = [r[0] for r in L]

page = lambda a: int(A[a][1])
text = lambda a: A[a][10].strip()
left = lambda a: float(A[a][7])


def bold_glyphs(a):
    return sum(int(f.rsplit(':', 1)[1]) for f in G[a][5].split(',') if ':' in f and 'Bold' in f)


items = []
claimed = set()


def whole(a):
    return {"sourceAlias": a, "selectionMode": "WHOLE_ALIAS", "verbatimText": None, "occurrence": None}


def verbatim(a, t):
    assert t in text(a), (a, t)
    return {"sourceAlias": a, "selectionMode": "VERBATIM_TEXT", "verbatimText": t, "occurrence": None}


def add(section, aliases, verdict, pattern, reason, axes, parts=None):
    for a in aliases:
        assert a not in claimed, a
        claimed.add(a)
    parts = parts or [whole(a) for a in aliases]
    items.append({"section": section, "parts": parts, "text": ' '.join(p["verbatimText"] or text(p["sourceAlias"]) for p in parts),
                  "verdict": verdict, "pattern": pattern, "reason": reason, "axes": axes, "page": page(aliases[0])})


def ax(functions, primary, scope, roles=("REGION_OPENER",), title="TITLE", info=None):
    return {"semanticFunctions": list(functions), "primaryFunction": primary, "scope": scope,
            "occurrenceRoles": list(roles), "titleRelation": title, "informationType": info}


SECT = ax(["IDENTITY", "STRUCTURE"], "STRUCTURE", "SECTION")
PART = ax(["IDENTITY", "STRUCTURE"], "IDENTITY", "DOCUMENT_PART")
DOC = ax(["IDENTITY"], "IDENTITY", "DOCUMENT")
BODY = ax(["INFORMATION"], "INFORMATION", "SECTION", ("BODY",), "NONE")
NOTE = ax(["INFORMATION"], "INFORMATION", "NOTE", ("BODY",), "NONE")
FURN = ax(["INFORMATION"], "INFORMATION", "DOCUMENT", ("PAGE_FURNITURE",), "NONE")
META = lambda info, scope="DOCUMENT": ax(["INFORMATION"], "INFORMATION", scope, ("METADATA",), "NONE", info)
POL = "FINANCIAL_PROCUREMENT_HEADING_POLICY_V1 (its general definition: part / section / subsection / appendix headings)"
H, N, Q = "HEADING", "NON_HEADING", "AMBIGUOUS"
CLAUSE = re.compile(r'^(?:\d+ ?\.|[a-zđ]/)\s')

# ---------------- title block (p1): no precedent ----------------
add("FRONT", ["L0006:S0", "L0007:S0", "L0008:S0"], Q, "S089_Q1_DECREE_TITLE_BLOCK",
    "the title block: 'DECREE' (bold, centred) over 'DETAILING A NUMBER OF ARTICLES OF, AND MEASURES FOR IMPLEMENTING,' and "
    "'THE PUBLICATION LAW (*)' (regular capitals, centred; '(*)' a footnote mark). Proposed: one title of three lines, the "
    "document's name being 'Decree detailing ... the Publication Law', without the footnote mark. Alternatives: 'DECREE' alone "
    "a document-type label (metadata, like 'RFC 9114' in S095_Q1) and the two lines below the title; or 'DECREE' the title and "
    "the two lines a subtitle", DOC,
    parts=[whole("L0006:S0"), whole("L0007:S0"), verbatim("L0008:S0", "THE PUBLICATION LAW")])

# ---------------- chapters: no precedent ----------------
for r in L:
    a = r[0]
    if text(a).startswith("Chapter ") and bold_glyphs(a) > 0:
        nxt = ORDER[ORDER.index(a) + 1]
        assert bold_glyphs(nxt) > 0 and text(nxt).isupper(), nxt
        add("BODY", [a, nxt], Q, "S089_Q2_CHAPTER_LABEL_OVER_TITLE",
            "'" + text(a) + "' bold at the margin, its title '" + text(nxt) + "' bold capitals centred on the next line. Proposed: one "
            "chapter heading of two parts (a wrapped two-line title is one claim, SRC-029 PART_3; here the lines are a label and "
            "its title). Alternative: two claims, the chapter label and the chapter title", PART)

# ---------------- articles: 'Article N.' bold lead + title, wrapped onto lower-case lines ----------------
for i, a in enumerate(ORDER):
    if a in claimed or not re.match(r'^Article \d', text(a)) or not A[a][11].startswith("Article"):
        continue
    parts = [a]
    k = i + 1
    while k < len(ORDER) and left(ORDER[k]) < 80 and re.match(r'^\(?[a-z]', text(ORDER[k])) and not CLAUSE.match(text(ORDER[k])):
        parts.append(ORDER[k])
        k += 1
    add("BODY", parts, H, "ARTICLE_HEADING",
        "article heading: 'Article N.' in bold and the article's title in regular weight" + (", wrapped onto " + str(len(parts) - 1) +
        " line(s) that begin in lower case" if len(parts) > 1 else "") + ", over its clauses: " + POL + "; a numbered section heading "
        "with its title is one claim (SRC-095 NUMBERED_SECTION)", SECT)

# ---------------- clause labels: no precedent ----------------
for a in ORDER:
    t = text(a)
    if a not in claimed and CLAUSE.match(t) and t.endswith(':') and len(t.split()) <= 5:
        add("BODY", [a], Q, "S089_Q3_COLON_CLAUSE_LABEL",
            "a numbered clause whose whole text is a short noun phrase ending in a colon ('" + t + "'), over the points or text of "
            "that clause; plain 12pt like the body. Proposed: not a heading - a clause, the legal unit below the article, whose "
            "text introduces its points (colon-ended labels are meaning decisions, bucket A). Alternative: a sub-heading of its article", BODY)

# ---------------- set-apart non-headings ----------------
add("FRONT", ["L0000:S0", "L0001:S0"], N, "TRANSLATION_NOTICE",
    "'Unofficial Translation - For Reference Purposes Only', bold italic 14pt at the top right: a status notice about the document, not a heading",
    META("STATUS_METADATA"))
add("FRONT", ["L0002:S0"], N, "ISSUER_HEADER", "'THE GOVERNMENT', the issuing body, top left: issuer metadata, as the issuer lines of SRC-041/042/053 (P2)",
    META("ISSUER_METADATA"))
add("FRONT", ["L0002:S1", "L0003:S1"], N, "NATIONAL_HEADER",
    "'THE SOCIALIST REPUBLIC OF VIETNAM' over 'Independence - Freedom - Happiness': the national heading and motto every Vietnamese legal document carries - metadata",
    META("ISSUER_METADATA"))
for a in ["L0003:S0", "L0004:S0"]:
    add("FRONT", [a], N, "RULE_LINE", "a bold row of dashes under the header: a rule, not text", FURN)
add("FRONT", ["L0005:S0"], N, "NUMBER_AND_DATE", "'No. 195/2013/ND-CP Hanoi, November 21, 2013': the document's number, place and date - metadata (S095_Q1: an identifier is not a heading)",
    META("DOCUMENT_IDENTIFIER"))
add("BACK", ["L0576:S0", "L0577:S0", "L0578:S0"], N, "SIGNATURE_BLOCK",
    "'ON BEHALF OF THE GOVERNMENT / PRIME MINISTER / Nguyen Tan Dung', bold at the right after the last article: the signature block - authorship metadata, as the SRC-095 author line",
    META("AUTHORSHIP_METADATA"))
add("BACK", ["L0579:S0"], N, "FOOTNOTE", "the footnote '(*)' naming the Official Gazette issue: a note", NOTE)

# every atom with bold glyphs is accounted for: a claimed item, or the bold lead of an article line
unaccounted = [a for a in ORDER if bold_glyphs(a) > 0 and a not in claimed]
assert not unaccounted, unaccounted

json.dump({"items": items}, open(OUT, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
print(dict(collections.Counter(i["verdict"] for i in items)))
print(collections.Counter(i["pattern"] for i in items))
