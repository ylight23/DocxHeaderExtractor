using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Gate #7 of partial-result preservation's acceptance criteria: no new cross-document regression.
/// This inventories every retained artifact anywhere in the repository (working tree, not just
/// committed evidence - `.verify-build` included, per the project's own "never the sole authority"
/// caveat applied honestly here in the other direction: it is still real evidence to inventory) that
/// could carry span-lane evidence, and reports what actually exists rather than assuming absence means
/// pass or manufacturing a document to force a verdict.
/// <para>
/// `spanLaneStatus` was added this session (C1.4a); no artifact from before that commit can carry it
/// by construction. At the time this gate first ran, only a small retained corpus had produced
/// span-lane evidence, so it locked <c>INSUFFICIENT_EXISTING_EVIDENCE</c> rather than forcing a verdict.
/// N3.4's canonical live trace on `004` later supplied the independent evidence this gate was waiting
/// for - and N3.5 already used it to reach a BLOCK decision (12 new `UNMATCHED_OUTPUT` collateral).
/// Later retained replay artifacts for 030, 043, and 058 are also included in the explicit inventory
/// lock below. This lock records that outcome instead of the earlier
/// "insufficient evidence" state - a correction, not a silent drift, since the underlying evidence and
/// its N3.5 decision are both already committed and this test would otherwise silently disagree with
/// them.
/// </para>
/// </summary>
public sealed class PdfC1CrossDocumentRegressionInventoryProbe
{
    [Fact]
    public void IndependentPartialTimeoutEvidenceNowExistsAndWasConsumedByN35()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var searchRoots = new[] { "eval", "keys", ".verify-build" }
            .Select(d => Path.Combine(root, d))
            .Where(Directory.Exists);

        var runArtifacts = new List<(string Path, string? DocumentId, string? SpanLaneStatus)>();
        foreach (var searchRoot in searchRoots)
        {
            foreach (var path in Directory.EnumerateFiles(searchRoot, "*.json", SearchOption.AllDirectories))
            {
                string text;
                try { text = File.ReadAllText(path); } catch { continue; }
                if (!text.Contains("pdf_hierarchy_facts", StringComparison.Ordinal)) continue;

                using var doc = JsonDocument.Parse(text);
                if (!doc.RootElement.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array) continue;
                foreach (var row in rows.EnumerateArray())
                {
                    var file = row.TryGetProperty("file", out var f) ? f.GetString() : null;
                    var status = row.TryGetProperty("spanLaneStatus", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                    runArtifacts.Add((Path.GetRelativePath(root, path), file, status));
                }
            }
        }

        var documentIds = runArtifacts
            .Select(r => r.DocumentId ?? "(unknown)")
            .Select(id => id.Split('_')[0]) // stem prefix, e.g. "001_Bo_luat..." -> "001"
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        var partialTimeoutDocuments = runArtifacts
            .Where(r => r.SpanLaneStatus == "partial_timeout")
            .Select(r => (r.DocumentId ?? "(unknown)").Split('_')[0])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        // The inventory's own evidence, not an assertion about what SHOULD exist - this is what makes
        // the gate verdict falsifiable rather than assumed.
        Assert.True(documentIds.Length > 0, "expected at least one retained pdf_hierarchy_facts artifact to exist");
        Assert.Contains("003", documentIds);

        // Corrected expectation: these are the independent retained documents this gate currently
        // consumes alongside 003. If another document appears, this lock must be revisited deliberately.
        var independentPartialTimeoutDocuments = partialTimeoutDocuments.Where(d => d is not "003").ToArray();
        Assert.Equal(["004", "030", "043", "058"], independentPartialTimeoutDocuments);

        // N3.5 already decided BLOCK using this exact evidence (12/83 emitted outputs with zero silver
        // support, all new relative to baseline's 0) - the decision artifact is the authority, not a
        // re-derivation here.
        var decisionPath = Path.Combine(root, "eval", "benchmark-n3", "n3.4", "reports", "004-n3.4-decision.v1.json");
        Assert.True(File.Exists(decisionPath), "N3.5's decision artifact for 004 should exist once this gate's evidence is consumed");
    }

    [Fact]
    public void WriteInventory()
    {
        var output = Environment.GetEnvironmentVariable("C1_GATE7_INVENTORY_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;

        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var searchRoots = new[] { "eval", "keys", ".verify-build" }
            .Select(d => Path.Combine(root, d))
            .Where(Directory.Exists);

        var jsonlCheckpoints = new List<object>();
        foreach (var searchRoot in searchRoots)
            foreach (var path in Directory.EnumerateFiles(searchRoot, "*.jsonl", SearchOption.AllDirectories))
            {
                var lanes = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var line in File.ReadLines(path))
                {
                    string? lane;
                    try { using var d = JsonDocument.Parse(line); lane = d.RootElement.TryGetProperty("lane", out var l) ? l.GetString() : null; }
                    catch { continue; }
                    if (lane is null) continue;
                    lanes[lane] = lanes.GetValueOrDefault(lane) + 1;
                }
                if (lanes.Count > 0)
                    jsonlCheckpoints.Add(new { path = Path.GetRelativePath(root, path), lanes });
            }

        var runArtifacts = new List<object>();
        foreach (var searchRoot in searchRoots)
            foreach (var path in Directory.EnumerateFiles(searchRoot, "*.json", SearchOption.AllDirectories))
            {
                string text;
                try { text = File.ReadAllText(path); } catch { continue; }
                if (!text.Contains("pdf_hierarchy_facts", StringComparison.Ordinal)) continue;
                using var doc = JsonDocument.Parse(text);
                if (!doc.RootElement.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array) continue;
                foreach (var row in rows.EnumerateArray())
                    runArtifacts.Add(new
                    {
                        path = Path.GetRelativePath(root, path),
                        file = row.TryGetProperty("file", out var f) ? f.GetString() : null,
                        semanticLaneStatus = row.TryGetProperty("semanticLaneStatus", out var sem) ? sem.GetString() : null,
                        spanLaneStatus = row.TryGetProperty("spanLaneStatus", out var span) ? span.GetString() : null,
                        validatedHeadings = row.TryGetProperty("counters", out var c) && c.TryGetProperty("validatedHeadings", out var v) ? v.GetInt32() : (int?)null,
                    });
            }

        var report = new
        {
            schemaVersion = 1,
            artifactKind = "c1_gate7_cross_document_regression_inventory",
            usesModel = false,
            purpose = "Gate #7 (no new cross-document regression) for partial-result preservation. Inventories every retained artifact that could carry span-lane evidence, rather than assuming absence means pass.",
            spanLaneStatusIntroducedAt = "commit 5036530 (this session) - no artifact before it can carry the field by construction",
            jsonlCheckpointsWithASpanLane = jsonlCheckpoints.Cast<dynamic>().Count(c => ((IDictionary<string, int>)c.lanes).ContainsKey("span")),
            allJsonlCheckpointsFound = jsonlCheckpoints,
            pdfHierarchyFactsRunArtifactsFound = runArtifacts,
            verdict = "REGRESSION_FOUND",
            verdictReason = "004 (N3.4's canonical live trace) supplied the independent third partial_timeout document this gate was waiting for. R1 recovered 47/55 exact-span decisionRelevant occurrences there, but the same trace also exposed 12/83 emitted outputs with zero support from any reviewed silver occurrence - all new relative to baseline's 0. N3.5 already decided BLOCK on this evidence (eval/benchmark-n3/n3.4/reports/004-n3.4-decision.v1.json); this gate's earlier INSUFFICIENT_EXISTING_EVIDENCE verdict is superseded, not silently left standing.",
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
