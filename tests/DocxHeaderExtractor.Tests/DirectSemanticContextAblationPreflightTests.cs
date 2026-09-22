using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Offline authority for a future three-arm context ablation of the frozen direct semantic probe.
/// No provider result is created here. The only semantic variable in the future run is the source
/// context window policy.
/// </summary>
public sealed class DirectSemanticContextAblationPreflightTests
{
    private const string PreflightRoot =
        "eval/a99-closed-loop/direct-semantic-context-ablation-preflight-v1";
    private const string FutureCaptureRoot =
        "eval/a99-closed-loop/direct-semantic-context-ablation-v1/DOC-0252";
    private const string Doc0252Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string SourceSha256 =
        "a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61";
    private const string SourceUniverseSha256 =
        "2a953bf785ed1af00bc908ff9e5d6a1d988b04c0d980ecd95336bc5a9702f46f";
    private const string GoldSha256 =
        "e0001e940bc71c78d0dc2c8df44434f49421ff97679f1f968b192e98a05dd66e";
    private const string PromptSha256 =
        "5e8d393c7e78af28a9695011e63cb4bad00505467532bdcf14582e551aa2a387";
    private const string SchemaSha256 =
        "20f937f19ef560380f9ec7f76ad43fa8dec4442dafdd9c404eb6c258e14295b9";
    private const string Model = "qwen/qwen3.7-flash";
    private const int Repeats = 3;
    private const int LocalRadius = 3;
    private const int StructuralNeighborCount = 2;
    private const int MaxOutputTokens = 32768;

    private static readonly string[] PackNames = ["PACK_001", "PACK_005", "PACK_006"];

    [Fact]
    public void Freeze_context_ablation_preflight_without_provider_calls()
    {
        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Doc0252Pdf));
        var build = DirectSemanticProbePreflightTests.Build(plan);

        Assert.Equal(SourceSha256, CanonicalArtifactHash.OfBytes(TestRepository.Path(Doc0252Pdf)));
        Assert.Equal(SourceUniverseSha256, plan.SourceUniverseSha256);
        Assert.Equal(GoldSha256, CanonicalGoldRegistry.Entry("DOC-0252").GoldSha256);
        Assert.Equal(PromptSha256, CanonicalArtifactHash.OfText(
            DirectSemanticProbeRetryPreflightTests.RetryProbePrompt));
        Assert.Equal(SchemaSha256, HashSchema(build.Items.Select(item => item.ItemId).ToArray()));

        var fullPacks = DirectSemanticProbePreflightTests.ComposeAllPacks(plan);
        var itemsByPack = build.Items
            .GroupBy(item => item.PackId.Split(':')[1], StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.OrderBy(item => item.Identity, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);

        var arms = new[]
        {
            BuildArm(ContextWindowPolicy.Full, plan, fullPacks, itemsByPack),
            BuildArm(ContextWindowPolicy.Local, plan, fullPacks, itemsByPack),
            BuildArm(ContextWindowPolicy.MinimalStructural, plan, fullPacks, itemsByPack),
        };

        Assert.Equal(3, arms.Length);
        Assert.All(arms, arm => Assert.Equal(9, arm.Slots.Length));
        Assert.All(arms, arm => Assert.Equal(9, arm.Slots.Select(cell => cell.Identity).Distinct(StringComparer.Ordinal).Count()));
        Assert.All(arms, arm => Assert.True(arm.TransportCompatible));

        var full = arms.Single(arm => arm.Policy == ContextWindowPolicy.Full);
        Assert.Equal("22d3bbe551900fd0e47f0144054e66c16ef12b03f030540c34b9856886450d37",
            full.ProviderInputHashes["PACK_001"]);
        Assert.Equal("8ce58a9c478f481f6d021f83b2e849953f1d5b1e378186405d7dd4d5fb555866",
            full.ProviderInputHashes["PACK_005"]);
        Assert.Equal("127573c38acfbcaca27dd713a9054cd627a02ac8e3469c0c538f9e1f6b3e86fd",
            full.ProviderInputHashes["PACK_006"]);
        Assert.Equal("a792fb0f03b2c6c16facecba5485ed48dc7742610f7542e0936ca88ecd59243b",
            full.PlanHash);

        // Every arm carries the same classification items and differs only in documentEvidence.
        Assert.All(arms, arm => Assert.Equal(build.Items.Select(item => item.ItemId), arm.ItemIds));
        Assert.Equal(arms[0].ItemOwnership, arms[1].ItemOwnership);
        Assert.Equal(arms[0].ItemOwnership, arms[2].ItemOwnership);
        Assert.All(arms.Skip(1), arm => Assert.Equal(full.NonContextRequestHash, arm.NonContextRequestHash));
        Assert.Equal(3, arms.Select(arm => arm.PolicyHash).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(arms.Skip(1), arm => arm.ContextHashes.Any(pair =>
            pair.Value != full.ContextHashes[pair.Key]));

        var futureSlots = BuildFutureSlots(arms);
        Assert.Equal(27, futureSlots.Count);
        Assert.Equal(27, futureSlots.Select(slot => slot.Path).Distinct(StringComparer.Ordinal).Count());
        Assert.All(futureSlots, slot => Assert.False(File.Exists(TestRepository.Path(slot.Path))));
        Assert.True(ProbeAtomicReservation(futureSlots.Count));

        FreezeArtifact.AssertJson(PreflightRoot, "direct-semantic-context-ablation-preflight.v1.json", new
        {
            artifactKind = "a99_direct_semantic_context_ablation_preflight",
            schemaVersion = "a99-direct-semantic-context-ablation-preflight-v1",
            documentId = "DOC-0252",
            providerAuthorized = false,
            modelCalls = 0,
            providerCalls = 0,
            firstModel = new
            {
                model = Model,
                classification = "DIRECT_SEMANTIC_DISCRIMINATION_LIMIT_OBSERVED",
                mechanism = "DOCUMENT_LABEL_ATTRACTOR",
                f1 = "0/3 DOCUMENT_LABEL",
                agenda = "0/3 DOCUMENT_LABEL",
                f2 = "3/3",
                f3 = "3/3",
                structuralControls = "39/42; 13/14 each repeat",
                documentLabelControl = "3/3",
            },
            authority = new
            {
                sourceSha256 = SourceSha256,
                sourceUniverseSha256 = SourceUniverseSha256,
                goldSha256 = GoldSha256,
                model = Model,
                promptSha256 = PromptSha256,
                schemaSha256 = SchemaSha256,
                labels = new[] { "STRUCTURAL_UNIT", "DOCUMENT_LABEL", "NON_STRUCTURAL" },
                expectedLabelsProviderVisible = false,
                itemIdentityMappingIdentical = true,
                itemOwnershipMappingIdentical = true,
            },
            arms = arms.Select(arm => new
            {
                id = arm.PolicyId,
                policyHash = arm.PolicyHash,
                policy = arm.PolicyDescription,
                sourceDerived = true,
                goldUsedForContext = false,
                packContextHashes = arm.ContextHashes,
                providerInputHashes = arm.ProviderInputHashes,
                providerInputPlanHash = arm.PlanHash,
                nonContextRequestHash = arm.NonContextRequestHash,
                expectedItemCounts = arm.ExpectedItemCounts,
                maxTokens = arm.MaxTokens,
                transport = new
                {
                    model = Model,
                    temperature = 0,
                    reasoning = "none",
                    responseFormat = "json_object",
                    maxOutputTokens = MaxOutputTokens,
                    route = "OpenRouter",
                },
                transportCompatible = arm.TransportCompatible,
                repeats = Repeats,
                proposedCalls = arm.Slots.Length,
                captures = arm.Slots,
            }).ToArray(),
            differenceAudit = new
            {
                onlyContextChanged = true,
                schemaBytesIdentical = true,
                semanticPromptBytesIdentical = true,
                itemIdsIdentical = true,
                expectedLabelsIdenticalInScorer = true,
                modelIdentical = true,
                transportSettingsIdentical = true,
                providerVisibleDelta = "context evidence only",
            },
            capture = new
            {
                futureRoot = FutureCaptureRoot,
                identitiesRequired = 27,
                identitiesFresh = true,
                identitiesDistinct = true,
                identitiesAtomicallyReservable = true,
                permanentReservationsCreated = false,
                slots = futureSlots,
            },
            callPlan = new
            {
                fullContext = 9,
                localContext = 9,
                minimalStructuralContext = 9,
                totalProposedProviderCalls = 27,
                placementCalls = 0,
                repeats = Repeats,
            },
            primaryComparison = new
            {
                items = new[] { "F1", "Agenda" },
                controls = new[] { "F2", "F3", "14 structural controls", "document-label control" },
                scoring = "F1/Agenda correct per 3; controls per 3; structural 42 per arm; confusion matrix per arm",
            },
            interpretation = new
            {
                contextContribution = "If narrower arms fix both F1 and Agenda while controls remain strong",
                contextBoundary = "If all arms retain both DOCUMENT_LABEL errors while controls remain strong",
                tradeoff = "If narrowing fixes one primary target and degrades the other",
                controlFailure = "If narrowing materially damages structural or document-label controls",
                crossGenreGeneralizationEstablished = false,
                materializedGold = "48/3955; one document",
            },
            status = "CONTEXT_ABLATION_AUTHORIZATION_READY",
        });
    }

    [Fact]
    public void Context_policies_are_coordinate_only_and_do_not_accept_gold_labels()
    {
        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Doc0252Pdf));
        var build = DirectSemanticProbePreflightTests.Build(plan);
        var fullPacks = DirectSemanticProbePreflightTests.ComposeAllPacks(plan);
        var itemsByPack = build.Items
            .GroupBy(item => item.PackId.Split(':')[1], StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.OrderBy(item => item.Identity, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);

        // The context selector accepts only source coordinates. ExpectedLabel and GoldRole never
        // enter its signature, so changing either cannot alter the selected evidence.
        foreach (var policy in new[] { ContextWindowPolicy.Local, ContextWindowPolicy.MinimalStructural })
        {
            var normal = BuildArm(policy, plan, fullPacks, itemsByPack);
            var reversedTargets = itemsByPack.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Reverse().ToArray(),
                StringComparer.Ordinal);
            var reversed = BuildArm(policy, plan, fullPacks, reversedTargets);
            Assert.Equal(normal.ContextHashes, reversed.ContextHashes);
        }
    }

    private static ArmResult BuildArm(
        ContextWindowPolicy policy,
        PdfStructuredSourceAuthority plan,
        IReadOnlyDictionary<string, string> fullPacks,
        IReadOnlyDictionary<string, DirectSemanticProbePreflightTests.ProbeItem[]> itemsByPack)
    {
        var contextHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var providerHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var expectedCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var maxTokens = new Dictionary<string, int>(StringComparer.Ordinal);
        var requests = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pack in PackNames)
        {
            var fullPackRequest = fullPacks[$"COHERENT_REGION_SEGMENTATION_V1:{pack}"];
            var schemaMarker = fullPackRequest.LastIndexOf("\nSCHEMA=", StringComparison.Ordinal);
            Assert.True(schemaMarker > 0);
            var fullEvidence = JsonDocument.Parse(fullPackRequest[..schemaMarker]);
            var fullRoot = fullEvidence.RootElement;
            var items = itemsByPack[pack];
            var context = policy == ContextWindowPolicy.Full
                ? JsonNode.Parse(fullRoot.GetRawText())!
                : BuildReducedContext(policy, plan, fullRoot, items.Select(item => item.Identity));
            var contextJson = context.ToJsonString(new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            var request = ComposeRequest(contextJson, items);
            requests[pack] = request;
            contextHashes[pack] = Sha256CanonicalJson(context);
            providerHashes[pack] = SemanticAuthorityTransportCall.Sha256Utf8(
                JsonSerializer.Serialize(new
                {
                    systemPrompt = DirectSemanticProbeRetryPreflightTests.RetryProbePrompt,
                    userMessage = request,
                }));
            expectedCounts[pack] = fullRoot.GetProperty("ownedSourceAliases").GetArrayLength();
            maxTokens[pack] = OpenRouterHeaderExtractor.BoundaryOutputBudgetFor(
                request, expectedCounts[pack], MaxOutputTokens);
            Assert.True(maxTokens[pack] > 256);
            Assert.True(TransportCompatibility.Validate(
                DirectSemanticProbeRetryPreflightTests.RetryProbePrompt, request,
                TransportCompatibility.JsonObjectResponseFormat).IsCompatible);
            fullEvidence.Dispose();
        }

        var inputPlanHash = SemanticAuthorityTransportCall.Sha256Utf8(
            string.Join("\0", PackNames.Select(pack => providerHashes[pack])));
        var nonContextHash = SemanticAuthorityTransportCall.Sha256Utf8(
            string.Join("\0", PackNames.Select(pack => NonContextProjectionHash(requests[pack]))));
        var itemIds = itemsByPack.SelectMany(pair => pair.Value)
            .OrderBy(item => item.Identity, StringComparer.Ordinal)
            .Select(item => item.ItemId).ToArray();
        var itemOwnership = itemsByPack.SelectMany(pair => pair.Value.Select(item =>
                new KeyValuePair<string, string>(item.ItemId, item.PackId)))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var policyHash = HashObject(new
        {
            id = PolicyId(policy),
            localRadius = policy == ContextWindowPolicy.Local ? LocalRadius : (int?)null,
            structuralNeighborCount = policy == ContextWindowPolicy.MinimalStructural
                ? StructuralNeighborCount : (int?)null,
            inputs = new[] { "source atom coordinates", "source evidence order", "existing structural markers" },
        });
        var slots = Enumerable.Range(1, Repeats)
            .SelectMany(repeat => PackNames.Select(pack => new CellSlot($"r{repeat}/{pack}", repeat, pack)))
            .ToArray();

        return new ArmResult(
            policy,
            PolicyId(policy),
            PolicyDescription(policy),
            policyHash,
            contextHashes,
            providerHashes,
            inputPlanHash,
            nonContextHash,
            expectedCounts,
            maxTokens,
            itemIds,
            itemOwnership,
            requests,
            slots,
            true);
    }

    private static JsonNode BuildReducedContext(
        ContextWindowPolicy policy,
        PdfStructuredSourceAuthority plan,
        JsonElement fullRoot,
        IEnumerable<string> targetIdentities)
    {
        var entries = fullRoot.GetProperty("sourceEvidence").EnumerateArray().ToArray();
        var entryByAlias = entries.ToDictionary(
            entry => entry.GetProperty("alias").GetString()!, StringComparer.Ordinal);
        var atomIndex = plan.Atoms.Select((atom, index) => (atom.Alias, index))
            .ToDictionary(pair => pair.Alias, pair => pair.index, StringComparer.Ordinal);
        var selected = new HashSet<int>();
        var targetAliases = targetIdentities.SelectMany(Aliases).Distinct(StringComparer.Ordinal).ToArray();

        foreach (var alias in targetAliases)
        {
            Assert.True(atomIndex.TryGetValue(alias, out var index));
            if (policy == ContextWindowPolicy.Local)
            {
                for (var cursor = Math.Max(0, index - LocalRadius);
                     cursor <= Math.Min(plan.Atoms.Count - 1, index + LocalRadius); cursor++)
                    selected.Add(cursor);
            }
            else
            {
                selected.Add(index);
                AddRange(selected, index - 1, index + 1, plan.Atoms.Count);
                var candidates = entries
                    .Select(entry =>
                    {
                        var aliasValue = entry.GetProperty("alias").GetString()!;
                        return (entry, aliasValue, index: atomIndex.GetValueOrDefault(aliasValue, -1));
                    })
                    .Where(candidate => candidate.index >= 0 && IsStructuralEvidence(candidate.entry))
                    .OrderBy(candidate => candidate.index)
                    .ToArray();
                foreach (var before in candidates.Where(candidate => candidate.index < index)
                             .OrderByDescending(candidate => candidate.index).Take(StructuralNeighborCount))
                    selected.Add(before.index);
                foreach (var after in candidates.Where(candidate => candidate.index > index)
                             .OrderBy(candidate => candidate.index).Take(StructuralNeighborCount))
                    selected.Add(after.index);
            }
        }

        var sourceEvidence = new JsonArray();
        foreach (var entry in entries)
        {
            var alias = entry.GetProperty("alias").GetString()!;
            if (atomIndex.TryGetValue(alias, out var index) && selected.Contains(index))
                sourceEvidence.Add(JsonNode.Parse(entry.GetRawText()));
        }

        foreach (var alias in targetAliases)
            Assert.True(entryByAlias.ContainsKey(alias), $"Target alias missing from pack context: {alias}");

        var owned = new JsonArray();
        foreach (var alias in fullRoot.GetProperty("ownedSourceAliases").EnumerateArray())
            owned.Add(alias.GetString());
        return new JsonObject
        {
            ["protocol"] = fullRoot.GetProperty("protocol").GetString(),
            ["ownedSourceAliases"] = owned,
            ["sourceEvidence"] = sourceEvidence,
        };
    }

    private static bool IsStructuralEvidence(JsonElement entry)
    {
        if (!entry.TryGetProperty("scope", out var scope) ||
            !string.Equals(scope.GetString(), "document_body", StringComparison.Ordinal))
            return false;
        if (!entry.TryGetProperty("markers", out var markers) || markers.ValueKind != JsonValueKind.Array)
            return false;
        return markers.EnumerateArray().Any(marker =>
            marker.GetString()?.StartsWith("marker-family:", StringComparison.Ordinal) == true);
    }

    private static void AddRange(HashSet<int> selected, int start, int end, int count)
    {
        for (var index = Math.Max(0, start); index <= Math.Min(count - 1, end); index++)
            selected.Add(index);
    }

    private static string ComposeRequest(
        string contextJson,
        IReadOnlyList<DirectSemanticProbePreflightTests.ProbeItem> items)
    {
        var request = JsonSerializer.Serialize(new
        {
            probe = "direct-semantic-classification",
            documentEvidence = JsonSerializer.Deserialize<JsonElement>(contextJson),
            itemsToClassify = items.Select(item => new { item.ItemId, sourceText = item.Text }),
        });
        return request + "\nSCHEMA=" + JsonSerializer.Serialize(
            DirectSemanticProbePreflightTests.ProbeSchema(items.Select(item => item.ItemId).ToArray()));
    }

    private static string NonContextProjectionHash(string request)
    {
        var marker = request.LastIndexOf("\nSCHEMA=", StringComparison.Ordinal);
        using var user = JsonDocument.Parse(request[..marker]);
        var projection = new JsonObject();
        foreach (var property in user.RootElement.EnumerateObject())
        {
            if (property.NameEquals("documentEvidence")) continue;
            projection[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }
        return SemanticAuthorityTransportCall.Sha256Utf8(
            projection.ToJsonString(new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }) + request[marker..]);
    }

    private static List<CellSlot> BuildFutureSlots(IReadOnlyList<ArmResult> arms) =>
        arms.SelectMany(arm => arm.Slots.Select(slot => new CellSlot(
            $"{arm.PolicyId}/{slot.Identity}", slot.Repeat, slot.Pack))).ToList();

    private static bool ProbeAtomicReservation(int count)
    {
        var root = Path.Combine(Path.GetTempPath(), "a99-context-ablation-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            for (var index = 0; index < count; index++)
            {
                var path = Path.Combine(root, $"slot-{index:000}.reserve");
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Flush(true);
            }
            return Directory.GetFiles(root).Length == count;
        }
        finally
        {
            foreach (var path in Directory.EnumerateFiles(root)) File.Delete(path);
            Directory.Delete(root);
        }
    }

    private static string PolicyId(ContextWindowPolicy policy) => policy switch
    {
        ContextWindowPolicy.Full => "FULL_CONTEXT",
        ContextWindowPolicy.Local => "LOCAL_CONTEXT_RADIUS_3",
        ContextWindowPolicy.MinimalStructural => "MINIMAL_STRUCTURAL_CONTEXT_V1",
        _ => throw new ArgumentOutOfRangeException(nameof(policy)),
    };

    private static string PolicyDescription(ContextWindowPolicy policy) => policy switch
    {
        ContextWindowPolicy.Full => "The exact production direct-probe context composition, rebuilt under this lineage.",
        ContextWindowPolicy.Local => "For every frozen item, target aliases plus three preceding and three following source atoms; overlapping windows unioned in source order.",
        ContextWindowPolicy.MinimalStructural => "For every frozen item, target aliases plus immediate source neighbors and up to two nearest preceding/following document-body evidence entries carrying existing structural marker families.",
        _ => throw new ArgumentOutOfRangeException(nameof(policy)),
    };

    private static IEnumerable<string> Aliases(string identity) => identity.Split('|')
        .Select(part => part[..part.LastIndexOf(':')]);

    private static string HashSchema(string[] itemIds)
    {
        var schema = DirectSemanticProbePreflightTests.ProbeSchema(itemIds);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(schema, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }).ReplaceLineEndings("\n"))));
    }

    private static string HashObject(object value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(value, FreezeArtifact.Json).ReplaceLineEndings("\n"))));

    private static string Sha256CanonicalJson(JsonNode value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            value.ToJsonString(FreezeArtifact.Json).ReplaceLineEndings("\n"))));

    private enum ContextWindowPolicy
    {
        Full,
        Local,
        MinimalStructural,
    }

    private sealed record ArmResult(
        ContextWindowPolicy Policy,
        string PolicyId,
        string PolicyDescription,
        string PolicyHash,
        Dictionary<string, string> ContextHashes,
        Dictionary<string, string> ProviderInputHashes,
        string PlanHash,
        string NonContextRequestHash,
        Dictionary<string, int> ExpectedItemCounts,
        Dictionary<string, int> MaxTokens,
        string[] ItemIds,
        Dictionary<string, string> ItemOwnership,
        Dictionary<string, string> Requests,
        CellSlot[] Slots,
        bool TransportCompatible);

    private sealed record CellSlot(string Identity, int Repeat, string Pack)
    {
        public string Path =>
            $"{FutureCaptureRoot}/{Identity.Split('/')[0].ToLowerInvariant()}/r{Repeat}/{Pack}.capture-slot.v1.json";
    }
}
