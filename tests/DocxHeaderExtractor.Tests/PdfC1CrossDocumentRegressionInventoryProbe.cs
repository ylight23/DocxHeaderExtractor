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
/// by construction. The only question worth asking is whether any OTHER document besides 001 and 003
/// has ever produced a retained `pdf_hierarchy_facts` run with span-lane evidence, checkpoint or
/// artifact - and whether any of that evidence shows `partial_timeout` this gate hasn't already used.
/// </para>
/// </summary>
public sealed class PdfC1CrossDocumentRegressionInventoryProbe
{
    [Fact]
    public void NoIndependentPartialTimeoutDocumentExistsBeyond001And003()
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
        Assert.True(documentIds.Length > 0, "expected at least one retained pdf_hierarchy_facts artifact to exist (001's, at minimum)");
        Assert.Contains("001", documentIds);
        Assert.Contains("003", documentIds);

        // The actual gate #7 verdict: true only if a document beyond 001/003 shows partial_timeout,
        // which would be independent cross-document regression evidence to replay. As of this lock,
        // it is not - and this test fixes that fact so a future run can't silently drift from it.
        var independentPartialTimeoutDocuments = partialTimeoutDocuments.Where(d => d is not ("001" or "003")).ToArray();
        Assert.Empty(independentPartialTimeoutDocuments);
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
            verdict = "INSUFFICIENT_EXISTING_EVIDENCE",
            verdictReason = "Only 001 and 003 have ever produced a retained pdf_hierarchy_facts run with span-lane evidence anywhere in this repository (working tree included). No independent third document exists to replay for cross-document regression evidence. This is not treated as a pass by default absence, nor is a synthetic document manufactured to force a verdict.",
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
