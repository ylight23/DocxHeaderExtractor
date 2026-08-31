using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// R5-3D1 replay harness. Cross-revision joins use page plus the ordered exact PDF line identities.
/// Candidate ids are retained only as run-local diagnostics and are never serialized as replay
/// identity. This stays in the test/evaluation boundary so adding the harness cannot change the
/// production authority path.
/// </summary>
public sealed class PdfR5SourceIdentityReplayHarnessTests
{
    [Fact]
    public void ExportPinnedBaselineFixturesWhenExplicitlyEnabled()
    {
        var environment = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Where(item => item.Key is string && item.Value is string)
            .ToDictionary(item => (string)item.Key, item => (string)item.Value!, StringComparer.Ordinal);
        if (!environment.TryGetValue("R5_3D1_BASELINE_FIXTURE_ROOT", out var fixtureRoot) ||
            string.IsNullOrWhiteSpace(fixtureRoot))
            return;

        var audit = PdfR5ReplayHarness.ReadAudit(Required(environment, "R5_3D1_BASELINE_AUDIT"));
        var output = PdfR5ReplayHarness.ReadJson(Required(environment, "R5_3D1_BASELINE_OUTPUT"));
        var checkpoint = PdfR5ReplayHarness.ReadCheckpoint(Required(environment, "R5_3D1_BASELINE_CHECKPOINT"));
        var build = PdfR5ReplayHarness.Build(
            audit, Required(environment, "R5_3D1_BASELINE_REVISION"),
            Required(environment, "R5_3D1_DOCUMENT_ID"), Required(environment, "R5_3D1_DOCX_SHA256"),
            Required(environment, "R5_3D1_PDF_SHA256"), output,
            Required(environment, "R5_3D1_PDF_LINE_EXTRACTION_FINGERPRINT"),
            Required(environment, "R5_3D1_MODEL_CONTRACT_HASH"));
        var errors = build.Errors.Concat(checkpoint.Errors).ToList();
        var fixture = build.Fixture;
        if (fixture is not null)
        {
            fixture = fixture with
            {
                SemanticProposals = checkpoint.SemanticProposals.Count > 0
                    ? checkpoint.SemanticProposals : fixture.SemanticProposals,
                SpanProposals = checkpoint.SpanProposals,
            };
            errors.AddRange(PdfR5ReplayHarness.ValidateCapture(fixture, audit));
        }
        Assert.Empty(errors);
        Directory.CreateDirectory(fixtureRoot);
        File.WriteAllText(Path.Combine(fixtureRoot, Required(environment, "R5_3D1_DOCUMENT_ID") + ".producer-replay.v1.json"),
            JsonSerializer.Serialize(fixture, JsonOptions));
    }

    [Fact]
    public void ExportConfiguredReplayEvidenceWithoutInventingMissingBaseline()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry item in Environment.GetEnvironmentVariables())
        {
            if (item.Key is string key && item.Value is string value)
                environment[key] = value;
        }
        if (!PdfR5ReplayHarness.IsEvidenceConfigured(environment)) return;

        var reportPath = Required(environment, "R5_3D1_REPORT");
        var baseline = PdfR5ReplayHarness.ReadAudit(Required(environment, "R5_3D1_BASELINE_AUDIT"));
        var current = PdfR5ReplayHarness.ReadAudit(Required(environment, "R5_3D1_CURRENT_AUDIT"));
        var baselineBuild = PdfR5ReplayHarness.Build(
            baseline, Required(environment, "R5_3D1_BASELINE_REVISION"), Required(environment, "R5_3D1_DOCUMENT_ID"),
            Required(environment, "R5_3D1_DOCX_SHA256"), Required(environment, "R5_3D1_PDF_SHA256"),
            PdfR5ReplayHarness.ReadJson(Required(environment, "R5_3D1_BASELINE_OUTPUT")),
            Required(environment, "R5_3D1_PDF_LINE_EXTRACTION_FINGERPRINT"),
            Required(environment, "R5_3D1_MODEL_CONTRACT_HASH"));
        var currentBuild = PdfR5ReplayHarness.Build(
            current, Required(environment, "R5_3D1_CURRENT_REVISION"), Required(environment, "R5_3D1_DOCUMENT_ID"),
            Required(environment, "R5_3D1_DOCX_SHA256"), Required(environment, "R5_3D1_PDF_SHA256"),
            PdfR5ReplayHarness.ReadJson(Required(environment, "R5_3D1_CURRENT_OUTPUT")),
            Required(environment, "R5_3D1_PDF_LINE_EXTRACTION_FINGERPRINT"),
            Required(environment, "R5_3D1_MODEL_CONTRACT_HASH"));
        var baselineCheckpoint = PdfR5ReplayHarness.ReadCheckpoint(Required(environment, "R5_3D1_BASELINE_CHECKPOINT"));
        var currentCheckpoint = PdfR5ReplayHarness.ReadCheckpoint(Required(environment, "R5_3D1_CURRENT_CHECKPOINT"));
        var corpusErrors = PdfR5ReplayHarness.ValidateCorpus(
            Required(environment, "R5_3D1_CORPUS_MANIFEST"), Required(environment, "R5_3D1_CORPUS_ROOT"));
        var errors = baselineBuild.Errors.Concat(currentBuild.Errors)
            .Concat(baselineCheckpoint.Errors).Concat(currentCheckpoint.Errors).ToList();
        errors.AddRange(corpusErrors);
        errors.AddRange(PdfR5ReplayHarness.ValidateDocumentBinding(
            Required(environment, "R5_3D1_CORPUS_MANIFEST"), Required(environment, "R5_3D1_CORPUS_ROOT"),
            Required(environment, "R5_3D1_DOCUMENT_ID"), Required(environment, "R5_3D1_DOCX_SHA256"),
            Required(environment, "R5_3D1_PDF_SHA256")));
        var baselineFixture = baselineBuild.Fixture;
        var currentFixture = currentBuild.Fixture;
        if (baselineFixture is not null)
        {
            var auditCheckpointJoin = PdfR5ReplayHarness.JoinSelections(
                baselineFixture.SelectedSources, baselineCheckpoint.SelectedSources);
            if (auditCheckpointJoin.UnjoinedBaseline > 0 || auditCheckpointJoin.UnjoinedCurrent > 0 ||
                auditCheckpointJoin.SourceTextMismatches > 0)
                errors.Add("BASELINE_AUDIT_CHECKPOINT_SELECTION_MISMATCH");
        }
        if (currentFixture is not null)
        {
            var auditCheckpointJoin = PdfR5ReplayHarness.JoinSelections(
                currentFixture.SelectedSources, currentCheckpoint.SelectedSources);
            if (auditCheckpointJoin.UnjoinedBaseline > 0 || auditCheckpointJoin.UnjoinedCurrent > 0 ||
                auditCheckpointJoin.SourceTextMismatches > 0)
                errors.Add("CURRENT_AUDIT_CHECKPOINT_SELECTION_MISMATCH");
        }
        if (baselineFixture is not null) baselineFixture = baselineFixture with
        {
            SemanticProposals = baselineCheckpoint.SemanticProposals.Count > 0
                ? baselineCheckpoint.SemanticProposals : baselineFixture.SemanticProposals,
            SpanProposals = baselineCheckpoint.SpanProposals,
        };
        if (currentFixture is not null) currentFixture = currentFixture with
        {
            SemanticProposals = currentCheckpoint.SemanticProposals.Count > 0
                ? currentCheckpoint.SemanticProposals : currentFixture.SemanticProposals,
            SpanProposals = currentCheckpoint.SpanProposals,
        };
        errors.AddRange(PdfR5ReplayHarness.ValidateCapture(baselineFixture, baseline));
        errors.AddRange(PdfR5ReplayHarness.ValidateCapture(currentFixture, current));

        var selection = baselineFixture is null || currentFixture is null
            ? new SelectionJoin(0, 0, 0, 0)
            : PdfR5ReplayHarness.JoinSelections(baselineFixture.SelectedSources, currentFixture.SelectedSources);
        if (selection.SourceTextMismatches > 0)
            errors.Add("CROSS_REVISION_SOURCE_TEXT_DRIFT");
        var semantic = PdfR5ReplayHarness.JoinStage(
            baselineFixture?.SemanticProposals.Select(item => item.Source) ?? [],
            currentFixture?.SemanticProposals.Select(item => item.Source) ?? []);
        var span = PdfR5ReplayHarness.JoinStage(
            baselineFixture?.SpanProposals.Select(item => item.Source) ?? [],
            currentFixture?.SpanProposals.Select(item => item.Source) ?? []);
        var hierarchy = PdfR5ReplayHarness.JoinStage(
            baselineFixture?.HierarchyProposals.Select(item => item.Source) ?? [],
            currentFixture?.HierarchyProposals.Select(item => item.Source) ?? []);

        var output = new
        {
            schemaVersion = 1,
            artifactKind = "r5_source_identity_replay",
            baselineRevision = environment["R5_3D1_BASELINE_REVISION"],
            currentRevision = environment["R5_3D1_CURRENT_REVISION"],
            corpusHashMismatch = corpusErrors.Count > 0,
            selectionSourceUnjoined = selection.UnjoinedBaseline + selection.UnjoinedCurrent,
            semanticReplayUnjoined = semantic.UnjoinedBaseline + semantic.UnjoinedCurrent,
            spanReplayUnjoined = span.UnjoinedBaseline + span.UnjoinedCurrent,
            hierarchyReplayUnjoined = hierarchy.UnjoinedBaseline + hierarchy.UnjoinedCurrent,
            sourceTextMismatches = selection.SourceTextMismatches,
            pdfProducerJoined = selection.Joined > 0 && errors.Count == 0 ? 1 : 0,
            pdfProducerUnjoined = selection.Joined > 0 && errors.Count == 0 ? 0 : 1,
            finalStructureDelta = "UNMEASURED",
            finalHeadingJsonDelta = "UNMEASURED",
            productOutputJsonDelta = "UNMEASURED",
            providerCallsDuringReplay = 0,
            overSelectionAllowed = false,
            productionCodeDelta = 0,
            errors = errors.ToArray(),
            baselineFixture,
            currentFixture,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(output, JsonOptions));
        Assert.Empty(errors);
    }

    private static string Required(IReadOnlyDictionary<string, string> environment, string key) =>
        environment.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException("R5_3D1_MISSING_ENV:" + key);

    [Fact]
    public void SourceIdentityKeyIgnoresRunLocalCandidateIdButPreservesLineOrder()
    {
        var first = SourceEntry("candidate-a", 3, ["line-1", "line-2"], "Heading");
        var second = SourceEntry("candidate-z", 3, ["line-1", "line-2"], "Heading");
        var reversed = SourceEntry("candidate-z", 3, ["line-2", "line-1"], "Heading");

        Assert.Equal(first.Key, second.Key);
        Assert.NotEqual(first.Key, reversed.Key);
    }

    [Fact]
    public void SelectionJoinFailsClosedOnSourceTextDrift()
    {
        var baseline = new[] { SourceEntry("old-id", 4, ["line"], "Old source text") };
        var current = new[] { SourceEntry("new-id", 4, ["line"], "New source text") };

        var join = PdfR5ReplayHarness.JoinSelections(baseline, current);

        Assert.Equal(1, join.Joined);
        Assert.Equal(1, join.SourceTextMismatches);
        Assert.Equal(0, join.UnjoinedBaseline);
        Assert.Equal(0, join.UnjoinedCurrent);
    }

    [Fact]
    public void AuditProposalsAreMaterializedWithSourceKeysOnly()
    {
        var audit = new RouteExecutionAudit(
            "test", 1, 1, 1, 1,
            [new RouteBlockAudit("candidate-1", 2, "1 Scope")],
            [new RouteBlockAudit("candidate-1", 2, "1 Scope")], [],
            [new RouteBlockDecisionAudit("candidate-1", "HeadingTopic", .91, "role")],
            ["candidate-1"], [], ["candidate-1"])
        {
            SelectedSourceIdentities = [new PdfSelectedSourceIdentity(
                "candidate-1", 2, ["source-line-1"], "1 Scope", new TextOffsetSpan(0, 7))],
            ValidatedStructures = [new PdfValidatedStructure(
                "candidate-1", 1, null, "unresolved", "requires_review")],
            HierarchyProposals = [new PdfHierarchyProposalAudit(
                "candidate-1", null, null, "unresolved")],
        };

        var result = PdfR5ReplayHarness.Build(audit, "baseline", "doc", "docx-sha", "pdf-sha");

        Assert.Empty(result.Errors);
        var fixture = Assert.IsType<PdfR5ReplayFixture>(result.Fixture);
        Assert.Equal("2|source-line-1", fixture.SelectedSources.Single().Key.Value);
        Assert.Equal("2|source-line-1", fixture.SemanticProposals.Single().Source.Value);
        Assert.Equal("2|source-line-1", fixture.HierarchyProposals.Single().Source.Value);
        Assert.DoesNotContain("candidate-1", JsonSerializer.Serialize(fixture, JsonOptions));
    }

    [Fact]
    public void CheckpointSpanProposalJoinsBySelectedSourceIdentity()
    {
        var path = Path.Combine(Path.GetTempPath(), $"r5-3d1-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllText(path, """
                {"lane":"selection","payload":{"selected":[{"candidateIdDiagnostic":"candidate-1","page":2,"sourceLineIds":["source-line-1"],"sourceText":"1 Scope"}]}}
                {"lane":"span","payload":{"blocks":[{"id":"candidate-1","page":2,"lineIds":["source-line-1"],"resolved":true,"start":0,"end":7}]}}
                """);

            var result = PdfR5ReplayHarness.ReadCheckpoint(path);

            Assert.Empty(result.Errors);
            var proposal = Assert.Single(result.SpanProposals);
            Assert.Equal("2|source-line-1", proposal.Source.Value);
            Assert.Equal(new TextOffsetSpan(0, 7), proposal.Span);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ReplayProbeIsNoOpWithoutExplicitEvidencePaths()
    {
        // The environment-driven probe below must never silently run a live provider or invent a
        // baseline when the pinned audit/checkpoint evidence has not been supplied.
        Assert.False(PdfR5ReplayHarness.IsEvidenceConfigured(
            new Dictionary<string, string>(StringComparer.Ordinal)));
    }

    private static PdfReplaySourceEntry SourceEntry(string candidateId, int page, IReadOnlyList<string> lines, string text) =>
        new(new PdfReplaySourceKey(page, lines), text,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant(),
            new TextOffsetSpan(0, text.Length), candidateId);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
}

internal static class PdfR5ReplayHarness
{
    private const string BaselineAudit = "R5_3D1_BASELINE_AUDIT";
    private const string CurrentAudit = "R5_3D1_CURRENT_AUDIT";
    private const string BaselineCheckpoint = "R5_3D1_BASELINE_CHECKPOINT";
    private const string CurrentCheckpoint = "R5_3D1_CURRENT_CHECKPOINT";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static bool IsEvidenceConfigured(IReadOnlyDictionary<string, string> environment) =>
        environment.ContainsKey(BaselineAudit) && environment.ContainsKey(CurrentAudit) &&
        environment.ContainsKey(BaselineCheckpoint) && environment.ContainsKey(CurrentCheckpoint) &&
        environment.ContainsKey("R5_3D1_BASELINE_OUTPUT") && environment.ContainsKey("R5_3D1_CURRENT_OUTPUT") &&
        environment.ContainsKey("R5_3D1_REPORT") && environment.ContainsKey("R5_3D1_BASELINE_REVISION") &&
        environment.ContainsKey("R5_3D1_CURRENT_REVISION") && environment.ContainsKey("R5_3D1_DOCUMENT_ID") &&
        environment.ContainsKey("R5_3D1_DOCX_SHA256") && environment.ContainsKey("R5_3D1_PDF_SHA256") &&
        environment.ContainsKey("R5_3D1_CORPUS_MANIFEST") && environment.ContainsKey("R5_3D1_CORPUS_ROOT") &&
        environment.ContainsKey("R5_3D1_PDF_LINE_EXTRACTION_FINGERPRINT") &&
        environment.ContainsKey("R5_3D1_MODEL_CONTRACT_HASH");

    public static RouteExecutionAudit ReadAudit(string path)
    {
        if (!File.Exists(path)) throw new InvalidOperationException("AUDIT_MISSING:" + path);
        return JsonSerializer.Deserialize<RouteExecutionAudit>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException("AUDIT_PARSE_FAILED:" + path);
    }

    public static JsonElement ReadJson(string path)
    {
        if (!File.Exists(path)) throw new InvalidOperationException("OUTPUT_ORACLE_MISSING:" + path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    public static IReadOnlyList<string> ValidateCorpus(string manifestPath, string root)
    {
        var errors = new List<string>();
        if (!File.Exists(manifestPath)) return ["CORPUS_MANIFEST_MISSING:" + manifestPath];
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
                return ["CORPUS_ITEMS_MISSING"];

            foreach (var item in items.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idValue) ? idValue.GetString() ?? "unknown" : "unknown";
                foreach (var (pathProperty, hashProperty) in new[] { ("docx", "docxSha256"), ("pdf", "pdfSha256") })
                {
                    var relative = item.TryGetProperty(pathProperty, out var pathValue) ? pathValue.GetString() : null;
                    var expected = item.TryGetProperty(hashProperty, out var hashValue) ? hashValue.GetString() : null;
                    if (string.IsNullOrWhiteSpace(relative) || string.IsNullOrWhiteSpace(expected))
                    {
                        errors.Add($"CORPUS_METADATA_MISSING:{id}:{pathProperty}");
                        continue;
                    }
                    var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(path))
                    {
                        errors.Add($"CORPUS_FILE_MISSING:{id}:{pathProperty}");
                        continue;
                    }
                    var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
                    if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                        errors.Add($"CORPUS_HASH_MISMATCH:{id}:{pathProperty}");
                }
            }
        }
        catch (JsonException ex)
        {
            errors.Add("CORPUS_MANIFEST_INVALID:" + ex.Message);
        }
        return errors;
    }

    public static IReadOnlyList<string> ValidateDocumentBinding(
        string manifestPath, string root, string documentId, string docxSha256, string pdfSha256)
    {
        if (!File.Exists(manifestPath)) return ["CORPUS_MANIFEST_MISSING:" + manifestPath];
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var item = document.RootElement.TryGetProperty("items", out var items)
                ? items.EnumerateArray().FirstOrDefault(candidate =>
                    candidate.TryGetProperty("id", out var id) && id.GetString() == documentId)
                : default;
            if (item.ValueKind == JsonValueKind.Undefined) return ["CORPUS_DOCUMENT_MISSING:" + documentId];
            var errors = new List<string>();
            if (!string.Equals(item.GetProperty("docxSha256").GetString(), docxSha256, StringComparison.OrdinalIgnoreCase))
                errors.Add("DOCX_SHA256_BINDING_MISMATCH:" + documentId);
            if (!string.Equals(item.GetProperty("pdfSha256").GetString(), pdfSha256, StringComparison.OrdinalIgnoreCase))
                errors.Add("PDF_SHA256_BINDING_MISMATCH:" + documentId);
            return errors;
        }
        catch (JsonException ex)
        {
            return ["CORPUS_MANIFEST_INVALID:" + ex.Message];
        }
    }

    public static IReadOnlyList<string> ValidateCapture(PdfR5ReplayFixture? fixture, RouteExecutionAudit audit)
    {
        if (fixture is null) return ["FIXTURE_MISSING"];
        var errors = new List<string>();
        if (fixture.SelectedSources.Count == 0) errors.Add("SELECTED_SOURCE_COUNT_ZERO");
        if (fixture.SemanticProposals.Count == 0) errors.Add("SEMANTIC_PROPOSAL_COUNT_ZERO");
        if (fixture.SpanProposals.Count == 0) errors.Add("SPAN_PROPOSAL_COUNT_ZERO");
        if (audit.ValidatedStructures.Count == 0) errors.Add("VALIDATED_STRUCTURE_COUNT_ZERO");
        if (audit.GroundedBlockIds.Count == 0) errors.Add("GROUNDED_COUNT_ZERO");
        if (fixture.Oracle.FinalHeadingRecords is not { } headings ||
            headings.ValueKind != JsonValueKind.Array || headings.GetArrayLength() == 0)
            errors.Add("FINAL_HEADING_COUNT_ZERO");
        return errors;
    }

    public static ReplayBuildResult Build(
        RouteExecutionAudit audit,
        string revision,
        string documentId,
        string docxSha256,
        string pdfSha256,
        JsonElement? expectedOutput = null,
        string? pdfLineExtractionFingerprint = null,
        string? modelContractHash = null)
    {
        var errors = new List<string>();
        var sources = new List<PdfReplaySourceEntry>();
        var byCandidate = new Dictionary<string, PdfReplaySourceEntry>(StringComparer.Ordinal);
        foreach (var selected in audit.SelectedSourceIdentities)
        {
            if (selected.Page < 1 || selected.SourceLineIds.Count == 0 ||
                selected.SourceLineIds.Any(string.IsNullOrWhiteSpace))
            {
                errors.Add($"INVALID_SELECTED_SOURCE:{selected.CandidateIdDiagnostic}");
                continue;
            }

            var entry = new PdfReplaySourceEntry(
                new PdfReplaySourceKey(selected.Page, selected.SourceLineIds),
                selected.SourceText,
                Sha256(selected.SourceText),
                selected.SourceSpan,
                selected.CandidateIdDiagnostic);
            if (string.IsNullOrEmpty(selected.SourceText))
                errors.Add($"EMPTY_SELECTED_SOURCE_TEXT:{selected.CandidateIdDiagnostic}");
            if (selected.SourceSpan is { } sourceSpan &&
                (sourceSpan.Start < 0 || sourceSpan.End < sourceSpan.Start || sourceSpan.End > selected.SourceText.Length))
                errors.Add($"INVALID_SELECTED_SOURCE_SPAN:{selected.CandidateIdDiagnostic}");
            if (!byCandidate.TryAdd(selected.CandidateIdDiagnostic, entry))
                errors.Add($"DUPLICATE_CANDIDATE_ID:{selected.CandidateIdDiagnostic}");
            else if (sources.Any(existing => existing.Key == entry.Key))
                errors.Add($"DUPLICATE_SOURCE_IDENTITY:{entry.Key.Value}");
            else
                sources.Add(entry);
        }

        var semantic = new List<PdfReplaySemanticProposal>();
        foreach (var decision in audit.BlockDecisions)
        {
            if (!byCandidate.TryGetValue(decision.Id, out var source))
            {
                errors.Add($"SEMANTIC_UNJOINED:{decision.Id}");
                continue;
            }
            semantic.Add(new PdfReplaySemanticProposal(
                source.Key,
                decision.Role,
                decision.Confidence,
                decision.Reason));
        }

        var hierarchy = new List<PdfReplayHierarchyProposal>();
        var proposalById = audit.HierarchyProposals
            .GroupBy(proposal => proposal.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var structure in audit.ValidatedStructures)
        {
            if (!byCandidate.TryGetValue(structure.SourceId, out var source))
            {
                errors.Add($"HIERARCHY_UNJOINED:{structure.SourceId}");
                continue;
            }

            proposalById.TryGetValue(structure.SourceId, out var proposal);
            PdfReplaySourceKey? proposedParent = ResolveParent(proposal?.ProposedParentId, byCandidate, errors, "PROPOSED_PARENT");
            PdfReplaySourceKey? resolvedParent = ResolveParent(structure.ParentId, byCandidate, errors, "RESOLVED_PARENT");
            hierarchy.Add(new PdfReplayHierarchyProposal(
                source.Key, structure.Level, proposedParent, resolvedParent,
                proposal?.Resolution ?? structure.ParentResolution));
        }

        var fixture = new PdfR5ReplayFixture(
            1,
            documentId,
            revision,
            docxSha256,
            pdfSha256,
            sources.OrderBy(source => source.Key.Value, StringComparer.Ordinal).ToArray(),
            semantic.OrderBy(proposal => proposal.Source.Value, StringComparer.Ordinal).ToArray(),
            [],
            hierarchy.OrderBy(proposal => proposal.Source.Value, StringComparer.Ordinal).ToArray(),
            PdfReplayOracle.From(expectedOutput))
        {
            PdfLineExtractionFingerprint = pdfLineExtractionFingerprint,
            ModelContractHash = modelContractHash,
        };
        return new ReplayBuildResult(fixture, errors);
    }

    public static CheckpointReplayResult ReadCheckpoint(string path)
    {
        var errors = new List<string>();
        var selectedByCandidate = new Dictionary<string, PdfReplaySourceEntry>(StringComparer.Ordinal);
        var selectedByKey = new Dictionary<PdfReplaySourceKey, PdfReplaySourceEntry>();
        var semanticProposals = new List<PdfReplaySemanticProposal>();
        var spanProposals = new List<PdfReplaySpanProposal>();
        if (!File.Exists(path)) return new CheckpointReplayResult([], [], [], ["CHECKPOINT_MISSING:" + path]);

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                var lane = String(root, "lane");
                var payload = root.TryGetProperty("payload", out var value) ? value : default;
                if (lane == "selection")
                {
                    foreach (var item in Array(payload, "selected"))
                    {
                        var candidate = String(item, "candidateIdDiagnostic");
                        var source = new PdfReplaySourceEntry(
                            new PdfReplaySourceKey(Int(item, "page"), Strings(item, "sourceLineIds")),
                            String(item, "sourceText"),
                            Sha256(String(item, "sourceText")),
                            Span(item, "sourceSpan"),
                            candidate);
                        if (source.Key.Page < 1 || source.Key.LineIds.Count == 0 ||
                            source.Key.LineIds.Any(string.IsNullOrWhiteSpace))
                        {
                            errors.Add("INVALID_CHECKPOINT_SOURCE:" + candidate);
                        }
                        else if (!selectedByCandidate.TryAdd(candidate, source))
                            errors.Add("DUPLICATE_CHECKPOINT_CANDIDATE:" + candidate);
                        else if (!selectedByKey.TryAdd(source.Key, source))
                            errors.Add("DUPLICATE_CHECKPOINT_SOURCE_IDENTITY:" + source.Key.Value);
                        else if (source.SourceSpan is { } sourceSpan &&
                                 (sourceSpan.Start < 0 || sourceSpan.End < sourceSpan.Start ||
                                  sourceSpan.End > source.SourceText.Length))
                            errors.Add("INVALID_CHECKPOINT_SOURCE_SPAN:" + candidate);
                    }
                }
                else if (lane == "semantic")
                {
                    foreach (var item in Array(payload, "blocks"))
                    {
                        var candidate = String(item, "id");
                        if (!selectedByCandidate.TryGetValue(candidate, out var source))
                        {
                            errors.Add("SEMANTIC_CHECKPOINT_UNJOINED:" + candidate);
                            continue;
                        }
                        var lineIds = Strings(item, "lineIds");
                        if (!source.Key.LineIds.SequenceEqual(lineIds, StringComparer.Ordinal))
                        {
                            errors.Add("SEMANTIC_SOURCE_IDENTITY_MISMATCH:" + candidate);
                            continue;
                        }
                        semanticProposals.Add(new PdfReplaySemanticProposal(
                            source.Key, String(item, "role"), Number(item, "confidence"), String(item, "reason")));
                    }
                }
                else if (lane == "span")
                {
                    foreach (var item in Array(payload, "blocks"))
                    {
                        if (!Bool(item, "resolved")) continue;
                        var candidate = String(item, "id");
                        if (!selectedByCandidate.TryGetValue(candidate, out var source))
                        {
                            errors.Add("SPAN_UNJOINED:" + candidate);
                            continue;
                        }
                        var lineIds = Strings(item, "lineIds");
                        if (!source.Key.LineIds.SequenceEqual(lineIds, StringComparer.Ordinal))
                        {
                            errors.Add("SPAN_SOURCE_IDENTITY_MISMATCH:" + candidate);
                            continue;
                        }
                        var start = Int(item, "start");
                        var end = Int(item, "end");
                        if (start < 0 || end < start || end > source.SourceText.Length)
                        {
                            errors.Add("INVALID_CHECKPOINT_SPAN:" + candidate);
                            continue;
                        }
                        spanProposals.Add(new PdfReplaySpanProposal(
                            source.Key, new TextOffsetSpan(start, end)));
                    }
                }
            }
            catch (JsonException ex)
            {
                errors.Add("CHECKPOINT_JSON_INVALID:" + ex.Message);
            }
        }

        if (selectedByKey.Count == 0)
            errors.Add("CHECKPOINT_SELECTION_MISSING");

        return new CheckpointReplayResult(
            selectedByKey.Values.OrderBy(source => source.Key.Value, StringComparer.Ordinal).ToArray(),
            semanticProposals.OrderBy(proposal => proposal.Source.Value, StringComparer.Ordinal).ToArray(),
            spanProposals.OrderBy(proposal => proposal.Source.Value, StringComparer.Ordinal).ToArray(), errors);
    }

    public static StageJoin JoinStage(
        IEnumerable<PdfReplaySourceKey> baseline,
        IEnumerable<PdfReplaySourceKey> current)
    {
        var oldKeys = baseline.ToHashSet();
        var newKeys = current.ToHashSet();
        return new StageJoin(
            oldKeys.Intersect(newKeys).Count(),
            oldKeys.Except(newKeys).Count(),
            newKeys.Except(oldKeys).Count());
    }

    public static SelectionJoin JoinSelections(
        IReadOnlyList<PdfReplaySourceEntry> baseline,
        IReadOnlyList<PdfReplaySourceEntry> current)
    {
        var oldByKey = baseline.ToDictionary(source => source.Key);
        var newByKey = current.ToDictionary(source => source.Key);
        var joined = oldByKey.Keys.Intersect(newByKey.Keys).ToArray();
        return new SelectionJoin(
            joined.Length,
            oldByKey.Keys.Except(newByKey.Keys).Count(),
            newByKey.Keys.Except(oldByKey.Keys).Count(),
            joined.Count(key => oldByKey[key].SourceTextSha256 != newByKey[key].SourceTextSha256));
    }

    private static PdfReplaySourceKey? ResolveParent(
        string? candidateId,
        IReadOnlyDictionary<string, PdfReplaySourceEntry> byCandidate,
        ICollection<string> errors,
        string stage)
    {
        if (candidateId is null) return null;
        if (byCandidate.TryGetValue(candidateId, out var source)) return source.Key;
        errors.Add($"{stage}_UNJOINED:{candidateId}");
        return null;
    }

    private static JsonElement[] Array(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && TryProperty(value, property, out var array) &&
        array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().ToArray()
            : [];

    private static string[] Strings(JsonElement value, string property) => Array(value, property)
        .Select(item => item.GetString() ?? "").ToArray();

    private static string String(JsonElement value, string property) =>
        TryProperty(value, property, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString() ?? ""
            : "";

    private static int Int(JsonElement value, string property) =>
        TryProperty(value, property, out var item) && item.TryGetInt32(out var result) ? result : -1;

    private static double Number(JsonElement value, string property) =>
        TryProperty(value, property, out var item) && item.TryGetDouble(out var result) ? result : 0;

    private static bool Bool(JsonElement value, string property) =>
        TryProperty(value, property, out var item) && item.ValueKind == JsonValueKind.True;

    private static TextOffsetSpan? Span(JsonElement value, string property)
    {
        if (!TryProperty(value, property, out var item) || item.ValueKind != JsonValueKind.Object) return null;
        var start = Int(item, "start");
        var end = Int(item, "end");
        return start >= 0 && end >= start ? new TextOffsetSpan(start, end) : null;
    }

    private static bool TryProperty(JsonElement value, string property, out JsonElement result)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out result))
            return true;
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var candidate in value.EnumerateObject())
            {
                if (string.Equals(candidate.Name, property, StringComparison.OrdinalIgnoreCase))
                {
                    result = candidate.Value;
                    return true;
                }
            }
        }
        result = default;
        return false;
    }

    private static string Sha256(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

internal sealed class PdfReplaySourceKey : IEquatable<PdfReplaySourceKey>
{
    public PdfReplaySourceKey(int page, IReadOnlyList<string> lineIds)
    {
        Page = page;
        LineIds = lineIds.ToArray();
    }

    public int Page { get; }
    public IReadOnlyList<string> LineIds { get; }
    public string Value => Page.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" +
        string.Join("\u001f", LineIds);

    public bool Equals(PdfReplaySourceKey? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as PdfReplaySourceKey);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
}

internal sealed record PdfReplaySourceEntry(
    PdfReplaySourceKey Key,
    string SourceText,
    string SourceTextSha256,
    TextOffsetSpan? SourceSpan,
    [property: JsonIgnore] string CandidateIdDiagnostic);

internal sealed record PdfReplaySemanticProposal(
    PdfReplaySourceKey Source,
    string Role,
    double Confidence,
    string? Reason);

internal sealed record PdfReplaySpanProposal(PdfReplaySourceKey Source, TextOffsetSpan Span);

internal sealed record PdfReplayHierarchyProposal(
    PdfReplaySourceKey Source,
    int Level,
    PdfReplaySourceKey? ProposedParent,
    PdfReplaySourceKey? ResolvedParent,
    string Resolution);

internal sealed record PdfR5ReplayFixture(
    int SchemaVersion,
    string DocumentId,
    string Revision,
    string DocxSha256,
    string PdfSha256,
    IReadOnlyList<PdfReplaySourceEntry> SelectedSources,
    IReadOnlyList<PdfReplaySemanticProposal> SemanticProposals,
    IReadOnlyList<PdfReplaySpanProposal> SpanProposals,
    IReadOnlyList<PdfReplayHierarchyProposal> HierarchyProposals,
    PdfReplayOracle Oracle)
{
    public string? PdfLineExtractionFingerprint { get; init; }
    public string? ModelContractHash { get; init; }
}

internal sealed record PdfReplayOracle(
    JsonElement? FinalStructure,
    JsonElement? OutputDecisions,
    JsonElement? ProductOutput,
    JsonElement? FinalHeadingRecords)
{
    public static PdfReplayOracle From(JsonElement? value)
    {
        if (value is not { } root || root.ValueKind != JsonValueKind.Object)
            return new(null, null, null, null);

        return new(
            Child(root, "finalStructure"),
            Child(root, "outputDecisions"),
            Child(root, "productOutput"),
            Child(root, "finalHeadingRecords"));
    }

    private static JsonElement? Child(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) ? value.Clone() : null;
}

internal sealed record ReplayBuildResult(PdfR5ReplayFixture? Fixture, IReadOnlyList<string> Errors);

internal sealed record CheckpointReplayResult(
    IReadOnlyList<PdfReplaySourceEntry> SelectedSources,
    IReadOnlyList<PdfReplaySemanticProposal> SemanticProposals,
    IReadOnlyList<PdfReplaySpanProposal> SpanProposals,
    IReadOnlyList<string> Errors);

internal sealed record SelectionJoin(
    int Joined,
    int UnjoinedBaseline,
    int UnjoinedCurrent,
    int SourceTextMismatches);

internal sealed record StageJoin(int Joined, int UnjoinedBaseline, int UnjoinedCurrent);
