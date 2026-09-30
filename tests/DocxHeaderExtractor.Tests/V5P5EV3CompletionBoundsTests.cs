using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free audit and proof of finite v3 response bounds.</summary>
public sealed class V5P5EV3CompletionBoundsTests
{
    private const string ArtifactRoot = "artifacts/v5-p5e-v3-completion-bounds";
    private static readonly DocumentTaskContract Contract =
        DocxHeaderExtractor.DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
    private static readonly V5ProviderEnvelope Envelope =
        new("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300) { UsageInclude = true };
    private static readonly string[] Docs = [SourcePdfCorpus.Src089, SourcePdfCorpus.Src095];

    [Fact]
    public void Freeze_bounds_from_live_contract_and_historical_cohort_and_prove_serializer_ceiling()
    {
        var historical = AuditHistoricalResponses();
        Assert.Equal(31, historical.ResponseFiles);
        Assert.Equal(1448, historical.TotalClaims);
        Assert.Equal(10, historical.MaxClaimsPerSubject);
        Assert.Equal(10, historical.MaxRelationsPerSubject);
        Assert.Equal(6, historical.MaxSubjectParts);
        Assert.Equal(1, historical.MaxTargetParts);
        Assert.Equal(1, historical.MaxEvidenceNeeds);
        Assert.Equal(543, historical.MaxValueUtf8Bytes);
        Assert.Equal(318, historical.MaxSelectionStringUtf8Bytes);

        Assert.Equal(7, V5ClaimShapesV2_1.Generate(Contract).Count);
        Assert.Equal(6, Enum.GetValues<EvidenceNeed>().Length);
        Assert.Equal(42, V5SemanticDecisionResponseBoundsV3.DurableClaimIdUtf8Bytes);

        var packRows = new List<object>();
        var requestCount = 0;
        var maxOwned = 0;
        var maxResponseBytes = 0;
        var maxCompletionTokens = 0;
        V5PackedDecisionRequestV3? largestPack = null;
        IReadOnlyList<SemanticSourceAtom>? largestPackAtoms = null;
        IReadOnlyList<V5PackedDecisionRequestV3>? largestDocPacks = null;

        foreach (var (documentId, pdf, expectedPacks) in new[]
        {
            ("SRC-089", Docs[0], 7),
            ("SRC-095", Docs[1], 24),
        })
        {
            var built = V5PdfPreflightBuilder.BuildV3(TestRepository.Path(pdf), documentId, Contract,
                V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, Envelope);
            Assert.Equal(expectedPacks, built.Count);
            var atoms = V5PdfPreflightBuilder.LoadAtoms(TestRepository.Path(pdf));
            var ownedAliases = built.SelectMany(item => item.OwnedAliases).ToArray();
            Assert.Equal(atoms.Select(atom => atom.Alias).ToHashSet(StringComparer.Ordinal), ownedAliases.ToHashSet(StringComparer.Ordinal));
            Assert.Equal(ownedAliases.Length, ownedAliases.Distinct(StringComparer.Ordinal).Count());

            foreach (var pack in built)
            {
                requestCount++;
                var bounds = pack.Request.ResponseBounds;
                var maxTokens = V5SemanticCompletionBudget.Compute(pack.OwnedAliases.Count, pack.VisibleAliases.Count,
                    pack.Request.Utf8Bytes, V5SemanticDecisionResponseBoundsV3.ProviderCompletionCeiling);
                Assert.Equal(pack.OwnedAliases.Count, bounds.MaxDecisions);
                Assert.True(bounds.MaxResponseUtf8Bytes <= maxTokens,
                    $"{documentId}:{pack.PackId}:responseBytes={bounds.MaxResponseUtf8Bytes}:completionTokens={maxTokens}");
                Assert.True(maxTokens <= V5SemanticDecisionResponseBoundsV3.ProviderCompletionCeiling);

                var responseSchema = JsonDocument.Parse(pack.Request.Prompt).RootElement
                    .GetProperty("responseSchema").GetProperty("properties");
                var decisions = responseSchema.GetProperty("decisions");
                Assert.Equal(pack.OwnedAliases.Count, decisions.GetProperty("minItems").GetInt32());
                Assert.Equal(pack.OwnedAliases.Count, decisions.GetProperty("maxItems").GetInt32());
                var claimSchema = decisions.GetProperty("items").GetProperty("properties").GetProperty("claims");
                Assert.Equal(10, claimSchema.GetProperty("maxItems").GetInt32());
                var claim = claimSchema.GetProperty("items").GetProperty("properties");
                Assert.Equal(543, claim.GetProperty("value").GetProperty("maxLength").GetInt32());
                Assert.Equal(6, claim.GetProperty("evidenceNeeds").GetProperty("maxItems").GetInt32());
                Assert.Equal(42, claim.GetProperty("existingClaimId").GetProperty("maxLength").GetInt32());
                Assert.Equal(bounds.MaxResponseUtf8Bytes, JsonDocument.Parse(pack.Request.Prompt).RootElement
                    .GetProperty("responseSchema").GetProperty("maxSerializedUtf8Bytes").GetInt32());

                var empty = new V5SemanticDecisionResponseV3(pack.OwnedAliases
                    .Select(_ => new V5SemanticSubjectDecisionV3([])).ToArray());
                var emptyBytes = JsonSerializer.SerializeToUtf8Bytes(empty, CanonicalJson.Options).Length;
                Assert.True(emptyBytes <= bounds.MaxResponseUtf8Bytes);
                packRows.Add(new
                {
                    documentId,
                    packId = pack.PackId,
                    ownedCount = pack.OwnedAliases.Count,
                    visibleCount = pack.VisibleAliases.Count,
                    requestHash = pack.Request.RequestHash,
                    schemaHash = pack.Request.SchemaHash,
                    requestUtf8Bytes = pack.Request.Utf8Bytes,
                    maxClaimsPerDecision = bounds.MaxClaimsPerDecision,
                    maxSubjectParts = bounds.MaxSubjectParts,
                    maxTargetParts = bounds.MaxTargetParts,
                    maxEvidenceNeeds = bounds.MaxEvidenceNeeds,
                    maxValueUtf8Bytes = bounds.MaxValueUtf8Bytes,
                    maxSelectionStringUtf8Bytes = bounds.MaxSelectionStringUtf8Bytes,
                    maxResponseUtf8Bytes = bounds.MaxResponseUtf8Bytes,
                    emptyLedgerSerializedUtf8Bytes = emptyBytes,
                    configuredCompletionTokens = maxTokens,
                    providerRequestHash = pack.ProviderRequestHash,
                    providerRequestBytes = pack.ProviderRequestBytes,
                });
                if (largestPack is null || pack.OwnedAliases.Count > largestPack.OwnedAliases.Count)
                {
                    largestPack = pack;
                    largestPackAtoms = atoms;
                    largestDocPacks = built;
                }
                maxOwned = Math.Max(maxOwned, pack.OwnedAliases.Count);
                maxResponseBytes = Math.Max(maxResponseBytes, bounds.MaxResponseUtf8Bytes);
                maxCompletionTokens = Math.Max(maxCompletionTokens, maxTokens);
            }
        }

        Assert.Equal(31, requestCount);
        Assert.Equal(96, maxOwned);
        Assert.Equal(25088, maxResponseBytes);
        Assert.Equal(25088, maxCompletionTokens);
        var maxResponseUtf8Bytes = maxResponseBytes;
        Assert.NotNull(largestPack);
        Assert.NotNull(largestPackAtoms);
        Assert.NotNull(largestDocPacks);

        // Serialize the independent maxima themselves. This deliberately exceeds the aggregate
        // response envelope and proves both parser and runtime reject it whole, with no truncation.
        var oversized = BuildIndependentMaximaFixture(largestPack!.Request.ResponseBounds);
        var oversizedBytes = JsonSerializer.SerializeToUtf8Bytes(oversized, CanonicalJson.Options).Length;
        Assert.True(oversizedBytes > largestPack.Request.ResponseBounds.MaxResponseUtf8Bytes);
        var raw = JsonSerializer.Serialize(oversized, CanonicalJson.Options);
        Assert.Throws<InvalidOperationException>(() => V5SemanticDecisionContractV3.Parse(
            JsonDocument.Parse(raw).RootElement, Contract, largestPack.OwnedAliases.Count,
            largestPack.Packet.ContextOnlyEvidence.Count));
        var scope = ClaimBindingScope.Create(largestPack.OwnedAliases, largestPack.VisibleAliases);
        var refused = V5SemanticDecisionContractV3.Bind("p5e-overflow", oversized, Contract,
            largestPack.Packet.SubjectEvidence, largestPack.Packet.ContextOnlyEvidence, largestPackAtoms!, scope);
        Assert.Null(refused.Binding);
        Assert.Contains("decision-response-byte-budget-exceeded", refused.Refusals.Values.Single(), StringComparison.Ordinal);
        Assert.Empty(refused.Bound);

        // Independent per-field/cardinality bounds are also enforced on typed runtime responses.
        var tooManyClaims = new V5SemanticDecisionResponseV3(largestPack.OwnedAliases.Select((_, index) =>
            new V5SemanticSubjectDecisionV3(index == 0
                ? Enumerable.Range(0, 11).Select(_ => new V5SemanticDecisionClaimV3("DOCUMENT_IDENTITY", "x", EvidenceNeeds: [])).ToArray()
                : [])).ToArray());
        var countRefused = V5SemanticDecisionContractV3.Bind("p5e-too-many-claims", tooManyClaims, Contract,
            largestPack.Packet.SubjectEvidence, largestPack.Packet.ContextOnlyEvidence, largestPackAtoms!, scope);
        Assert.Null(countRefused.Binding);
        Assert.Contains("claims", countRefused.Refusals.Values.Single(), StringComparison.Ordinal);

        var utf8Overflow = new V5SemanticDecisionResponseV3(largestPack.OwnedAliases.Select((_, index) =>
            new V5SemanticSubjectDecisionV3(index == 0
                ? [new V5SemanticDecisionClaimV3("DOCUMENT_IDENTITY", new string('é', 272), EvidenceNeeds: [])]
                : [])).ToArray());
        Assert.Throws<InvalidOperationException>(() => V5SemanticDecisionContractV3.ValidateBounds(utf8Overflow,
            Contract, largestPack.OwnedAliases.Count, largestPack.Packet.ContextOnlyEvidence.Count));

        Write("audit.v1.json", new
        {
            schemaVersion = "v5-p5e-v3-completion-bounds-audit-v1",
            providerCalls = 0,
            goldRead = false,
            historicalV2_1 = historical,
            liveContract = new
            {
                unaryShapes = Contract.Predicates.Count,
                relationShapes = Contract.Relations.Count,
                claimShapes = V5ClaimShapesV2_1.Generate(Contract).Count,
                evidenceNeedValues = Enum.GetNames<EvidenceNeed>(),
                durableClaimIdUtf8Bytes = V5SemanticDecisionResponseBoundsV3.DurableClaimIdUtf8Bytes,
            },
            selectedBounds = new
            {
                claimsPerDecision = 10,
                sourcePartsPerSubject = 6,
                relationTargetParts = 6,
                evidenceNeeds = 6,
                valueUtf8Bytes = 543,
                selectionStringUtf8Bytes = 318,
                existingClaimIdUtf8Bytes = 42,
                maxSerializedResponseUtf8Bytes = "min(32768, 512 + ownedCount * 256)",
                decisions = "exactly ownedCount",
            },
            cohort = new
            {
                requests = requestCount,
                maxOwnedDecisions = maxOwned,
                maxResponseUtf8Bytes,
                maxConfiguredCompletionTokens = maxCompletionTokens,
                maxProviderCompletionCeiling = V5SemanticDecisionResponseBoundsV3.ProviderCompletionCeiling,
                modelDocumentedCompletionCeiling = 65536,
                maxIndependentBoundsFixtureBytes = oversizedBytes,
                independentFixtureRejectedWhole = true,
                acceptedResponseByteProof = "Parse and Bind serialize the typed v3 response with CanonicalJson.Options and reject if its exact UTF-8 bytes exceed the per-pack ceiling; pre-parse wire bytes are also capped. Therefore every accepted serializer result is <= MaxResponseUtf8Bytes <= configured max_tokens <= 32768.",
            },
            rows = packRows,
        });
    }

    private static V5SemanticDecisionResponseBoundsV3Fixture AuditHistoricalResponses()
    {
        var files = Directory.GetDirectories(TestRepository.Path("artifacts/v5-provider-cohort-31-windows/provider/calls"))
            .SelectMany(path => Directory.GetFiles(path, "content.txt")).ToArray();
        var claimCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var relationCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var totalClaims = 0;
        var maxSubjectParts = 0;
        var maxTargetParts = 0;
        var maxNeeds = 0;
        var maxValueBytes = 0;
        var maxSelectionBytes = 0;
        var relationNames = new HashSet<string>(["PARENT_OF", "REFERENCES", "SAME_ENTITY", "CONTINUES"], StringComparer.Ordinal);
        foreach (var path in files)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var pack = Path.GetFileName(Path.GetDirectoryName(path));
            foreach (var claim in json.RootElement.GetProperty("claims").EnumerateArray())
            {
                totalClaims++;
                var subject = claim.GetProperty("subject").GetProperty("sourceParts").EnumerateArray().ToArray();
                var aliases = string.Join(",", subject.Select(part => part.GetProperty("sourceAlias").GetString()));
                var key = $"{pack}|{aliases}";
                claimCounts[key] = claimCounts.GetValueOrDefault(key) + 1;
                if (relationNames.Contains(claim.GetProperty("predicate").GetString()!))
                    relationCounts[key] = relationCounts.GetValueOrDefault(key) + 1;
                maxSubjectParts = Math.Max(maxSubjectParts, subject.Length);
                var targetParts = claim.TryGetProperty("object", out var target) && target.ValueKind == JsonValueKind.Object
                    ? target.GetProperty("sourceParts").GetArrayLength() : 0;
                maxTargetParts = Math.Max(maxTargetParts, targetParts);
                maxNeeds = Math.Max(maxNeeds, claim.GetProperty("evidenceNeeds").GetArrayLength());
                if (claim.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String)
                    maxValueBytes = Math.Max(maxValueBytes, Encoding.UTF8.GetByteCount(value.GetString()!));
                foreach (var part in subject.Concat(targetParts > 0 ? target.GetProperty("sourceParts").EnumerateArray() : []))
                    foreach (var field in new[] { "verbatimText", "leftExactContext", "rightExactContext" })
                        if (part.TryGetProperty(field, out var selection) && selection.ValueKind == JsonValueKind.String)
                            maxSelectionBytes = Math.Max(maxSelectionBytes, Encoding.UTF8.GetByteCount(selection.GetString()!));
            }
        }
        return new V5SemanticDecisionResponseBoundsV3Fixture(
            files.Length,
            totalClaims,
            claimCounts.Values.DefaultIfEmpty().Max(),
            relationCounts.Values.DefaultIfEmpty().Max(),
            maxSubjectParts,
            maxTargetParts,
            maxNeeds,
            maxValueBytes,
            maxSelectionBytes);
    }

    private static V5SemanticDecisionResponseV3 BuildIndependentMaximaFixture(V5SemanticDecisionResponseBoundsV3 bounds)
    {
        var selection = new V5DecisionTextSelectionV3(new string('s', bounds.MaxSelectionStringUtf8Bytes), int.MaxValue,
            new string('l', bounds.MaxSelectionStringUtf8Bytes), new string('r', bounds.MaxSelectionStringUtf8Bytes));
        return new V5SemanticDecisionResponseV3(Enumerable.Range(0, bounds.MaxDecisions).Select(decisionIndex =>
        {
            var additionalCount = Math.Min(bounds.MaxSubjectParts - 1, Math.Max(0, bounds.MaxDecisions - decisionIndex - 1));
            var additional = Enumerable.Range(decisionIndex + 1, additionalCount)
                .Select(index => new V5AdditionalOwnedSubjectPartV3(index, selection)).ToArray();
            var claims = Enumerable.Range(0, bounds.MaxClaimsPerDecision).Select(_ =>
                new V5SemanticDecisionClaimV3("PARENT_OF", SubjectSelection: selection,
                    AdditionalSubjectParts: additional,
                    TargetParts: Enumerable.Range(0, bounds.MaxTargetParts).Select(index =>
                        new V5VisibleTargetPartV3("OWNED", index % Math.Max(1, bounds.MaxDecisions), selection)).ToArray(),
                    State: ClaimResolutionState.OPEN,
                    EvidenceNeeds: Enum.GetValues<EvidenceNeed>(),
                    ExistingClaimId: "v5claim21-" + new string('x', 32))).ToArray();
            return new V5SemanticSubjectDecisionV3(claims);
        }).ToArray());
    }

    private sealed record V5SemanticDecisionResponseBoundsV3Fixture(
        int ResponseFiles, int TotalClaims, int MaxClaimsPerSubject, int MaxRelationsPerSubject,
        int MaxSubjectParts, int MaxTargetParts, int MaxEvidenceNeeds,
        int MaxValueUtf8Bytes, int MaxSelectionStringUtf8Bytes);

    private static void Write(string name, object value)
    {
        var path = TestRepository.Path($"{ArtifactRoot}/{name}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, new UTF8Encoding(false));
    }
}
