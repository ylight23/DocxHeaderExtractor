using System.Numerics;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// P6A audits source-domain sharding as an execution partition, never as a semantic cap.  It
/// deliberately stops before a provider manifest because the current V3.3 wire cannot name a
/// harness-owned start/end domain.
/// </summary>
public sealed class V5P6ASourceDomainBoundedOccurrenceShardingTests
{
    private const string Root = "artifacts/v5-p6a-source-domain-sharding";
    private static readonly DocumentTaskContract Contract =
        DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
    private static readonly V5ProviderEnvelope Envelope =
        new("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300)
        { UsageInclude = true, OpenRouterResponseCacheDisabled = true };
    private static readonly (string DocumentId, string Pdf, int PackCount)[] Documents =
    [
        ("SRC-089", SourcePdfCorpus.Src089, 7),
        ("SRC-095", SourcePdfCorpus.Src095, 24),
    ];

    [Fact]
    public void Freeze_source_domain_sharding_authority_without_provider_or_gold()
    {
        var rows = new List<ShardGeometry>();
        foreach (var document in Documents)
        {
            var packs = V5PdfPreflightBuilder.BuildV3(TestRepository.Path(document.Pdf), document.DocumentId,
                Contract, V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, Envelope);
            Assert.Equal(document.PackCount, packs.Count);
            rows.AddRange(packs.Select((pack, index) => Geometry(document.DocumentId, index + 1, pack.Packet.SubjectEvidence)));
        }

        Assert.Equal(31, rows.Count);
        Assert.Equal(2884, rows.Sum(row => row.OwnedCount));
        Assert.All(rows, row => Assert.True(row.PrimarySpanChoices > BigInteger.Zero));
        Assert.All(rows, row => Assert.True(row.RootedMultipartIdentityUpperBound >= row.PrimarySpanChoices));

        var maximum = rows.OrderByDescending(row => row.RootedMultipartIdentityUpperBound)
            .ThenBy(row => row.DocumentId, StringComparer.Ordinal).ThenBy(row => row.ParentOrdinal).First();
        FreezeArtifact.AssertJson(Root, "source-domain-sharding.v1.json", new
        {
            schemaVersion = "v5-p6a-source-domain-bounded-occurrence-sharding-v1",
            providerCalls = 0, goldRead = false, goldMutation = "NONE",
            policy = new
            {
                id = "OCCURRENCE_SOURCE_DOMAIN_SHARDING_V1",
                owner = "owner(occurrence) = shard containing the primary (ownedIndex, start) coordinate.",
                coverage = "Every bindable occurrence has a non-empty ordered source-part tuple and therefore a primary part with one ownedIndex and one harness-resolved start coordinate.",
                nonOverlap = "A deterministic half-open coordinate partition assigns that primary coordinate to exactly one shard. Additional multipart parts never change ownership.",
                multipart = "PRESERVED: continuation parts remain part of the bound identity but do not select the owner shard.",
                semanticCardinality = "NOT_USED: no limit is inferred for headings, claims, or occurrences per owned atom.",
            },
            sourceGeometryMethod = new
            {
                primarySpanChoices = "For an atom of UTF-16 length n, n*(n+1)/2 is a conservative finite upper bound on selectable non-empty spans. It intentionally over-approximates text/occurrence encodings that the binder can actually resolve.",
                multipartIdentityUpperBound = "For each primary atom, multiply its span-choice count by every increasing continuation sequence of one through five later owned atoms, each weighted by its own span-choice count; sum over primaries. This is a source-coordinate upper bound under V3.3's six-part limit, not a semantic occurrence count.",
                maximumAdditionalParts = 5,
            },
            cohort = new
            {
                parentPacks = rows.Count,
                ownedOccurrences = rows.Sum(row => row.OwnedCount),
                largestSourceDomain = ToArtifact(maximum),
                rows = rows.Select(ToArtifact).ToArray(),
            },
            wireAuthority = new
            {
                currentV33Selection = "verbatimText plus optional occurrence is bound against source after parse; it contains no harness-issued start/end or occurrence-domain handle.",
                consequence = "A start-range shard can be checked only after resolving model text. The current schema cannot constrain model output to a coordinate domain before it emits arbitrary selections.",
                requiredForExecution = "A qualification-only response wire must carry a harness-issued source-domain/occurrence handle (or an equivalent finite enumerated coordinate vocabulary), then parser and binder must reject selections outside that handle's domain.",
            },
            volumeGate = new
            {
                finiteIdentitySpace = "YES_FOR_EACH_FROZEN_PACKET",
                finiteAndExecutableShardBound = "NOT_ESTABLISHED",
                reason = "A primary-start shard still contains multiple end spans and multipart continuation combinations. Recursive source partitioning is semantically safe, but without a wire-level coordinate-domain handle it cannot provide a provider-facing response-volume proof.",
                providerManifest = "NOT_PREPARED",
                providerExecution = "BLOCKED",
                sharedRuntime = "UNCHANGED",
            },
            conclusion = "P6A validates source-domain ownership as the correct non-semantic partition principle, but does not open execution: finiteness of the source identity space alone is insufficient until the response wire can represent and enforce the partition domain.",
        });
    }

    private static ShardGeometry Geometry(string documentId, int parentOrdinal, IReadOnlyList<EvidenceNode> owned)
    {
        var weights = owned.Select(node => SpanChoices(node.Text.Length)).ToArray();
        var suffix = new BigInteger[weights.Length + 1, 6];
        suffix[weights.Length, 0] = BigInteger.One;
        for (var index = weights.Length - 1; index >= 0; index--)
        {
            suffix[index, 0] = BigInteger.One;
            for (var parts = 1; parts <= 5; parts++)
                suffix[index, parts] = suffix[index + 1, parts] + weights[index] * suffix[index + 1, parts - 1];
        }

        BigInteger rooted = BigInteger.Zero;
        for (var index = 0; index < weights.Length; index++)
            for (var continuationCount = 0; continuationCount <= 5; continuationCount++)
                rooted += weights[index] * suffix[index + 1, continuationCount];

        var longest = owned.Select((node, index) => new { index, length = node.Text.Length })
            .OrderByDescending(item => item.length).ThenBy(item => item.index).First();
        return new ShardGeometry(documentId, parentOrdinal, owned.Count, weights.Aggregate(BigInteger.Zero, (sum, item) => sum + item),
            rooted, longest.index, longest.length, new BigInteger(longest.length));
    }

    private static BigInteger SpanChoices(int utf16Length) => utf16Length <= 0
        ? BigInteger.Zero
        : new BigInteger(utf16Length) * (utf16Length + 1) / 2;

    private static object ToArtifact(ShardGeometry row) => new
    {
        row.DocumentId,
        row.ParentOrdinal,
        row.OwnedCount,
        primarySpanChoices = row.PrimarySpanChoices.ToString(),
        rootedMultipartIdentityUpperBound = row.RootedMultipartIdentityUpperBound.ToString(),
        row.LongestOwnedIndex,
        row.LongestAtomUtf16Length,
        singleStartEndChoices = row.SingleStartEndChoices.ToString(),
    };

    private sealed record ShardGeometry(string DocumentId, int ParentOrdinal, int OwnedCount,
        BigInteger PrimarySpanChoices, BigInteger RootedMultipartIdentityUpperBound,
        int LongestOwnedIndex, int LongestAtomUtf16Length, BigInteger SingleStartEndChoices);
}
