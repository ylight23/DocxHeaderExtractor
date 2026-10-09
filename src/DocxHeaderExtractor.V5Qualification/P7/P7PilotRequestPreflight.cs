using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Semantics.Canonical;
using DocxHeaderExtractor.DocumentProcessing.Semantics.HeadingAuthority.Protocols;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.DocumentProcessing.Source.Common;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// Source-only preparation and control-upstream stage-isolated recipes. No Gold, review rows,
/// sidecars, provider execution or semantic override inputs. A recipe is not a frozen request
/// until its upstream raw ledger exists and the resulting bytes are separately frozen.
/// </summary>
internal static class P7PilotRequestPreflight
{
    public const string Version = "P7_PILOT_CONTROL_B_STAGE_ISOLATED_REQUEST_PREFLIGHT_V1";
    public const string UpstreamPolicy = "BOTH_ARMS_USE_SAME_FROZEN_CONTROL_UPSTREAM_NO_GOLD_FILTER";
    public const string PackSelection = "PRODUCTION_P05_PACKS_WITH_ANY_OWNED_ATOM_ON_APPROVED_PAGES_NO_CLIPPING";

    public static IReadOnlyList<SemanticEvidencePack> SelectPacks(DocumentSourceSnapshot source,
        PdfSourceDetails details, IReadOnlyList<int> approvedPages)
    {
        if (approvedPages.Count == 0 || approvedPages.Distinct().Count() != approvedPages.Count ||
            approvedPages.Any(p => !source.Atoms.Any(a => a.Page == p)))
            throw new InvalidOperationException("pilot-approved-pages-invalid");
        var byAlias = source.Atoms.ToDictionary(a => a.Alias, StringComparer.Ordinal);
        return SemanticEvidencePackingPolicies.PdfResourceBoundedP05.BuildPacks(source.Evidence, details.LayoutBlockByAtom)
            .Where(p => p.Owned.Any(a => approvedPages.Contains(byAlias[a.SourceAlias].Page))).ToArray();
    }

    public static InterpretationRequest F1(DocumentSourceSnapshot source, PdfSourceDetails details, SemanticEvidencePack pack)
    {
        var owned = Owned(source, pack);
        var aliases = owned.Select(a => a.Alias).ToHashSet(StringComparer.Ordinal);
        var byAlias = source.Atoms.ToDictionary(a => a.Alias, StringComparer.Ordinal);
        var context = pack.Visible.Where(a => !aliases.Contains(a.SourceAlias)).Select(a => (byAlias[a.SourceAlias].Page, byAlias[a.SourceAlias].Text)).ToArray();
        var correspondences = PdfReadOnlyCorrespondenceBuilder.Build(owned, source.Atoms);
        var request = PdfInterpretationProtocol.Compose(InterpretationStage.F1, source, details, owned,
            context: context, correspondences: correspondences);
        var control = OccurrenceFunctionProtocolV1.ComposeWithReadOnlyCorrespondences(owned, context, correspondences);
        if (request.ControlSystemPrompt != control.SystemPrompt || request.ControlUserMessage != control.UserMessage)
            throw new InvalidOperationException("pilot-production-f1-parity-drift");
        return request;
    }

    public static InterpretationRequest? G2A(DocumentSourceSnapshot source, PdfSourceDetails details,
        SemanticEvidencePack pack, string frozenControlF1, string finishReason)
    {
        var f1 = F1(source, details, pack);
        var functions = ParseF1(f1, frozenControlF1, finishReason);
        var primaries = functions.Decisions.Where(d => d.Function == OccurrenceFunction.EstablishesStructure).Select(d => d.OccurrenceId).ToArray();
        return primaries.Length == 0 ? null : PdfInterpretationProtocol.Compose(InterpretationStage.G2A,
            source, details, Owned(source, pack), primaryIds: primaries);
    }

    public static IReadOnlyList<InterpretationRequest> H2C(DocumentSourceSnapshot source, PdfSourceDetails details,
        SemanticEvidencePack pack, string frozenControlF1, string f1FinishReason,
        string frozenControlG2A, string g2aFinishReason)
    {
        var anchor = G2A(source, details, pack, frozenControlF1, f1FinishReason);
        if (anchor is null) throw new InvalidOperationException("pilot-g2a-not-issued");
        RequireStop(g2aFinishReason);
        if (Encoding.UTF8.GetByteCount(frozenControlG2A) > PdfInferenceWireContract.ResponseUtf8ByteCap)
            throw new InvalidOperationException("pilot-control-g2a-cap");
        var has = HeadingAnchorProtocolV1.Parse(frozenControlG2A, anchor.DecisionSubjects);
        return anchor.DecisionSubjects.Where(has.Contains).Select(id => PdfInterpretationProtocol.Compose(
            InterpretationStage.H2C, source, details, Owned(source, pack), anchor: id)).ToArray();
    }

    public static void ValidateProjection(InterpretationRequest request)
    {
        using var control = JsonDocument.Parse(request.ControlUserMessage);
        using var treatment = JsonDocument.Parse(request.UserMessage);
        if (!JsonElement.DeepEquals(treatment.RootElement.GetProperty("stageInput"), control.RootElement) ||
            !treatment.RootElement.GetProperty("decisionSubjects").EnumerateArray().Select(v => v.GetString()!).SequenceEqual(request.DecisionSubjects))
            throw new InvalidOperationException("pilot-treatment-stage-input-or-universe-drift");
        var projected = treatment.RootElement.GetProperty("sourceEvidence").EnumerateArray().ToArray();
        if (!projected.Select(v => v.GetProperty("occurrence").GetString()!).SequenceEqual(request.VisibleAliasByOccurrence.Keys))
            throw new InvalidOperationException("pilot-evidence-mapping-drift");
        var entries = request.EvidenceStore.Entries.ToDictionary(e => e.SourceAlias);
        foreach (var row in projected)
        {
            var id = row.GetProperty("occurrence").GetString()!;
            if (row.GetProperty("selectable").GetBoolean() != request.DecisionSubjects.Contains(id) ||
                !JsonElement.DeepEquals(row.GetProperty("source"), SpatialCanonical.Element(entries[request.VisibleAliasByOccurrence[id]])))
                throw new InvalidOperationException("pilot-neutral-evidence-or-selectability-drift");
        }
    }

    private static IReadOnlyList<SemanticSourceAtom> Owned(DocumentSourceSnapshot source, SemanticEvidencePack pack)
    {
        var atoms = source.Atoms.ToDictionary(a => a.Alias, StringComparer.Ordinal);
        return pack.Owned.Select(e => atoms[e.SourceAlias]).ToArray();
    }
    private static OccurrenceFunctionResult ParseF1(InterpretationRequest f1, string raw, string finishReason)
    {
        RequireStop(finishReason);
        using var json = JsonDocument.Parse(raw);
        return OccurrenceFunctionProtocolV1.Parse(json.RootElement, Encoding.UTF8.GetByteCount(raw),
            PdfInferenceWireContract.ResponseUtf8ByteCap, f1.Owned);
    }
    private static void RequireStop(string finishReason)
    {
        if (finishReason != "stop") throw new InvalidOperationException("pilot-complete-control-ledger-required");
    }
}
