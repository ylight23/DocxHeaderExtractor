using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Materialization;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Semantics.HeadingAuthority.Protocols;
using DocxHeaderExtractor.DocumentProcessing.Source.Common;

namespace DocxHeaderExtractor.DocumentProcessing.Semantics.HeadingAuthority;

/// <summary>
/// The qualified heading authority promoted from the F1 → G2A → H2-C V2 chain: occurrence function,
/// then anchor existence, then the exact heading extent. Each stage is strict and fail-closed: a
/// rejected function or anchor pack emits no headings and a rejected extent withholds only its
/// anchor. It has no semantic fallback. Request bytes are composed by the protocol classes and sent
/// verbatim through the frozen transport.
/// </summary>
/// <param name="transport">The qualified frozen transport the route authorized.</param>
/// <param name="layoutBlockByAtom">Parser layout labels used only to size request packs.</param>
/// <param name="ensureActive">Throws when the lane no longer owns the execution.</param>
internal sealed class FunctionAnchorExtentHeadingAuthority(
    IFrozenInferenceTransport transport,
    IReadOnlyDictionary<string, string> layoutBlockByAtom,
    Action ensureActive) : IHeadingAuthority
{
    internal const string AuthorityId = "pdf-function-conditioned-heading-authority-v1";
    private const int ResponseCap = PdfQualifiedInferencePolicy.ResponseUtf8ByteCap;
    // Qualification serializes with the framework default encoder. Do not use the relaxed encoder
    // here: escaping is part of the provider-body identity.
    private const int P05CompletionTokens = PdfQualifiedInferencePolicy.CompletionTokenCeiling;

    public async Task<HeadingAuthorityResult> DecideAsync(DocumentSourceSnapshot source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        var atoms = source.Atoms.ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
        var decisions = new List<HeadingExtentDecision>();
        var raw = new List<string>();
        foreach (var pack in SemanticEvidencePackingPolicies.PdfResourceBoundedP05.BuildPacks(source.Evidence, layoutBlockByAtom))
        {
            ct.ThrowIfCancellationRequested();
            ensureActive();
            var ownedAliases = pack.Owned.Select(item => item.SourceAlias).ToArray();
            var owned = ownedAliases.Select(alias => atoms[alias]).ToArray();
            var visible = pack.Visible.Select(item => item.SourceAlias).ToArray();
            var context = visible.Where(alias => !ownedAliases.Contains(alias, StringComparer.Ordinal)).Select(alias => (atoms[alias].Page, atoms[alias].Text)).ToArray();
            var f1Request = OccurrenceFunctionProtocolV1.ComposeWithReadOnlyCorrespondences(owned, context, Correspondences(owned, source.Atoms));
            var f1 = await ExecuteAsync(f1Request.SystemPrompt, f1Request.UserMessage, ct).ConfigureAwait(false);
            if (f1 is null) continue;
            raw.Add(f1.Content);
            OccurrenceFunctionResult functions;
            try { using var json = JsonDocument.Parse(f1.Content); functions = OccurrenceFunctionProtocolV1.Parse(json.RootElement, Encoding.UTF8.GetByteCount(f1.Content), ResponseCap, f1Request.Occurrences); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { continue; }
            var byId = f1Request.Occurrences.ToDictionary(value => value.Id, StringComparer.Ordinal);
            var establishes = functions.Decisions.Where(value => value.Function == OccurrenceFunction.EstablishesStructure).Select(value => byId[value.OccurrenceId]).ToArray();
            if (establishes.Length == 0) continue;

            var idByAlias = f1Request.Occurrences.ToDictionary(value => value.Atom.Alias, value => value.Id, StringComparer.Ordinal);
            var anchorUser = HeadingAnchorProtocolV1.ComposeUserMessage(owned, idByAlias, establishes.Select(value => (value.Id, value.Atom)).ToArray());
            var anchorBody = QualifiedInferenceRequestFactory.Build(HeadingAnchorProtocolV1.SystemPrompt, anchorUser, P05CompletionTokens);
            var anchors = await ExecuteAsync(HeadingAnchorProtocolV1.SystemPrompt, anchorUser, ct, anchorBody).ConfigureAwait(false);
            if (anchors is null) continue;
            raw.Add(anchors.Content);
            HashSet<string> has;
            try { has = HeadingAnchorProtocolV1.Parse(anchors.Content, establishes.Select(value => value.Id)); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { continue; }

            foreach (var anchor in establishes.Where(value => has.Contains(value.Id)))
            {
                var start = Array.IndexOf(ownedAliases, anchor.Atom.Alias);
                if (start < 0) continue;
                var tail = ownedAliases.Skip(start).ToArray(); // terminal anchors intentionally remain issued.
                var evidenceByAlias = source.Evidence.ToDictionary(item => item.SourceAlias, StringComparer.Ordinal);
                var user = HeadingExtentProtocolV2.ComposeUserMessage(anchor.Id, tail, idByAlias, atoms, evidenceByAlias);
                var body = QualifiedInferenceRequestFactory.Build(HeadingExtentProtocolV2.SystemPrompt, user, P05CompletionTokens);
                var extent = await ExecuteAsync(HeadingExtentProtocolV2.SystemPrompt, user, ct, body).ConfigureAwait(false);
                if (extent is null) continue;
                raw.Add(extent.Content);
                try { decisions.Add(HeadingExtentProtocolV2.Bind(extent.Content, anchor.Id, tail, idByAlias, atoms)); }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
            }
        }

        var bound = decisions.Select(decision => ToBound(decision, atoms)).ToArray();
        var atomCount = source.Atoms.Count;

        RouteExecutionAudit Complete(RouteExecutionAudit audit, HeadingStructureAssembly assembly) => audit with
        {
            RawAnalystResponses = raw,
            ModelInputContracts = ["v5-total-occurrence-function-f1", "v5-function-conditioned-anchor-existence-1", "v5-function-conditioned-exact-end-pointer-clean-paired-1"],
            ValidatedStructures = assembly.Placements.Values.ToArray(),
            SemanticLane = new RouteLaneExecutionAudit("complete", atomCount, decisions.Count, 0, 0),
            SpanLane = new RouteLaneExecutionAudit("exact-end-pointer", decisions.Count, assembly.Validated.Count, 0, decisions.Count - assembly.Validated.Count),
        };
        return new HeadingAuthorityResult(decisions, bound, transport, Complete);
    }

    private static CanonicalSemanticBoundHeading ToBound(HeadingExtentDecision decision, IReadOnlyDictionary<string, SemanticSourceAtom> atoms)
    {
        var primary = atoms.Values.Single(atom => atom.SourceId == decision.Id);
        var parts = decision.Parts ?? [];
        return new CanonicalSemanticBoundHeading(primary.Alias, primary.SourceId, primary.Ordinal, string.Join(" ", parts.Select(value => value.Text)), "ESTABLISHES_STRUCTURE", "heading", "document_body", [], 0, primary.Text.Length) { Parts = parts };
    }

    private async Task<FrozenInferenceResult?> ExecuteAsync(string prompt, string user, CancellationToken ct, byte[]? body = null)
    {
        body ??= QualifiedInferenceRequestFactory.Build(prompt, user, P05CompletionTokens);
        var result = await transport.ExecuteFrozenRequestAsync(body, P05CompletionTokens, prompt, user, ct).ConfigureAwait(false);
        return string.Equals(result.FinishReason, "stop", StringComparison.OrdinalIgnoreCase) && Encoding.UTF8.GetByteCount(result.Content) <= ResponseCap ? result : null;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>> Correspondences(
        IReadOnlyList<SemanticSourceAtom> owned, IReadOnlyList<SemanticSourceAtom> all) =>
        PdfReadOnlyCorrespondenceBuilder.Build(owned, all);
}
