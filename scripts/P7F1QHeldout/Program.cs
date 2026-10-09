using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.V5Qualification.P7;

// P7-F1Q held-out gate (Issue #6). Source-only preparation: contamination audit, page selection, review bundles.
// No provider calls, no Gold read, no model predictions read. Rules are declared in code before results are seen
// and are committed with their output.
if (args.Length < 1) throw new ArgumentException("audit | pages | review (see README)");
const string PoolPath = "artifacts/web-pdf-semantic-diagnostic/p7.d2.source-pool.v1.json";
var json = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

// Issue #6 Decision 1: the 24 candidates, by stratum, in the issue's order.
var candidates = new (string Stratum, string Id)[]
{
    ("legal","002"),("legal","006"),("legal","009"),("legal","015"),("legal","017"),
    ("procurement","028"),("procurement","030"),("procurement","035"),
    ("finance","043"),("finance","049"),("finance","051"),
    ("textbook","056"),("textbook","058"),("textbook","060"),("textbook","067"),
    ("minutes","073"),("minutes","074"),("minutes","077"),
    ("translation","083"),("translation","087"),("translation","088"),("translation","090"),
    ("rfc","093"),("rfc","094"),
};
var strataCategory = new Dictionary<string, string> { ["legal"] = "01_phap_quy", ["procurement"] = "02_hop_dong_mua_sam", ["finance"] = "03_tai_chinh_ke_toan",
    ["textbook"] = "04_giao_trinh", ["minutes"] = "05_bien_ban_hop", ["translation"] = "06_dich_song_ngu", ["rfc"] = "07_system_generated" };
// Development / exposed exclusions (Issue #6): Giấy mời, D01–D05 (012, 029, 054, 080, 092), SRC-089, SRC-095.
var devIds = new[] { "012", "029", "054", "080", "092", "089", "095" };
// Predeclared seen-family map (Issue #6): stratum -> development instances of the same formatting family.
var seenFamily = new Dictionary<string, string[]> { ["legal"] = ["012"], ["procurement"] = ["029"], ["finance"] = ["054"],
    ["minutes"] = ["080"], ["translation"] = ["089"], ["rfc"] = ["092", "095"], ["textbook"] = [] };

using var pool = JsonDocument.Parse(File.ReadAllBytes(PoolPath));
var sources = pool.RootElement.GetProperty("sources").EnumerateArray()
    .Select(s => (Key: s.GetProperty("sourceKey").GetString()!, Sha: s.GetProperty("sha256").GetString()!, Category: s.GetProperty("category").GetString()!))
    .Where(s => s.Key.StartsWith("todo10_8/", StringComparison.Ordinal)).ToArray();
string IdOf(string key) => Path.GetFileName(key)[..3];
(string Key, string Sha, string Category) Src(string id) => sources.Single(s => IdOf(s.Key) == id);

switch (args[0])
{
    case "audit":
    {
        if (args.Length != 2) throw new ArgumentException("audit <new-audit.json>");
        Need(!File.Exists(args[1]), "OUTPUT_EXISTS");
        var codex = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "diagnostic-captures");
        var codexText = Directory.Exists(codex) ? Directory.EnumerateFiles(codex, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".json") || f.EndsWith(".html")).Select(f => (Path: f, Size: new FileInfo(f).Length)).Where(f => f.Size < 50_000_000).ToArray() : [];
        var modelRunReconciliation = JsonDocument.Parse(File.ReadAllBytes("eval/harness-lift/model-exposure-reconciliation.v1.json")).RootElement
            .GetProperty("rows").EnumerateArray().Where(r => r.GetProperty("modelRunOccurredInDocument").GetBoolean())
            .Select(r => r.GetProperty("referenceId").GetString()!).ToArray();
        var devTexts = devIds.Select(id => (Id: id, Shingles: Shingles(Text(Src(id).Key)))).ToArray();
        var cohortTexts = new Dictionary<string, HashSet<string>>();

        object Audit(string id, string stratum)
        {
            var s = Src(id); var stem = Path.GetFileNameWithoutExtension(s.Key);
            var hits = GitGrep(s.Sha, stem).Where(p => !p.StartsWith("todo10_8/") && !p.Contains("p7.d2.source-pool")).ToArray();
            var classified = hits.Select(p => (Path: p, Class: Classify(p))).ToList();
            if (modelRunReconciliation.Any(r => r.Contains(stem, StringComparison.Ordinal)))
                classified.Add(("eval/harness-lift/model-exposure-reconciliation.v1.json#modelRunOccurredInDocument", "MODEL_PREDICTION_EXPOSURE"));
            foreach (var f in codexText.Where(f => File.ReadAllText(f.Path).Contains(stem, StringComparison.Ordinal) || File.ReadAllText(f.Path).Contains(s.Sha, StringComparison.Ordinal)))
                classified.Add(("~/.codex/diagnostic-captures/" + Path.GetRelativePath(codex, f.Path).Replace('\\', '/'), ClassifyCodex(f.Path)));
            var text = Text(s.Key); var shingles = Shingles(text); cohortTexts[id] = shingles;
            var nearDev = devTexts.Select(d => (d.Id, J: Jaccard(shingles, d.Shingles))).OrderByDescending(x => x.J).First();
            var reasons = new List<string>();
            if (text.Trim().Length == 0) reasons.Add("NO_PARSER_TEXT_LAYER");
            if (classified.Any(c => c.Class == "F1_FAMILY_EXPOSURE")) reasons.Add("F1_FAMILY_EXPOSURE");
            if (classified.Any(c => c.Class == "MODEL_PREDICTION_EXPOSURE")) reasons.Add("MODEL_PREDICTION_EXPOSURE");
            if (nearDev.J >= NearDuplicateJaccard) reasons.Add("NEAR_DUPLICATE_OF_DEVELOPMENT_" + nearDev.Id);
            return new
            {
                id, stratum, sourceKey = s.Key, sourceSha256 = s.Sha, verdict = reasons.Count == 0 ? "ELIGIBLE" : "REJECTED", rejectReasons = reasons,
                exposureTags = classified.Select(c => c.Class).Distinct().Order(),
                exposures = classified.GroupBy(c => c.Class).ToDictionary(g => g.Key, g => g.Select(x => x.Path).Distinct().Order().ToArray()),
                maxDevelopmentShingleJaccard = new { development = nearDev.Id, jaccard = Math.Round(nearDev.J, 4) },
                family = seenFamily[stratum].Length == 0 ? "UNSEEN_FAMILY" : "SEEN_FAMILY_IN_DEVELOPMENT:" + string.Join(",", seenFamily[stratum]),
            };
        }

        var rows = new List<object>(); var final = new List<object>(); var replacements = new List<object>();
        var used = candidates.Select(c => c.Id).Concat(devIds).ToHashSet();
        foreach (var (stratum, id) in candidates)
        {
            var row = Audit(id, stratum); rows.Add(row);
            if (Verdict(row) == "ELIGIBLE") { final.Add(new { id, stratum, origin = "ISSUE_6_CANDIDATE" }); continue; }
            // Predeclared deterministic reserve order: same category, not yet used, sha256("P7F1Q-HELDOUT-RESERVE-V1|" + sha) ascending.
            var reserve = sources.Where(x => x.Category == strataCategory[stratum] && !used.Contains(IdOf(x.Key)))
                .OrderBy(x => Hex(SHA256.HashData(Encoding.UTF8.GetBytes("P7F1Q-HELDOUT-RESERVE-V1|" + x.Sha))), StringComparer.Ordinal).ToArray();
            var tried = new List<object>(); string? chosen = null;
            foreach (var r in reserve)
            {
                var rid = IdOf(r.Key); used.Add(rid);
                var rr = Audit(rid, stratum); tried.Add(rr);
                if (Verdict(rr) == "ELIGIBLE") { chosen = rid; break; }
            }
            replacements.Add(new { rejected = id, stratum, reserveOrder = reserve.Select(x => IdOf(x.Key)), tried, chosen });
            if (chosen is not null) final.Add(new { id = chosen, stratum, origin = "RESERVE_REPLACEMENT_FOR_" + id });
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = "P7_F1Q_HELDOUT_CONTAMINATION_AUDIT_V2", issue = "ylight23/DocxHeaderExtractor#6",
            rules = new
            {
                reject = new[] { "F1_FAMILY_EXPOSURE: document text in any F1/F1Q request, response, capture or F1 Gold",
                    "MODEL_PREDICTION_EXPOSURE: the document (PDF or its DOCX twin) was the input of a recorded LLM run (semantic runs, harness-lift model runs, provider raw)",
                    $"NEAR_DUPLICATE: word 5-shingle Jaccard >= {NearDuplicateJaccard} against any development document",
                    "NO_PARSER_TEXT_LAYER: the parser yields no occurrences (image-only PDF), so F1 has no input" },
                amendment = "V2 adds NO_PARSER_TEXT_LAYER after V1 showed candidate 002 yields 0 parser occurrences; a parseability precondition, not a result-dependent choice. V1 is kept unchanged.",
                tagOnly = new[] { "HUMAN_REFERENCE_PRIOR (earlier human keys/Gold/review packets, not F1)", "P7_SOURCE_REVIEW (P7 D2 source/shape screening, no labels, no model)",
                    "ENGINE_OR_INVENTORY (deterministic engine runs, inventories, documentation)" },
                search = "git grep on HEAD for source sha256 and file stem (stem also matches the DOCX twin), plus ~/.codex/diagnostic-captures json/html",
                reserveOrder = "same category, unused, sha256('P7F1Q-HELDOUT-RESERVE-V1|' + sourceSha256) ascending, first ELIGIBLE",
                familyMap = seenFamily, exclusions = devIds.Append("USER_REFERENCE_PDF"),
            },
            candidates = rows, replacements, finalCohort = final, finalCount = final.Count,
            external25 = new { status = "NOT_MET_PENDING_INDEPENDENT_ADMINISTRATIVE_PDF", note = "No independent administrative letter/invitation is available; not imputed from the exposed Giấy mời." },
            providerCalls = 0, goldRead = false, modelPredictionsRead = false,
        }, json);
        File.WriteAllBytes(args[1], bytes);
        Console.WriteLine(JsonSerializer.Serialize(new { final = final.Count, rejected = rows.Count(r => Verdict(r) != "ELIGIBLE"), sha = Hex(SHA256.HashData(bytes)) }));
        break;
    }
    case "pages":
    {
        // Issue #6 Decision 2: first page + two seeded interior pages (one per half) + up to one source-identified
        // contents/table page; documents with < 4 pages use all pages. Source-only; no Gold or predictions.
        if (args.Length != 3) throw new ArgumentException("pages <audit.json> <new-page-selection.json>");
        Need(!File.Exists(args[2]), "OUTPUT_EXISTS");
        var auditBytes = File.ReadAllBytes(args[1]);
        using var audit = JsonDocument.Parse(auditBytes);
        var docs = new List<object>();
        foreach (var f in audit.RootElement.GetProperty("finalCohort").EnumerateArray())
        {
            var id = f.GetProperty("id").GetString()!; var s = Src(id);
            Need(Hex(SHA256.HashData(File.ReadAllBytes(s.Key))) == s.Sha, "SOURCE_SHA_DRIFT:" + id);
            var parsed = PdfSourceAdapter.BuildWithDetails(s.Key); var src = parsed.Snapshot;
            var store = PdfSourceEvidenceStore.Build(src, parsed.Details);
            var pages = src.Atoms.Select(a => a.Page).Distinct().Order().ToArray();
            var selected = new List<(int Page, string Stratum, string Rule)>();
            if (pages.Length < 4) selected.AddRange(pages.Select(p => (p, "ALL_PAGES", "DOCUMENT_UNDER_4_PAGES")));
            else
            {
                selected.Add((pages[0], "FIRST", "FIRST_PAGE"));
                var interior = pages.Skip(1).ToArray(); var half = (interior.Length + 1) / 2;
                var seed = SHA256.HashData(Encoding.UTF8.GetBytes("P7F1Q-HELDOUT-PAGES-V1|" + s.Sha));
                int Pick(int[] range, int offset) => range[(int)(BitConverter.ToUInt64(seed, offset) % (ulong)range.Length)];
                selected.Add((Pick(interior[..half], 0), "SEEDED_INTERIOR", "SHA256_SEEDED_FIRST_HALF"));
                selected.Add((Pick(interior[half..], 8), "SEEDED_INTERIOR", "SHA256_SEEDED_SECOND_HALF"));
                var structured = StructuredPage(src, store, selected.Select(x => x.Page).ToHashSet());
                if (structured is { } sp) selected.Add((sp.Page, "TARGETED_STRUCTURE", sp.Rule));
            }
            var packs = P7PilotRequestPreflight.SelectPacks(src, parsed.Details, selected.Select(x => x.Page).ToArray());
            var issued = packs.Sum(p => p.Owned.Count);
            docs.Add(new
            {
                id, stratum = f.GetProperty("stratum").GetString(), sourceKey = s.Key, sourceSha256 = s.Sha,
                sourceAliasUniverseSha256 = src.SourceAliasUniverseHash, evidenceStoreSha256 = store.StoreSha256, pageCount = pages.Length,
                selectedPages = selected.OrderBy(x => x.Page).Select(x => new { page = x.Page, stratum = x.Stratum, rule = x.Rule,
                    occurrences = src.Atoms.Count(a => a.Page == x.Page) }),
                goldOccurrences = src.Atoms.Count(a => selected.Any(x => x.Page == a.Page)),
                packs = packs.Select(p => p.PackId[(p.PackId.LastIndexOf(':') + 1)..]), packIssuedOccurrences = issued,
                v3Requests = packs.Sum(p => (p.Owned.Count + P7F1QProtocolV2.ChunkSize - 1) / P7F1QProtocolV2.ChunkSize),
            });
        }
        var totalRequests = docs.Sum(d => JsonSerializer.SerializeToElement(d).GetProperty("v3Requests").GetInt32());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = "P7_F1Q_HELDOUT_PAGE_SELECTION_V1", auditSha256 = Hex(SHA256.HashData(auditBytes)),
            rule = new
            {
                firstPage = "lowest page with parser occurrences",
                seededInterior = "pages after the first split into halves (first half gets the extra page); index = uint64(sha256('P7F1Q-HELDOUT-PAGES-V1|' + sourceSha256)[0..8] or [8..16]) mod half length",
                targetedStructure = "lowest unselected page with >= 5 lines ending in dot leaders or a trailing page number after text, or a 'contents'/'mục lục' line followed by such lines (CONTENTS); else lowest unselected page with >= 3 baselines that each hold >= 3 parser segments (TABLE); else none",
                shortDocuments = "< 4 pages: all pages",
                gold = "every parser occurrence on every selected page gets a three-class F1 label; other occurrences of the issued packs stay UNKNOWN",
            },
            documents = docs, totalGoldOccurrences = docs.Sum(d => JsonSerializer.SerializeToElement(d).GetProperty("goldOccurrences").GetInt32()),
            totalV3Requests = totalRequests, providerCalls = 0, goldRead = false,
        }, json);
        File.WriteAllBytes(args[2], bytes);
        Console.WriteLine(JsonSerializer.Serialize(new { documents = docs.Count, totalRequests, sha = Hex(SHA256.HashData(bytes)) }));
        break;
    }
    case "review":
    {
        // Review bundle: every parser occurrence on every selected page, with parser layout, page by page.
        // Source facts only; no labels, no predictions. Also a plain-text dump for reading.
        if (args.Length != 4) throw new ArgumentException("review <page-selection.json> <new-review-dir> <new-text-dump-dir>");
        Need(!Directory.Exists(args[2]) && !Directory.Exists(args[3]), "OUTPUT_EXISTS");
        Directory.CreateDirectory(args[2]); Directory.CreateDirectory(args[3]);
        var selBytes = File.ReadAllBytes(args[1]); using var sel = JsonDocument.Parse(selBytes);
        foreach (var d in sel.RootElement.GetProperty("documents").EnumerateArray())
        {
            var id = d.GetProperty("id").GetString()!; var key = d.GetProperty("sourceKey").GetString()!;
            var parsed = PdfSourceAdapter.BuildWithDetails(key); var src = parsed.Snapshot;
            Need(src.SourceAliasUniverseHash == d.GetProperty("sourceAliasUniverseSha256").GetString(), "UNIVERSE_DRIFT:" + id);
            var store = PdfSourceEvidenceStore.Build(src, parsed.Details);
            var tools = P7F1QEvidenceTools.FromStore(store, new Dictionary<string, string> { ["R"] = src.Atoms[0].Alias }, 2);
            var pages = d.GetProperty("selectedPages").EnumerateArray().Select(p => (Page: p.GetProperty("page").GetInt32(), Stratum: p.GetProperty("stratum").GetString()!)).ToArray();
            var rows = pages.SelectMany(p => src.Atoms.Where(a => a.Page == p.Page).OrderBy(a => a.Ordinal).Select(a => new
            {
                sourceAlias = a.Alias, page = a.Page, ordinal = a.Ordinal, pageStratum = p.Stratum, text = a.Text, layout = tools.Layout(a.Alias),
            })).ToArray();
            File.WriteAllBytes(Path.Combine(args[2], $"{id}.review.json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = "P7_F1Q_HELDOUT_REVIEW_BUNDLE_V1", id, sourceKey = key, sourceSha256 = src.SourceSha256,
                sourceAliasUniverseSha256 = src.SourceAliasUniverseHash, evidenceStoreSha256 = store.StoreSha256,
                pageSelectionSha256 = Hex(SHA256.HashData(selBytes)), pageStats = pages.Select(p => tools.PageStats(p.Page)),
                layoutBasis = P7F1QEvidenceTools.LayoutBasis, occurrences = rows, labels = "NONE_SOURCE_FACTS_ONLY",
            }, json));
            var sb = new StringBuilder();
            sb.AppendLine($"# {id} {key}");
            foreach (var p in pages)
            {
                var st = JsonSerializer.SerializeToElement(tools.PageStats(p.Page));
                sb.AppendLine($"## page {p.Page} ({p.Stratum}) extent={st.GetProperty("textExtent")} median={st.GetProperty("medianFontSize")}");
                foreach (var a in src.Atoms.Where(a => a.Page == p.Page).OrderBy(a => a.Ordinal))
                {
                    var l = JsonSerializer.SerializeToElement(tools.Layout(a.Alias));
                    string box = l.ValueKind == JsonValueKind.Object && l.GetProperty("bbox").ValueKind == JsonValueKind.Array
                        ? string.Join(",", l.GetProperty("bbox").EnumerateArray().Select(v => v.GetDouble().ToString("0"))) : "-";
                    string font = l.ValueKind == JsonValueKind.Object && l.GetProperty("font").ValueKind == JsonValueKind.Object
                        ? $"{l.GetProperty("font").GetProperty("size")}{(l.GetProperty("font").GetProperty("boldRatio").ValueKind == JsonValueKind.Number && l.GetProperty("font").GetProperty("boldRatio").GetDouble() >= 0.5 ? "B" : "")}{(l.GetProperty("font").GetProperty("italicRatio").ValueKind == JsonValueKind.Number && l.GetProperty("font").GetProperty("italicRatio").GetDouble() >= 0.5 ? "I" : "")}" : "-";
                    sb.AppendLine($"{a.Alias}|{box}|{font}|{a.Text}");
                }
            }
            File.WriteAllText(Path.Combine(args[3], $"{id}.txt"), sb.ToString());
        }
        Console.WriteLine("REVIEW_BUNDLES_WRITTEN");
        break;
    }
    case "draft":
    {
        // Reviewer-A draft Gold: every occurrence of every selected page gets a three-class label. Spec lines
        // ("E|R|O[?] alias | rationale") set non-default labels and review flags; all other rows are OTHER.
        // Output is DRAFT_REVIEWER_A_NOT_APPROVED; it is never Gold until the user approves each document.
        if (args.Length != 5) throw new ArgumentException("draft <review-dir> <spec-dir> <new-draft-dir> <new-review-packet.md>");
        Need(!Directory.Exists(args[3]) && !File.Exists(args[4]), "OUTPUT_EXISTS");
        Directory.CreateDirectory(args[3]);
        var packet = new StringBuilder();
        packet.AppendLine("# P7-F1Q held-out — Gold review packet (DRAFT, reviewer A, NOT APPROVED)");
        packet.AppendLine();
        packet.AppendLine("Every occurrence on every selected page has a drafted three-class label. Rows not listed below are drafted OTHER (body text, list items, table cells, page numbers). `?` marks rows that need your attention; `DECISION_NEEDED` marks a policy choice. Approve or correct per document; nothing here is Gold until approved.");
        var totals = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(args[1], "*.review.json").Order(StringComparer.Ordinal))
        {
            using var review = JsonDocument.Parse(File.ReadAllBytes(file));
            var r = review.RootElement; var id = r.GetProperty("id").GetString()!;
            var rows = r.GetProperty("occurrences").EnumerateArray().ToArray();
            var byAlias = rows.ToDictionary(o => o.GetProperty("sourceAlias").GetString()!, StringComparer.Ordinal);
            var spec = new Dictionary<string, (string Label, bool Flag, string Rationale)>(StringComparer.Ordinal);
            var notes = new List<string>();
            foreach (var raw in File.ReadAllLines(Path.Combine(args[2], id + ".spec")).Select(l => l.Trim().TrimStart('﻿')).Where(l => l.Length > 0))
            {
                if (raw.StartsWith("NOTE", StringComparison.Ordinal)) { notes.Add(raw[4..].Trim()); continue; }
                var m = Regex.Match(raw, @"^(E|R|O)(\?)?\s+(\S+)\s*\|\s*(.+)$");
                Need(m.Success, $"SPEC_LINE_INVALID:{id}:{raw}");
                var alias = m.Groups[3].Value;
                Need(byAlias.ContainsKey(alias), $"SPEC_ALIAS_NOT_ON_SELECTED_PAGES:{id}:{alias}");
                Need(spec.TryAdd(alias, (m.Groups[1].Value switch { "E" => "ESTABLISHES_STRUCTURE", "R" => "REPRESENTS_STRUCTURE", _ => "OTHER" },
                    m.Groups[2].Success, m.Groups[4].Value.Trim())), $"SPEC_ALIAS_DUPLICATE:{id}:{alias}");
            }
            var labels = rows.Select(o =>
            {
                var alias = o.GetProperty("sourceAlias").GetString()!;
                var has = spec.TryGetValue(alias, out var s);
                var label = has ? s.Label : "OTHER";
                totals[label] = totals.GetValueOrDefault(label) + 1;
                return new
                {
                    sourceAlias = alias, page = o.GetProperty("page").GetInt32(), pageStratum = o.GetProperty("pageStratum").GetString(),
                    text = o.GetProperty("text").GetString(), draftLabel = label,
                    reviewFlag = !has ? "NONE" : s.Rationale.Contains("USER_DECISION") ? "USER_DECIDED" : s.Rationale.Contains("DECISION_NEEDED") ? "DECISION_NEEDED" : s.Flag ? "REVIEW_FOCUS" : "NONE",
                    rationale = has ? s.Rationale : "Default: body text, list item, table cell, page number or other non-heading content on this page",
                    approval = has && s.Rationale.Contains("USER_DECISION") ? "USER_DECIDED_2026_10_10" : "PENDING_USER",
                };
            }).ToArray();
            File.WriteAllBytes(Path.Combine(args[3], $"{id}.gold-draft.json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = "P7_F1Q_HELDOUT_GOLD_DRAFT_V2", status = "DRAFT_REVIEWER_A_NOT_APPROVED", id,
                sourceKey = r.GetProperty("sourceKey").GetString(), sourceSha256 = r.GetProperty("sourceSha256").GetString(),
                sourceAliasUniverseSha256 = r.GetProperty("sourceAliasUniverseSha256").GetString(),
                reviewBundleSha256 = Hex(SHA256.HashData(File.ReadAllBytes(file))),
                reviewerA = "Claude (orchestrator), from parser text and layout only; PDF pages not rendered; independent of any provider prediction",
                visualEvidence = "NOT_RENDERED_IN_DRAFT_REVIEWER_TO_CONFIRM_AGAINST_PDF", notes, labels,
                counts = labels.GroupBy(l => l.draftLabel).ToDictionary(g => g.Key, g => g.Count()),
                flagged = labels.Count(l => l.reviewFlag != "NONE"),
            }, json));
            packet.AppendLine();
            packet.AppendLine($"## {id} — `{r.GetProperty("sourceKey").GetString()}`");
            packet.AppendLine();
            var c = labels.GroupBy(l => l.draftLabel).ToDictionary(g => g.Key, g => g.Count());
            packet.AppendLine($"Occurrences {labels.Length}: ESTABLISHES {c.GetValueOrDefault("ESTABLISHES_STRUCTURE")}, REPRESENTS {c.GetValueOrDefault("REPRESENTS_STRUCTURE")}, OTHER {c.GetValueOrDefault("OTHER")}; flagged {labels.Count(l => l.reviewFlag != "NONE")}. Pages: {string.Join(", ", labels.Select(l => $"{l.page} ({l.pageStratum})").Distinct())}.");
            foreach (var n in notes) packet.AppendLine($"- Note: {n}");
            packet.AppendLine();
            packet.AppendLine("| p. | alias | text | draft | flag | rationale |");
            packet.AppendLine("|---:|---|---|---|---|---|");
            foreach (var l in labels.Where(l => l.draftLabel != "OTHER" || l.reviewFlag != "NONE"))
                packet.AppendLine($"| {l.page} | {l.sourceAlias} | {Cell(l.text)} | {l.draftLabel.Replace("_STRUCTURE", "")} | {(l.reviewFlag == "NONE" ? "" : l.reviewFlag)} | {Cell(l.rationale)} |");
        }
        packet.Insert(0, "");
        File.WriteAllText(args[4], packet.ToString());
        Console.WriteLine(JsonSerializer.Serialize(new { totals }));
        break;
    }
    case "redact-publish":
    {
        // Issue #6 publication rule: no unredacted PII in the public repo. Every file under <public-dir> is copied
        // byte-exact to <new-private-dir>; files containing emails or context-marked phone numbers are replaced in the
        // public dir by a redacted copy plus a receipt binding the original sha256. Originals are never altered.
        if (args.Length != 3) throw new ArgumentException("redact-publish <public-dir> <new-private-dir>");
        var pub = Path.GetFullPath(args[1]); var priv = Path.GetFullPath(args[2]);
        Need(Directory.Exists(pub) && !Directory.Exists(priv), "REDACT_DIRS_INVALID");
        var email = new Regex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}");
        var phone = new Regex(@"(?i)(?:tel|phone|fax|mobile|điện thoại|đt|di động)\s*[:.]?\s*(\+?\d[\d .\-()]{7,}\d)");
        var redacted = new List<object>();
        foreach (var file in Directory.GetFiles(pub, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(pub, file); var target = Path.Combine(priv, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var bytes = File.ReadAllBytes(file); File.WriteAllBytes(target, bytes);
            var text = Encoding.UTF8.GetString(bytes);
            int emails = email.Matches(text).Count, phones = phone.Matches(text).Count;
            if (emails + phones == 0) continue;
            var clean = phone.Replace(email.Replace(text, "[REDACTED_EMAIL]"), m => m.Value.Replace(m.Groups[1].Value, "[REDACTED_PHONE]"));
            var cleanBytes = Encoding.UTF8.GetBytes(clean);
            var ext = Path.GetExtension(file);
            File.WriteAllBytes(Path.ChangeExtension(file, ".redacted" + ext), cleanBytes);
            File.WriteAllBytes(Path.ChangeExtension(file, ".redaction" + ".json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = "P7_F1Q_PUBLIC_REDACTION_V1", original = rel.Replace('\\', '/'), originalSha256 = Hex(SHA256.HashData(bytes)),
                redactedSha256 = Hex(SHA256.HashData(cleanBytes)), emails, phones, originalLocation = "PRIVATE_LOCAL_STORE_NOT_PUBLISHED",
            }, json));
            File.Delete(file);
            redacted.Add(new { file = rel, emails, phones });
        }
        Console.WriteLine(JsonSerializer.Serialize(new { redacted }));
        break;
    }
    case "atoms":
        foreach (var s in sources.Where(x => args.Skip(1).Contains(IdOf(x.Key))))
        {
            var parsed = PdfSourceAdapter.BuildWithDetails(s.Key);
            Console.WriteLine($"{IdOf(s.Key)} atoms={parsed.Snapshot.Atoms.Count} pages={parsed.Snapshot.Atoms.Select(a => a.Page).Distinct().Count()} lines={parsed.Details.ParserLineCount} {s.Key}");
        }
        break;
    default: throw new ArgumentException("UNKNOWN_MODE");
}

static (int Page, string Rule)? StructuredPage(DocxHeaderExtractor.DocumentProcessing.Source.Common.DocumentSourceSnapshot src, PdfSourceEvidenceStore store, HashSet<int> taken)
{
    var leader = new Regex(@"(\.\s*){4,}\s*\d*\s*$|…{2,}|\S\s{2,}\d{1,4}$|^.{3,}\D\s\d{1,4}$");
    var contentsWord = new Regex(@"^\s*(table of )?contents\s*$|^\s*m[ụu]c l[ụu]c\s*$", RegexOptions.IgnoreCase);
    var byPage = src.Atoms.GroupBy(a => a.Page).OrderBy(g => g.Key).ToArray();
    foreach (var g in byPage.Where(g => !taken.Contains(g.Key)))
    {
        var lines = g.OrderBy(a => a.Ordinal).Select(a => a.Text).ToArray();
        var leaders = lines.Count(l => leader.IsMatch(l));
        if (leaders >= 5 || (lines.Any(l => contentsWord.IsMatch(l)) && leaders >= 3)) return (g.Key, "CONTENTS");
    }
    var bottoms = store.Entries.Select(e => (Page: e.Fields.First(f => f.Name == "page").Value is { } p ? p.GetInt32() : 0,
        Bottom: e.Fields.First(f => f.Name == "bbox").Value is { } b ? Math.Round(b.GetProperty("bottom").GetDouble() / 2) : double.NaN))
        .Where(x => !double.IsNaN(x.Bottom)).ToArray();
    foreach (var g in bottoms.GroupBy(x => x.Page).OrderBy(g => g.Key).Where(g => !taken.Contains(g.Key)))
        if (g.GroupBy(x => x.Bottom).Count(row => row.Count() >= 3) >= 3) return (g.Key, "TABLE");
    return null;
}

static string Cell(string? s) => (s ?? "").Replace("|", "\\|").Replace("\n", " ").Length > 110 ? (s ?? "").Replace("|", "\\|").Replace("\n", " ")[..110] + "…" : (s ?? "").Replace("|", "\\|").Replace("\n", " ");
static string Verdict(object row) => JsonSerializer.SerializeToElement(row).GetProperty("verdict").GetString()!;

static string Classify(string path)
{
    if (Regex.IsMatch(path, @"p7\.d3\.|p7\.f1q\.|v5-p6t-function-membership|p7\.d2\.3\.(pilot-f1|approved-pilot|gold)")) return "F1_FAMILY_EXPOSURE";
    if (Regex.IsMatch(path, @"accuracy-round\d+/.*(semantic-run|role-span)|llm-semantic|/raw/|raw-capture|response\.(txt|sse)|provider-raw")) return "MODEL_PREDICTION_EXPOSURE";
    if (Regex.IsMatch(path, @"gold|manual-labels|/keys/|reference-coverage|reference-occurrence-bridge|a99-closed-loop/review/|annotation|adjudication|human")) return "HUMAN_REFERENCE_PRIOR";
    if (Regex.IsMatch(path, @"p7\.d2\.3\.(shape|source-candidates)")) return "P7_SOURCE_REVIEW";
    return "ENGINE_OR_INVENTORY";
}
static string ClassifyCodex(string path) => Regex.IsMatch(path, @"p7-(source-review|shape-review|shape-audit|portable-review)") ? "P7_SOURCE_REVIEW" : Classify(path.Replace('\\', '/'));

static IEnumerable<string> GitGrep(string sha, string stem)
{
    var psi = new ProcessStartInfo("git", $"grep -l -F -e {sha} -e {stem} HEAD -- .") { RedirectStandardOutput = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8 };
    using var p = Process.Start(psi)!; var output = p.StandardOutput.ReadToEnd(); p.WaitForExit();
    return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim().Replace("HEAD:", ""));
}

static string Text(string pdf) => string.Join(" ", PdfSourceAdapter.BuildWithDetails(pdf).Snapshot.Atoms.OrderBy(a => a.Ordinal).Select(a => a.Text));
static HashSet<string> Shingles(string text)
{
    var words = Regex.Split(text.ToLowerInvariant(), @"\W+").Where(w => w.Length > 0).ToArray();
    return Enumerable.Range(0, Math.Max(0, words.Length - 4)).Select(i => string.Join(' ', words, i, 5)).ToHashSet();
}
static double Jaccard(HashSet<string> a, HashSet<string> b) => a.Count + b.Count == 0 ? 0 : (double)a.Intersect(b).Count() / a.Union(b).Count();
static string Hex(byte[] b) => Convert.ToHexStringLower(b);
static void Need(bool ok, string code) { if (!ok) throw new InvalidOperationException(code); }

internal static partial class Program { internal const double NearDuplicateJaccard = 0.5; }
