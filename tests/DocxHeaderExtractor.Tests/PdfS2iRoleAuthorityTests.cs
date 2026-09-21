using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfS2iRoleAuthorityTests
{
    private const string Root =
        "eval/a99-closed-loop/semantic-text-replay-successor-v1-runtime-authority/DOC-0252";
    private const string GoldPath =
        "eval/a99-closed-loop/canonical-semantic-gold-vnext/occurrence/DOC-0252.occurrence-gold.v1.json";
    private const string PreflightPath =
        "eval/a99-closed-loop/semantic-text-replay-baseline-v1-runtime-authority/DOC-0252/preflight.v1.json";
    private const string SourceHash =
        "a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61";
    private const string SourceUniverseHash =
        "cb3c9af67a7f17fd9560b56cf23bb9648a9fcfe3ea4eb5d333ac7282176bfc66";
    private const string PromptHash =
        "8b056f1722b356dd9e836908b8d05ad0850353a06fbc0a4aa39db568fd47f0a8";
    private const string SemanticContractHash =
        "21687e5a78d59b8c82124dcc567c7353024145d599b1294c41c888ffe4512365";
    private const string SuccessorManifestHash =
        "2ac4513d81d63fcaab23cf26d0fbacf4bd5ac741770eeb0f4c4ce9c0fcbd3474";
    private const int OwnedPerSegment = 120;
    private const int VisibleMargin = 20;

    [Fact]
    public void Audit_role_authority_and_freeze_strict_persistent_omissions()
    {
        var gold = JsonSerializer.Deserialize<PdfGoldDocument>(
            File.ReadAllText(RepositoryPath(GoldPath)))!;
        var goldBound = PdfGoldBoundOccurrenceEvaluator.BindGold(
            gold, LoadBundle(1).AliasCatalog, out var bindingIssues);
        Assert.Empty(bindingIssues);
        Assert.Equal(41, goldBound.Count);

        var roleRows = new List<RoleAuditRow>();
        var falseNegativeKinds = new Dictionary<string, IReadOnlyDictionary<string, string>>(
            StringComparer.Ordinal);
        var falseNegativeKeys = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        for (var repeat = 1; repeat <= 3; repeat++)
        {
            var bundle = LoadBundle(repeat);
            var replay = SemanticAuthorityReplay.Replay(bundle, SourceHash, SourceUniverseHash);
            var predicted = replay.Pipeline.BoundHeadings
                .Select(item => new PdfBoundOccurrence(
                    item.Parts,
                    item.SemanticRole,
                    item.Alias,
                    ParentFromHints(item.RelationHints)))
                .ToArray();
            var runtimeByKey = replay.Pipeline.BoundHeadings
                .ToDictionary(item => Key(new PdfBoundOccurrence(
                    item.Parts,
                    item.SemanticRole,
                    item.Alias,
                    ParentFromHints(item.RelationHints))), StringComparer.Ordinal);
            var goldByKey = goldBound
                .Select((item, index) => new { item, Gold = gold.Headings[index] })
                .ToDictionary(item => Key(item.item), StringComparer.Ordinal);

            foreach (var key in goldByKey.Keys.Intersect(runtimeByKey.Keys, StringComparer.Ordinal)
                         .Order(StringComparer.Ordinal))
            {
                var expected = goldByKey[key];
                var actual = runtimeByKey[key];
                roleRows.Add(new(
                    $"r{repeat}",
                    key,
                    expected.Gold.SourceAlias,
                    expected.Gold.SemanticRole,
                    actual.SemanticRole,
                    actual.StructuralType,
                    actual.Scope,
                    actual.SemanticRole,
                    "UNRESOLVED"));
            }

            var kinds = ClassifyFalseNegatives(goldBound, predicted);
            falseNegativeKinds[$"r{repeat}"] = kinds;
            falseNegativeKeys[$"r{repeat}"] = goldBound
                .Where(item => kinds.ContainsKey(Key(item)))
                .Select(Key)
                .Order(StringComparer.Ordinal)
                .ToArray();
        }

        Assert.Equal(55, roleRows.Count);
        Assert.All(roleRows, row => Assert.Equal("UNRESOLVED", row.Classification));

        var strictKeys = falseNegativeKinds.Values
            .Select(items => items
                .Where(item => item.Value == "TRUE_MODEL_OMISSION")
                .Select(item => item.Key)
                .ToHashSet(StringComparer.Ordinal))
            .Aggregate((left, right) =>
            {
                left.IntersectWith(right);
                return left;
            });
        Assert.Equal(11, strictKeys.Count);

        var aliasByKey = goldBound
            .Select((item, index) => new { item, Gold = gold.Headings[index] })
            .ToDictionary(item => Key(item.item), item => item.Gold, StringComparer.Ordinal);
        var aliases = LoadBundle(1).AliasCatalog.ToDictionary(item => item.Alias, StringComparer.Ordinal);
        var strictRows = strictKeys
            .Order(StringComparer.Ordinal)
            .Select(key =>
            {
                var heading = aliasByKey[key];
                var alias = aliases[heading.SourceAlias];
                var segment = alias.SourceOrdinal / OwnedPerSegment;
                return new StrictOmissionRow(
                    key,
                    heading.SourceAlias,
                    alias.Text,
                    heading.SemanticRole,
                    alias.SourceOrdinal,
                    segment,
                    $"segment-{segment:000}");
            })
            .ToArray();

        var persistentResiduals = falseNegativeKinds["r1"].Keys
            .Intersect(falseNegativeKinds["r2"].Keys, StringComparer.Ordinal)
            .Intersect(falseNegativeKinds["r3"].Keys, StringComparer.Ordinal)
            .Where(key => !strictKeys.Contains(key))
            .Order(StringComparer.Ordinal)
            .Select(key => new
            {
                key,
                perRepeat = new
                {
                    r1 = falseNegativeKinds["r1"][key],
                    r2 = falseNegativeKinds["r2"][key],
                    r3 = falseNegativeKinds["r3"][key],
                },
            })
            .ToArray();

        if (string.Equals(Environment.GetEnvironmentVariable("A99_S2I_AUDIT"), "1", StringComparison.Ordinal))
        {
            WriteRoleAudit(roleRows);
            WriteI6Preflight(strictRows, persistentResiduals);
        }
    }

    private static void WriteRoleAudit(IReadOnlyList<RoleAuditRow> rows)
    {
        var artifact = new
        {
            artifactKind = "a99_pdf_s2i_role_authority_audit",
            schemaVersion = "a99-pdf-s2i-role-authority-audit-v1",
            baselineCommit = "56eed18",
            sourceHash = SourceHash,
            sourceUniverseHash = SourceUniverseHash,
            goldHash = CanonicalArtifactHash.OfTextFile(RepositoryPath(GoldPath)),
            evaluator = PdfGoldBoundOccurrenceEvaluator.ContractVersion,
            roleEvaluatorField = "CanonicalSemanticBoundHeading.SemanticRole",
            goldRoleField = "PdfGoldHeading.SemanticRole",
            runtimeProposalRoleField = "CanonicalSemanticProposal.SemanticRole",
            runtimeStructuralTypeField = "CanonicalSemanticProposal.StructuralType",
            runtimeScopeField = "CanonicalSemanticProposal.Scope",
            evaluatorComparePath = new[]
            {
                "SemanticAuthorityReplay.Replay",
                "CanonicalSemanticPipeline.RunAliases",
                "CanonicalSemanticExactBinder.Bind",
                "CanonicalSemanticBoundHeading.SemanticRole",
                "PdfGoldBoundOccurrenceEvaluator.EvaluateBound",
                "PdfSemanticRoleScore",
            },
            roleOntologyIsClosed = false,
            roleOntologyAuthority =
                "The live canonical schema declares semanticRole as an optional string; the frozen discovery prompt does not define a role vocabulary.",
            roleRequiredDuringDiscovery = false,
            structuralTypeAndSemanticRoleConflated = false,
            scorerDecision = "ROLE_SCORING_NOT_CURRENTLY_ADJUDICABLE",
            roleMismatchClassificationPolicy =
                "UNRESOLVED unless an existing canonical mapping proves equivalence; no such mapping exists for this contract.",
            modelCalls = 0,
            providerCalls = 0,
            matchedOccurrences = rows.Count,
            mismatches = rows,
        };
        WriteArtifact("role-authority-audit.v1.json", artifact);
    }

    private static void WriteI6Preflight(
        IReadOnlyList<StrictOmissionRow> strictRows,
        IReadOnlyList<object> persistentResiduals)
    {
        var segments = strictRows
            .GroupBy(row => row.SegmentIndex)
            .OrderBy(group => group.Key)
            .Select(group => new
            {
                segmentIndex = group.Key,
                segmentId = group.First().SegmentId,
                baselineRequestSha256 = BaselineRequestHashes()[group.Key],
                targetOccurrences = group.Select(row => new { row.Key, row.SourceAlias, row.SourceOrdinal, row.GoldRole }),
                ownedPerSegment = OwnedPerSegment,
                visibleMargin = VisibleMargin,
                targeting = "original full segment input; no custom window",
            })
            .ToArray();
        var affectedSegmentCount = segments.Length;

        var artifact = new
        {
            artifactKind = "a99_pdf_s2i_i6_preflight",
            schemaVersion = "a99-pdf-s2i-i6-preflight-v1",
            baselineCommit = "56eed18",
            sourceHash = SourceHash,
            sourceUniverseHash = SourceUniverseHash,
            goldHash = CanonicalArtifactHash.OfTextFile(RepositoryPath(GoldPath)),
            baselinePromptHash = PromptHash,
            baselineSemanticContractHash = SemanticContractHash,
            baselineSuccessorManifestHash = SuccessorManifestHash,
            strictCausalCohort = new
            {
                name = "STRICT_PERSISTENT_TRUE_OMISSION",
                count = strictRows.Count,
                baselineExactRecoveries = "0/3 for every target",
                rows = strictRows,
            },
            mixedPersistentResiduals = persistentResiduals,
            segmentation = new
            {
                ownedPerSegment = OwnedPerSegment,
                visibleMargin = VisibleMargin,
                affectedSegments = segments,
            },
            i6Question =
                "Does removing the semantic-role classification obligation during discovery recover strict persistent omissions?",
            promptSchemaDelta = new
            {
                status = "NO_CONTRACT_DELTA_AVAILABLE",
                exactDiff = "empty",
                reason =
                    "The current discovery prompt does not require semanticRole and the live schema already makes semanticRole optional string; removing an obligation that is absent cannot form a causal contrast.",
            },
            i6Authority = new
            {
                status = "NOT_CREATED",
                reason = "A new experiment authority requires a real prompt or schema delta; no valid delta was found.",
            },
            callPlan = new
            {
                repeats = 3,
                affectedSegmentCount,
                primaryCalls = affectedSegmentCount * 3,
                maxProviderCalls = affectedSegmentCount * 3,
                placementCalls = 0,
            },
            modelCalls = 0,
            providerCalls = 0,
            finalState = "I6_PREFLIGHT_BLOCKED",
        };
        WriteArtifact("i6-preflight.v1.json", artifact);
    }

    private static SemanticAuthorityReplayBundle LoadBundle(int repeat)
    {
        var path = Directory.GetFiles(RepositoryPath(
            $"eval/a99-closed-loop/semantic-text-replay-baseline-v1-runtime-authority/DOC-0252/r{repeat}"), "*.json")
            .Single();
        return JsonSerializer.Deserialize<SemanticAuthorityReplayBundle>(File.ReadAllText(path))!;
    }

    private static IReadOnlyDictionary<string, string> ClassifyFalseNegatives(
        IReadOnlyList<PdfBoundOccurrence> gold,
        IReadOnlyList<PdfBoundOccurrence> predicted)
    {
        var classifications = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var expected in gold)
        {
            var expectedKey = Key(expected);
            if (predicted.Any(item => Key(item) == expectedKey)) continue;
            var sameAlias = predicted.Where(item =>
                string.Equals(item.SourceAlias, expected.SourceAlias, StringComparison.Ordinal)).ToArray();
            if (sameAlias.Length == 0)
            {
                classifications[expectedKey] = "TRUE_MODEL_OMISSION";
                continue;
            }

            var expectedPart = expected.Parts.Count == 1 ? expected.Parts[0] : null;
            if (expectedPart is not null && sameAlias.Any(item =>
                item.Parts.Any(part =>
                    part.SourceId == expectedPart.SourceId &&
                    part.Start == expectedPart.Start &&
                    part.End == expectedPart.End) &&
                item.Parts.Count > 1))
            {
                classifications[expectedKey] = "SUPERSET_HEADING";
                continue;
            }

            if (expectedPart is not null && sameAlias.Any(item => item.Parts.Count == 1 &&
                item.Parts[0].SourceId == expectedPart.SourceId &&
                item.Parts[0].Start < expectedPart.End &&
                item.Parts[0].End > expectedPart.Start))
            {
                classifications[expectedKey] = "MODEL_WRONG_TEXT_BOUNDARY";
                continue;
            }

            classifications[expectedKey] = "MODEL_WRONG_SPAN";
        }
        return classifications;
    }

    private static string Key(PdfBoundOccurrence item) =>
        string.Join(';', item.Parts.Select(part => $"{part.SourceId}:{part.Start}:{part.End}"));

    private static string? ParentFromHints(IReadOnlyList<string> hints)
    {
        var hint = hints.FirstOrDefault(item => item.StartsWith("parent-node:", StringComparison.Ordinal));
        if (hint is null) return null;
        var value = hint["parent-node:".Length..];
        return value is "ROOT" or "NONE" ? null : value;
    }

    private static void WriteArtifact(string fileName, object artifact) =>
        File.WriteAllText(
            RepositoryPath(Root + "/" + fileName),
            JsonSerializer.Serialize(artifact, new JsonSerializerOptions { WriteIndented = true }));

    private static IReadOnlyList<string> BaselineRequestHashes()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryPath(PreflightPath)));
        return document.RootElement
            .GetProperty("request")
            .GetProperty("userPayloadSha256")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();
    }

    private static string RepositoryPath(string relativePath) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..", relativePath));

    private sealed record RoleAuditRow(
        string Repeat,
        string CanonicalOccurrenceKey,
        string SourceAlias,
        string GoldRole,
        string RuntimeSemanticRole,
        string RuntimeStructuralType,
        string RuntimeScope,
        string EvaluatorValue,
        string Classification);

    private sealed record StrictOmissionRow(
        string Key,
        string SourceAlias,
        string SourceText,
        string GoldRole,
        int SourceOrdinal,
        int SegmentIndex,
        string SegmentId);
}
