using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using UglyToad.PdfPig;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The authority dimension a universe hash does not cover: what the model is actually shown.
/// <para>
/// A migration can leave every alias, its text and its order untouched while changing the facts
/// attached to each one, the context around it, or how it is cut into requests. A preflight that
/// checked only the universe would pass while the model read something else entirely. The previous
/// migration attempt was blocked on a related confusion, and the gap it exposed is what this file
/// pins: coordinates, evidence, packing and requests are four hashes, not one.
/// </para>
/// <para>
/// Shadow throughout. The active lane still builds its universe from parser blocks, canonical Gold
/// is untouched, and no request leaves the process.
/// </para>
/// </summary>
public sealed class PdfStructuredSourceAuthorityBuilderTests
{
    private const string Doc0252 = "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string Artifacts = "eval/a99-closed-loop/representation";
    private const int Atoms = 650;
    private const int ApprovedHeadings = 41;

    /// <summary>A 467-unit universe of blocks over segment lines. Never an atom universe.</summary>
    private const string BlockDiagnosticHash =
        "3b351411e1c773d14bf534f19c4ae721612716a4561396c757bc3ac35ed8b05c";

    /// <summary>The atom catalog serialized through the generic alias catalog, which renumbers.</summary>
    private const string CandidateSegmentHash =
        "86cad7d6b7a52391b4361ea1ad402774fa4bccf6af5570e3705f7582a6893c79";

    private const string Doc0252SourceSha256 =
        "a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61";

    /// <summary>
    /// The request-plan hash a hand-rolled measurement helper produced, before this file's own
    /// composer discovery showed it disagreed with production. Kept as a named predecessor, never
    /// reused as authority.
    /// </summary>
    private const string PredecessorSyntheticRequestPlanHash =
        "97952bd190c4ffffb77f30e12924efaf648c92d840228169a0c9adec74dd6d1d";

    // ---- every atom the model sees can be addressed ---------------------------------------------

    [Fact]
    public void Every_atom_has_both_a_coordinate_and_evidence()
    {
        // The defect that blocked the migration: candidate contexts were keyed by layout block id
        // while aliases were segments, so the lookup matched nothing and the model would have been
        // sent empty evidence with every hash still green.
        var plan = Plan();

        Assert.Equal(Atoms, plan.Atoms.Count);
        Assert.Equal(Atoms, plan.Evidence.Count);

        var catalog = plan.Atoms.Select(atom => atom.Alias).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(Atoms, catalog.Count);
        Assert.All(plan.Evidence, item => Assert.Contains(item.SourceAlias, catalog));
        Assert.All(plan.Evidence, item => Assert.False(string.IsNullOrWhiteSpace(item.ExactSourceText)));
        Assert.Empty(ProductionPacks(plan).Where(pack => pack.Owned.Count == 0));

        // Both keys reach the same atom, and they are one namespace rather than two: the evidence
        // is addressed by the alias, the context by the source id, and every atom has exactly one
        // of each. A layout block id is never either of them - that separation is what stops the
        // old coordinate authority from reappearing through a lookup that happens to succeed.
        var sourceIds = plan.Atoms.Select(atom => atom.SourceId).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(Atoms, sourceIds.Count);
        Assert.Equal(
            plan.Atoms.Select(atom => atom.SourceId),
            plan.Evidence.Select(item => item.SourceId));
        Assert.All(plan.LayoutBlockByAtom.Values, block =>
        {
            Assert.DoesNotContain(block, catalog);
            Assert.DoesNotContain(block, sourceIds);
        });
    }

    [Fact]
    public void Every_atom_carries_a_layout_block_label_and_keeps_its_own_alias()
    {
        var plan = Plan();

        Assert.All(plan.Atoms, atom => Assert.True(
            plan.LayoutBlockByAtom.ContainsKey(atom.SourceId),
            $"{atom.Alias} has no layout block to be shown with"));

        // Several atoms share a block, and none of them inherits its address.
        var shared = plan.Atoms
            .GroupBy(atom => plan.LayoutBlockByAtom[atom.SourceId], StringComparer.Ordinal)
            .First(group => group.Count() > 1)
            .ToArray();
        Assert.Equal(shared.Length, shared.Select(atom => atom.Alias).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_plan_is_deterministic()
    {
        var segments = Segments();
        var first = PdfStructuredSourceAuthorityBuilder.Build(segments);
        var second = PdfStructuredSourceAuthorityBuilder.Build(segments);

        Assert.Equal(first.SourceAliasUniverseHash, second.SourceAliasUniverseHash);
        Assert.Equal(first.ModelVisibleEvidenceHash, second.ModelVisibleEvidenceHash);
        Assert.Equal(ComposedRequests(first), ComposedRequests(second));
    }

    [Fact]
    public void The_atoms_conserve_the_text_of_the_segments_they_came_from()
    {
        var segments = Segments();
        var plan = PdfStructuredSourceAuthorityBuilder.Build(segments);

        Assert.Equal(
            segments.Select(line => line.Projection.VerbatimText),
            plan.Atoms.Select(atom => atom.Text));
        Assert.Equal(
            plan.Atoms.Select(atom => atom.Text),
            plan.Evidence.Select(item => item.ExactSourceText));
    }

    // ---- what a request would contain ------------------------------------------------------------

    [Fact]
    public void No_request_exposes_an_address_that_is_not_an_atom()
    {
        var plan = Plan();
        var catalog = plan.Atoms.Select(atom => atom.Alias).ToHashSet(StringComparer.Ordinal);
        var blockIds = plan.LayoutBlockByAtom.Values.ToHashSet(StringComparer.Ordinal);
        var requests = ComposedRequests(plan);
        var packs = ProductionPacks(plan);
        Assert.Equal(packs.Count, requests.Count);

        foreach (var pack in packs)
        {
            Assert.All(pack.Owned, item => Assert.Contains(item.SourceAlias, catalog));
            Assert.All(pack.Visible, item => Assert.Contains(item.SourceAlias, catalog));

            // A block id appearing where an alias belongs would put the old coordinate authority
            // back into the request without anything else changing.
            Assert.All(pack.Owned, item => Assert.DoesNotContain(item.SourceAlias, blockIds));
        }

        Assert.All(requests, request =>
        {
            Assert.Contains("\"ownedSourceAliases\"", request);
            Assert.Contains("sourceParts", request);
        });

        Assert.Equal(
            plan.Atoms.Select(atom => atom.Alias),
            packs.SelectMany(pack => pack.Owned).Select(item => item.SourceAlias));
    }

    [Fact]
    public void The_two_atoms_of_the_wrapped_heading_are_both_visible_and_still_bind()
    {
        // S0616, the case that could not be represented under block authority. Both atoms reach
        // the model, and the binder resolves them without a block being consulted anywhere.
        var plan = Plan();
        var first = plan.Atoms.Single(atom => atom.Alias == "L0359:S0");
        var second = plan.Atoms.Single(atom => atom.Alias == "L0360:S0");

        Assert.StartsWith("2. A Survey Based Approach", first.Text, StringComparison.Ordinal);
        Assert.Equal("Comparisons", second.Text);

        var requests = ComposedRequests(plan);
        var visible = ProductionPacks(plan)
            .Select((pack, index) => (pack, index))
            .Where(item => item.pack.Owned.Any(owned => owned.SourceAlias == first.Alias || owned.SourceAlias == second.Alias))
            .Select(item => requests[item.index])
            .ToArray();
        Assert.All([first, second], atom => Assert.Contains(visible,
            request => request.Contains($"\"alias\":\"{atom.Alias}\"", StringComparison.Ordinal)));

        var bound = SemanticSourcePartBinder.Bind(plan.Atoms, new SemanticSourcePartsProposal(
        [
            new SemanticSourcePart(first.Alias, CanonicalSemanticSelectionMode.WholeAlias),
            new SemanticSourcePart(second.Alias, CanonicalSemanticSelectionMode.WholeAlias),
        ]));

        Assert.True(bound.IsBound);
        Assert.Equal(SemanticSourceLocality.NextRowCompatible, bound.Parts[1].LocalityFromPrevious);
    }

    // ---- the frozen authority ---------------------------------------------------------------------

    // ---- helpers ----------------------------------------------------------------------------------

    private static string Doc0252Path => System.IO.Path.Combine(
        TestRepository.Root(), Doc0252.Replace('/', System.IO.Path.DirectorySeparatorChar));

    private static IReadOnlyList<PdfLine> Segments()
    {
        using var document = PdfDocument.Open(Doc0252Path);
        return PdfLineExtraction.ExtractLines(document);
    }

    private static PdfStructuredSourceAuthority Plan() =>
        PdfStructuredSourceAuthorityBuilder.Build(Segments(), sourceSha256: Doc0252SourceSha256);

    /// <summary>
    /// Every request the plan would actually produce, through the one real composer - the same
    /// path <see cref="CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel.InferAsync"/>
    /// sends to a classifier, reached here without one.
    /// </summary>
    /// <summary>The requests the production PDF lane composes for this plan.</summary>
    private static IReadOnlyList<string> ComposedRequests(PdfStructuredSourceAuthority plan)
    {
        var model = new CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel(
            new UnreachableClassifier(), SemanticCoordinateContract.PdfSemanticFunctionMembershipV1,
            SemanticEvidencePackingPolicies.PdfResourceBoundedP05, CanonicalSemanticPdfAuthorityAdapter.Request);
        return model.ComposeRequests(plan.CreateProductionInput("DOC-0252"))
            .Select(segment => segment.RequestBytes)
            .ToArray();
    }

    /// <summary>The partition those requests are sent under.</summary>
    private static IReadOnlyList<SemanticEvidencePack> ProductionPacks(PdfStructuredSourceAuthority plan) =>
        SemanticEvidencePackingPolicies.PdfResourceBoundedP05.BuildPacks(plan.Evidence, plan.LayoutBlockByAtom);

    /// <summary>A classifier that must never be called - proof that composing requests transports nothing.</summary>
    private sealed class UnreachableClassifier : IHeaderClassifier
    {
        public string ModelName => throw new InvalidOperationException("composing a request must not need a model name");
        public int ContextSize => throw new InvalidOperationException();
        public string RuntimeDescription => throw new InvalidOperationException();
        public int SharedPrefixTokens => throw new InvalidOperationException();
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0) =>
            throw new InvalidOperationException("PROVIDER_CALLS must remain 0: composing a request must never transport.");
        public void Dispose() { }
    }

    /// <summary>
    /// Captures the exact bytes InferAsync sends, without transporting them anywhere - the no-network
    /// proof that <see cref="CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel.InferAsync"/>
    /// and its own dry-run <c>ComposeRequests</c> genuinely produce the same bytes, not just similar
    /// ones.
    /// </summary>
    private sealed class RecordingClassifier : IHeaderClassifier
    {
        public List<string> Requests { get; } = [];
        public string ModelName => "recording";
        public int ContextSize => 8192;
        public string RuntimeDescription => "recording";
        public int SharedPrefixTokens => 0;
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0)
        {
            Requests.Add(userMessage);
            return Task.FromResult("{\"headings\":[]}");
        }
        public void Dispose() { }
    }

}
