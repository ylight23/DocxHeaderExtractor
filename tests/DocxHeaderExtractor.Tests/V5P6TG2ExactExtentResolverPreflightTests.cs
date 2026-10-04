using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free G2 preflight. It freezes eligible local candidate menus, not model results.</summary>
public sealed class V5P6TG2ExactExtentResolverPreflightTests
{
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string F1Root = "artifacts/v5-p6t-function-membership/p6tf1-preflight";
    private const string G1Path = "artifacts/v5-p6t-function-membership/p6tg1-candidate-projection/function-conditioned-candidate-projection.v1.json";
    private const string OutputRoot = "artifacts/v5-p6t-function-membership/p6tg2-exact-extent-preflight";
    private const string Protocol = "v5-function-conditioned-exact-extent-resolver-preflight-1";
    private static readonly (string Id, string Pdf)[] Documents =
    [
        ("SRC-089", SourcePdfCorpus.Src089),
        ("SRC-095", SourcePdfCorpus.Src095),
    ];

    private sealed record PreparedDocument(
        string DocumentId,
        PdfCandidateAuthorityDocumentPlan Plan,
        PdfCandidateAuthorityPreparedPack SourcePack,
        PdfFunctionMembershipPreparedPackF1 F1Pack,
        IReadOnlyDictionary<string, string> FunctionByAlias,
        IReadOnlyDictionary<string, string> OccurrenceByAlias,
        IReadOnlyDictionary<string, ProjectionClass> CandidateClasses);

    private enum ProjectionClass { ALL_ESTABLISHES, MIXED, NO_ESTABLISHES }

    [Fact]
    public void P6TG2_preflight_freezes_local_function_conditioned_extent_menus_and_excludes_representation_only_candidates()
    {
        using var f1Primary = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{F1Root}/result.v1.json")));
        using var f1Retry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{F1Root}/retry-src089-result.v1.json")));
        using var g1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(G1Path)));
        using var anchorAudit = JsonDocument.Parse(File.ReadAllText(TestRepository.Path("artifacts/v5-p6t-total-occurrence-role/p6tb-anchor-role-audit/anchor-role-audit.v1.json")));

        var g1Root = g1.RootElement;
        Assert.Equal("v5-p6tg1-function-conditioned-candidate-projection-v1", g1Root.GetProperty("schemaVersion").GetString());
        Assert.Equal(0, g1Root.GetProperty("execution").GetProperty("providerCalls").GetInt32());
        Assert.Equal("NONE", g1Root.GetProperty("execution").GetProperty("goldMutation").GetString());
        Assert.Equal("UNCHANGED", g1Root.GetProperty("execution").GetProperty("sharedRuntime").GetString());

        var p089 = Prepare("SRC-089", SourcePdfCorpus.Src089, f1Retry.RootElement.GetProperty("row"), g1Root.GetProperty("src089"));
        var p095 = Prepare("SRC-095", SourcePdfCorpus.Src095,
            f1Primary.RootElement.GetProperty("rows").EnumerateArray().Single(row => row.GetProperty("documentId").GetString() == "SRC-095"),
            g1Root.GetProperty("src095"));
        var tocAliases = anchorAudit.RootElement.GetProperty("src095").GetProperty("rows").EnumerateArray()
            .Select(row => row.GetProperty("occurrence").GetString()!)
            .Select(id => p095.F1Pack.Request.Occurrences.Single(item => item.Id == id).Atom.Alias)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(53, tocAliases.Count);
        Assert.All(tocAliases, alias => Assert.Equal("REPRESENTS_STRUCTURE", p095.FunctionByAlias[alias]));
        var tocOccurrenceIds = tocAliases.Select(alias => p095.OccurrenceByAlias[alias]).ToHashSet(StringComparer.Ordinal);

        var src089 = Compose(p089, new HashSet<string>(StringComparer.Ordinal));
        var src095 = Compose(p095, tocAliases);
        Assert.DoesNotContain(src095.OccurrenceGroups.SelectMany(group => group.CandidateIds), id =>
            p095.SourcePack.Universe.Candidates.Single(candidate => candidate.Id == id).Endpoint.Parts.Any(part => tocAliases.Contains(part.Alias)));
        Assert.DoesNotContain(tocOccurrenceIds, id => src095.UserMessage.Contains($"\"{id}\"", StringComparison.Ordinal));
        Assert.All(src089.OccurrenceGroups.Concat(src095.OccurrenceGroups), group => Assert.NotEmpty(group.CandidateIds));

        var gold089 = g1Root.GetProperty("src089").GetProperty("goldExtents").EnumerateArray().ToArray();
        Assert.Equal(6, gold089.Length);
        var goldProbeRows = gold089.Select(gold =>
        {
            var identity = gold.GetProperty("goldIdentity").GetString()!;
            var candidate = p089.SourcePack.Universe.Candidates.Single(value => value.SpanIdentity == identity);
            var group = src089.OccurrenceGroups.Single(value => value.PrimaryAlias == candidate.Endpoint.Parts[0].Alias);
            Assert.Contains(candidate.Id, group.CandidateIds);
            return new
            {
                primary = group.PrimaryOccurrence,
                primaryAlias = group.PrimaryAlias,
                exactCandidate = candidate.Id,
                exactCandidateKind = candidate.Kind.ToString(),
                eligibleOptionsAtPrimary = group.CandidateIds.Count,
                exactCandidateIncluded = true,
            };
        }).ToArray();

        var frontMatterRows = new[] { "L0002:S0", "L0002:S1", "L0003:S1" }.Select(alias =>
        {
            var group = src089.OccurrenceGroups.Single(value => value.PrimaryAlias == alias);
            var g1TouchedCount = g1Root.GetProperty("src089").GetProperty("falseEstablishAtoms").EnumerateArray()
                .Single(value => value.GetProperty("alias").GetString() == alias)
                .GetProperty("eligibleCandidateCountTouchingAtom").GetInt32();
            return new { primary = group.PrimaryOccurrence, primaryAlias = alias, noStructuralExtentIsAllowed = true,
                eligibleCandidatesSharingThisPrimary = group.CandidateIds.Count,
                g1EligibleCandidatesTouchingAtom = g1TouchedCount, candidateIds = group.CandidateIds };
        }).ToArray();
        var g1FrontMatterCandidateIds = g1Root.GetProperty("src089").GetProperty("frontMatterSurvivingCandidates").EnumerateArray()
            .Select(candidate => candidate.GetProperty("candidateId").GetString()!).ToHashSet(StringComparer.Ordinal);
        var g2FrontMatterCandidateIds = frontMatterRows.SelectMany(row => row.candidateIds).ToHashSet(StringComparer.Ordinal);
        Assert.True(g1FrontMatterCandidateIds.SetEquals(g2FrontMatterCandidateIds), "G1/G2 distinct front-matter candidate union mismatch");

        var reviewedToc = g1Root.GetProperty("src095").GetProperty("tocRows").EnumerateArray().ToArray();
        Assert.Equal(53, reviewedToc.Length);
        Assert.Equal(0, g1Root.GetProperty("answers").GetProperty("src095EligibleCandidateLeakageCount").GetInt32());

        FreezeArtifact.AssertJson(OutputRoot, "exact-extent-resolver-preflight.v1.json", new
        {
            schemaVersion = "v5-p6tg2-exact-extent-resolver-preflight-v1",
            status = "PREPARED_NOT_AUTHORIZED",
            protocolVersion = Protocol,
            treatment = new
            {
                model = "qwen/qwen3.7-flash",
                provider = "alibaba",
                reasoning = new { enabled = true, effort = "OMITTED" },
                promptAxis = "LOCAL_EXACT_EXTENT_SELECTION_OR_NO_STRUCTURAL_EXTENT",
                noPromptTuning = true,
                noGoldInModelInput = true,
            },
            outputContract = new
            {
                shape = "{\"decisions\":[{\"primary\":\"O27\",\"candidate\":\"C123\"},{\"primary\":\"O28\",\"candidate\":\"NO_STRUCTURAL_EXTENT\"}]}",
                oneDecisionPerIssuedPrimary = true,
                candidateMustBeIssuedForThatPrimary = true,
                noStructuralExtentAllowed = true,
                modelAuthoredTextOrCoordinates = false,
                exactIdentityResolution = "harness maps selected C# to frozen P6S endpoint identity",
                invalidDecisionPolicy = "decision-local quarantine; never repair or infer candidate",
            },
            inputInvariants = new
            {
                candidates = "ALL_ESTABLISHES only; alternatives share the same primary O#",
                sourceEvidence = "candidate exact text plus ordered source-part text and read-only function label",
                localContext = "at most nearest one preceding and nearest one following owned occurrence, read-only; representation-only occurrences omitted",
                contextOnlySelectable = false,
                tocReviewedAliasesInSrc095Input = 0,
                representationOnlyCandidatesInEitherInput = 0,
                relationsIncluded = false,
                goldExtentOrGoldLabelInModelInput = false,
            },
            sourceAuthority = new
            {
                g1ArtifactSha256 = Hashing.Sha256(File.ReadAllText(TestRepository.Path(G1Path))),
                src089CandidateUniverseFingerprint = p089.SourcePack.Universe.Fingerprint,
                src095CandidateUniverseFingerprint = p095.SourcePack.Universe.Fingerprint,
                src089FunctionRequestSha256 = p089.F1Pack.Request.UserMessageSha256,
                src095FunctionRequestSha256 = p095.F1Pack.Request.UserMessageSha256,
            },
            probes = new
            {
                src089ReviewedGoldExtents = goldProbeRows,
                src089FalseEstablishFrontMatter = frontMatterRows,
                src095ReviewedToc = new
                {
                    count = reviewedToc.Length,
                    appearsAsPrimary = reviewedToc.Count(row => src095.OccurrenceGroups.Any(group => group.PrimaryOccurrence == row.GetProperty("occurrence").GetString())),
                    selectableCandidateLeakage = 0,
                reviewedTocSourceIdentitiesAbsentFromSelectableCandidatesAndContext = true,
                },
            },
            callPlan = new[] { Describe(src089), Describe(src095) },
            budget = new
            {
                maximumAuthorizedProviderCalls = 0,
                providerCalls = 0,
                retries = 0,
                repairs = 0,
                fallbacks = 0,
                goldMutation = "NONE",
                runtimeChanged = false,
            },
            conclusion = "PROVIDER_FREE_G2_PREFLIGHT_FROZEN; EXACT_EXTENT_RESOLVER_NOT_EXECUTED; AWAIT_SEPARATE_PROVIDER_AUTHORIZATION",
        });
    }

    private static PreparedDocument Prepare(string documentId, string sourcePdf, JsonElement captureRow, JsonElement g1Document)
    {
        var sourceHash = CanonicalSemanticSourceHash.Compute(TestRepository.Path(sourcePdf));
        var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(
            TestRepository.Path($"{SnapshotRoot}/{sourceHash}.json"), documentId);
        var sourcePack = plan.Packs.Single(pack => pack.PackId.EndsWith("PACK_001", StringComparison.Ordinal));
        var f1Pack = PdfTotalOccurrenceRoleQualificationAdapter.PrepareFunctionMembershipF1(plan, sourcePack, Correspondences(sourcePack));
        Assert.Equal(f1Pack.Request.UserMessageSha256, captureRow.GetProperty("semanticRequestHash").GetString());
        Assert.Equal(f1Pack.ProviderRequestHash, captureRow.GetProperty("providerRequestHash").GetString());
        Assert.Equal("stop", captureRow.GetProperty("finishReason").GetString());
        Assert.True(captureRow.GetProperty("analysis").GetProperty("parserAccepted").GetBoolean());
        var raw = captureRow.GetProperty("rawResponse").GetString()!;
        Assert.Equal(captureRow.GetProperty("rawResponseSha256").GetString(), Hashing.Sha256(raw));
        var parsed = PdfTotalOccurrenceRoleQualificationAdapter.ParseFunctionMembershipF1(f1Pack, raw);
        Assert.Equal(96, parsed.Decisions.Count);
        var atomById = f1Pack.Request.Occurrences.ToDictionary(item => item.Id, item => item.Atom, StringComparer.Ordinal);
        var functionByAlias = parsed.Decisions.ToDictionary(item => atomById[item.OccurrenceId].Alias, item => item.Function.ToString(), StringComparer.Ordinal);
        var occurrenceByAlias = f1Pack.Request.Occurrences.ToDictionary(item => item.Atom.Alias, item => item.Id, StringComparer.Ordinal);
        var classes = Classify(sourcePack, functionByAlias);
        var persistedCounts = g1Document.GetProperty("classes");
        foreach (var value in Enum.GetValues<ProjectionClass>())
            Assert.Equal(classes.Values.Count(item => item == value), persistedCounts.GetProperty(value.ToString()).GetInt32());
        Assert.Equal(sourcePack.Universe.Fingerprint, g1Document.GetProperty("candidateUniverseFingerprint").GetString());
        Assert.Equal(sourcePack.Universe.Candidates.Count, g1Document.GetProperty("candidateCount").GetInt32());
        return new PreparedDocument(documentId, plan, sourcePack, f1Pack, functionByAlias, occurrenceByAlias, classes);
    }

    private sealed record Group(string PrimaryOccurrence, string PrimaryAlias, IReadOnlyList<string> CandidateIds);
    private sealed record PreparedRequest(string DocumentId, string SystemPrompt, string UserMessage, string MessageHash,
        int MessageBytes, int ProviderBytes, string ProviderHash, IReadOnlyList<Group> OccurrenceGroups);

    private static PreparedRequest Compose(PreparedDocument prepared, IReadOnlySet<string> forbiddenContextAliases)
    {
        var atoms = prepared.Plan.SourceAtoms.ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
        var ownedOrder = prepared.SourcePack.OwnedAliases;
        var candidatesByPrimary = prepared.SourcePack.Universe.Candidates
            .Where(candidate => prepared.CandidateClasses[candidate.Id] == ProjectionClass.ALL_ESTABLISHES)
            .GroupBy(candidate => candidate.Endpoint.Parts[0].Alias, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(candidate => int.Parse(candidate.Id[1..])).ToArray(), StringComparer.Ordinal);
        var roots = prepared.FunctionByAlias.Where(item => item.Value == "ESTABLISHES_STRUCTURE")
            .Select(item => item.Key).OrderBy(alias => atoms[alias].Ordinal).ThenBy(alias => alias, StringComparer.Ordinal).ToArray();
        var groups = roots.Select(alias =>
        {
            Assert.True(candidatesByPrimary.TryGetValue(alias, out var candidates) && candidates.Length > 0,
                $"establishes-primary-has-no-eligible-candidate:{prepared.DocumentId}:{alias}");
            return new Group(prepared.OccurrenceByAlias[alias], alias, candidates!.Select(candidate => candidate.Id).ToArray());
        }).ToArray();

        var tocAliasSet = forbiddenContextAliases;
        var rows = groups.Select(group =>
        {
            var rootIndex = Array.IndexOf(ownedOrder.ToArray(), group.PrimaryAlias);
            Assert.True(rootIndex >= 0, $"establishes-primary-not-owned:{group.PrimaryAlias}");
            var context = new List<object>();
            if (rootIndex > 0) AddContext(rootIndex - 1, "PREVIOUS");
            if (rootIndex + 1 < ownedOrder.Count) AddContext(rootIndex + 1, "NEXT");

            void AddContext(int index, string direction)
            {
                var alias = ownedOrder[index];
                var function = prepared.FunctionByAlias[alias];
                if (function == "REPRESENTS_STRUCTURE" || tocAliasSet.Contains(alias)) return;
                var atom = atoms[alias];
                context.Add(new { direction, page = atom.Page, text = atom.Text, function, selectable = false });
            }

            var options = group.CandidateIds.Select(id =>
            {
                var candidate = prepared.SourcePack.Universe.Candidates.Single(value => value.Id == id);
                Assert.All(candidate.Endpoint.Parts, part => Assert.Equal("ESTABLISHES_STRUCTURE", prepared.FunctionByAlias[part.Alias]));
                return new
                {
                    id = candidate.Id,
                    kind = candidate.Kind.ToString(),
                    text = candidate.Text,
                    parts = candidate.Endpoint.Parts.Select(part =>
                    {
                        var atom = atoms[part.Alias];
                        return new
                        {
                            occurrence = prepared.OccurrenceByAlias[part.Alias],
                            function = prepared.FunctionByAlias[part.Alias],
                            text = atom.Text.Substring(part.Start, part.End - part.Start),
                        };
                    }).ToArray(),
                };
            }).ToArray();
            return new
            {
                primary = group.PrimaryOccurrence,
                function = "ESTABLISHES_STRUCTURE",
                primaryText = atoms[group.PrimaryAlias].Text,
                candidates = options,
                context = context.ToArray(),
            };
        }).ToArray();

        var systemPrompt = $$"""
            Resolve only the exact source extent for each issued primary occurrence. The input contains a local menu of harness-issued candidate extents whose every source part is already classified ESTABLISHES_STRUCTURE. This function label is read-only evidence, not proof that a valid heading extent exists.

            For every supplied primary O#, return exactly one decision. Choose exactly one C# from that primary's own candidate menu, or return the exact sentinel NO_STRUCTURAL_EXTENT if none of the candidates is a valid structural heading extent. NO_STRUCTURAL_EXTENT is an explicit valid decision and must not be converted to OTHER or omitted.

            Return exactly one JSON object with this shape: {"decisions":[{"primary":"O27","candidate":"C123"},{"primary":"O28","candidate":"NO_STRUCTURAL_EXTENT"}]}. Each decision has exactly primary and candidate. Candidate IDs and source-part text are supplied by the harness; do not create, edit, join, trim, or retype extents. Do not output source text, coordinates, aliases, relations, hierarchy, rationale, confidence, or any extra property. Context items are read-only and never selectable. Do not use an external answer key.
            """;
        var userMessage = JsonSerializer.Serialize(new
        {
            protocolVersion = Protocol,
            occurrenceGroups = rows,
        });
        var request = new V5FreeHeadingRequestV1(Protocol, systemPrompt, userMessage, Hashing.Sha256(userMessage),
            Encoding.UTF8.GetByteCount(systemPrompt), Encoding.UTF8.GetByteCount(userMessage));
        var providerBody = PdfCandidateAuthorityQualificationAdapter.BuildProviderBodyReasoningEnabled(request,
            prepared.SourcePack.MaxCompletionTokens);
        return new PreparedRequest(prepared.DocumentId, systemPrompt, userMessage, request.UserMessageSha256,
            request.UserMessageUtf8Bytes, providerBody.Bytes, providerBody.Hash, groups);
    }

    private static object Describe(PreparedRequest request) => new
    {
        documentId = request.DocumentId,
        packId = "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001",
        status = "PREPARED_NOT_AUTHORIZED",
        primaryCount = request.OccurrenceGroups.Count,
        eligibleCandidateCount = request.OccurrenceGroups.Sum(group => group.CandidateIds.Count),
        optionsPerPrimary = request.OccurrenceGroups.Select(group => new
            { primary = group.PrimaryOccurrence, primaryAlias = group.PrimaryAlias, candidateCount = group.CandidateIds.Count, candidateIds = group.CandidateIds }).ToArray(),
        systemPromptSha256 = Hashing.Sha256(request.SystemPrompt),
        userMessageSha256 = request.MessageHash,
        userMessageUtf8Bytes = request.MessageBytes,
        providerBodySha256 = request.ProviderHash,
        providerBodyBytes = request.ProviderBytes,
    };

    private static IReadOnlyDictionary<string, ProjectionClass> Classify(PdfCandidateAuthorityPreparedPack pack, IReadOnlyDictionary<string, string> functionByAlias)
    {
        var output = new Dictionary<string, ProjectionClass>(StringComparer.Ordinal);
        foreach (var candidate in pack.Universe.Candidates)
        {
            var functions = candidate.Endpoint.Parts.Select(part => functionByAlias[part.Alias]).ToArray();
            var establishes = functions.Count(function => function == "ESTABLISHES_STRUCTURE");
            output.Add(candidate.Id, establishes == functions.Length ? ProjectionClass.ALL_ESTABLISHES
                : establishes > 0 ? ProjectionClass.MIXED : ProjectionClass.NO_ESTABLISHES);
        }
        return output;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>> Correspondences(PdfCandidateAuthorityPreparedPack pack)
    {
        var owned = pack.OwnedAliases.ToHashSet(StringComparer.Ordinal);
        var candidates = pack.Universe.Candidates.ToDictionary(value => value.Id, StringComparer.Ordinal);
        var result = new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal);
        foreach (var relation in pack.Universe.Relations)
        {
            if (!candidates.TryGetValue(relation.CandidateId, out var candidate) || !owned.Contains(candidate.Endpoint.Parts[0].Alias)) continue;
            var root = candidate.Endpoint.Parts[0].Alias;
            var values = result.TryGetValue(root, out var existing) ? existing.ToList() : [];
            if (!values.Any(value => value.TargetPage == relation.TargetPage && value.TargetText == relation.TargetText))
                values.Add(new V5ReadOnlyCorrespondenceV1(relation.TargetPage, relation.TargetText));
            result[root] = values;
        }
        return result;
    }
}
