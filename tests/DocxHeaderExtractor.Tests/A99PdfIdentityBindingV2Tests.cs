using System.Text.Json;
using DocxHeaderExtractor.Eval.Accuracy99;

namespace DocxHeaderExtractor.Tests;

public sealed class A99PdfIdentityBindingV2Tests
{
    private const string Doc0252Sha = "a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61";
    private const string Doc0133Sha = "08af1ba4ddb0adab18a838cb63a5daddf70d2dbd8f731b7b78ba1049bfc3ba12";

    [Fact]
    public void Canonicalizer_accepts_only_parser_backed_whitespace_fragmentation()
    {
        var recovered = PdfSourceTextCanonicalizer.Compare(
            "SESSION V: Current Research",
            "SESSION V: Cu rrent Research",
            "SESSION V: Current Research");

        Assert.True(recovered.Matches);
        Assert.Equal(IdentityGoldTextMatch.PdfLayoutCanonicalEquivalent, recovered.MatchKind);
        Assert.Equal("SESSION V: Current Research", recovered.CanonicalComparisonText);
        Assert.Contains("REJOIN_PDF_TEXT_RUN_FRAGMENTATION", recovered.NormalizationsApplied);

        var punctuationChanged = PdfSourceTextCanonicalizer.Compare(
            "SESSION V: Current Research",
            "SESSION V Current Research",
            "SESSION V Current Research");
        Assert.False(punctuationChanged.Matches);

        var missingToken = PdfSourceTextCanonicalizer.Compare(
            "SESSION V: Current Research",
            "SESSION V: Current",
            "SESSION V: Current");
        Assert.False(missingToken.Matches);
    }

    [Fact]
    public void Pdf_recovery_is_source_only_and_stable_across_repeated_extraction()
    {
        var path = RepoPath("todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf");
        var first = PdfRawOccurrenceRecovery.Recover(path, "SESSION V: Current Research");
        var second = PdfRawOccurrenceRecovery.Recover(path, "SESSION V: Current Research");

        var firstPage14 = Assert.Single(first, item => item.Page == 14);
        var secondPage14 = Assert.Single(second, item => item.Page == 14);
        Assert.Equal(firstPage14.StableOccurrenceId, secondPage14.StableOccurrenceId);
        Assert.Equal(firstPage14.TextSha256, secondPage14.TextSha256);
        Assert.Equal("SESSION V: Cu rrent Research", firstPage14.RawVerbatimText);
        Assert.Equal("SESSION V: Current Research", firstPage14.CanonicalComparisonText);
        Assert.Equal(IdentityGoldTextMatch.PdfLayoutCanonicalEquivalent,
            PdfSourceTextCanonicalizer.Compare("SESSION V: Current Research", firstPage14.RawVerbatimText,
                firstPage14.CanonicalComparisonText).MatchKind);
        Assert.NotNull(firstPage14.BoundingBox);
        Assert.True(firstPage14.BoundingBox!.Bottom < firstPage14.BoundingBox.Top);

        var continuation = Assert.Single(PdfRawOccurrenceRecovery.Recover(
            path, "SESSION V: Current Research (Cont’d)"));
        Assert.Equal(14, continuation.Page);
        Assert.Equal("SESSION V: Cu rrent Resea rch (Cont’d)", continuation.RawVerbatimText);
    }

    [Fact]
    public void Pdf_occurrence_recovery_does_not_bind_toc_suffix_as_exact_heading()
    {
        var path = RepoPath("todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/048_IBRD_Financial_Statements_March_2025.pdf");
        var report = PdfIdentityForensicBuilder.Build(path, ["INDEPENDENT AUDITOR'S REVIEW REPORT"]);
        var candidates = report.Candidates;

        Assert.Contains(candidates, item => item.Page == 31 &&
            item.MatchClassification == "DIAGNOSTIC_CANONICAL_CONTAINS_ONLY");
        var exact = Assert.Single(candidates, item => item.Page == 66 &&
            item.MatchClassification == "VERBATIM_EXACT");
        Assert.Equal("INDEPENDENT AUDITOR'S REVIEW REPORT", exact.RawText);
        Assert.NotEqual("DIAGNOSTIC_CANONICAL_CONTAINS_ONLY", exact.MatchClassification);
    }

    [Fact]
    public void Binder_uses_existing_pdf_identity_and_canonical_text_without_relation_input()
    {
        var path = RepoPath("todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf");
        var left = Assert.Single(PdfRawOccurrenceRecovery.Recover(path, "SESSION V: Current Research"), item => item.Page == 14);
        var right = Assert.Single(PdfRawOccurrenceRecovery.Recover(path, "SESSION V: Current Research (Cont’d)"));
        var source = new[]
        {
            Source("B000888", left),
            Source("B000893", right),
        };

        var result = IdentityGoldBinderV1.BindPair(
            new("DOC-0252", Doc0252Sha, "SESSION V: Current Research", SourceOccurrenceId: "B000888"),
            new("DOC-0252", Doc0252Sha, "SESSION V: Current Research (Cont’d)", SourceOccurrenceId: "B000893"),
            "DOC-0252", Doc0252Sha, source);

        Assert.Equal(IdentityGoldBindingStatus.ExactBound, result.Status);
        Assert.Equal("B000888", result.Left.Occurrence!.SourceOccurrenceId);
        Assert.Equal("B000893", result.Right.Occurrence!.SourceOccurrenceId);
        Assert.Equal(IdentityGoldTextMatch.PdfLayoutCanonicalEquivalent, result.Left.Occurrence.TextMatch);
        Assert.Equal(IdentityGoldTextMatch.PdfLayoutCanonicalEquivalent, result.Right.Occurrence.TextMatch);
        Assert.Equal("NATIVE_SOURCE_ID_AND_PDF_LAYOUT_CANONICAL_TEXT", result.Left.Occurrence.BindingMethod);
        Assert.Equal("NATIVE_SOURCE_ID_AND_PDF_LAYOUT_CANONICAL_TEXT", result.Right.Occurrence.BindingMethod);
        Assert.Null(result.Left.Occurrence.Utf16Start);
        Assert.False(JsonSerializer.Serialize(result).Contains("CONTINUATION", StringComparison.Ordinal));
    }

    [Fact]
    public void DOC0133_recovery_keeps_composite_toc_line_unbound_and_recovers_inner_exact_line()
    {
        var path = RepoPath("todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/048_IBRD_Financial_Statements_March_2025.pdf");
        var recovered = PdfRawOccurrenceRecovery.Recover(path, "INDEPENDENT AUDITOR'S REVIEW REPORT");
        var exact = Assert.Single(recovered);
        Assert.Equal(66, exact.Page);
        Assert.Equal("INDEPENDENT AUDITOR'S REVIEW REPORT", exact.RawVerbatimText);

        var result = IdentityGoldBinderV1.Bind(
            new("DOC-0133", Doc0133Sha, "INDEPENDENT AUDITOR'S REVIEW REPORT", SourceOccurrenceId: "B002349"),
            "DOC-0133", Doc0133Sha,
            [new IdentityGoldSourceOccurrence(
                "DOC-0133", Doc0133Sha, "B002349", null, exact.RawVerbatimText, exact.Page,
                2348, "b2349", null, null, null, exact.CanonicalComparisonText,
                exact.NormalizationsApplied)]);

        Assert.Equal(IdentityGoldBindingStatus.ExactBound, result.Status);
        Assert.Equal("B002349", result.Occurrence!.SourceOccurrenceId);
        Assert.Equal(66, result.Occurrence.Page);
    }

    [Fact]
    public void Frozen_v2_artifact_keeps_IR018_blocked_and_IR019_machine_evaluable()
    {
        using var artifact = JsonDocument.Parse(File.ReadAllText(RepoPath(
            "artifacts/identity-gold/semantic-identity-bindings.user-reviewed.v2.json")));
        var root = artifact.RootElement;

        Assert.Equal("BLOCKED_ON_IDENTITY_GOLD_BINDING", root.GetProperty("status").GetString());
        Assert.Equal(0, root.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, root.GetProperty("modelCalls").GetInt32());
        Assert.Equal(4, root.GetProperty("summary").GetProperty("exactBound").GetInt32());
        Assert.Equal(1, root.GetProperty("summary").GetProperty("bindingIncomplete").GetInt32());
        Assert.Equal(4, root.GetProperty("summary").GetProperty("machineEvaluable").GetInt32());
        var items = root.GetProperty("items");
        Assert.Equal("TEXT_NOT_FOUND", items.EnumerateArray().Single(item => item.GetProperty("id").GetString() == "IR-018").GetProperty("bindingStatus").GetString());
        var ir019 = items.EnumerateArray().Single(item => item.GetProperty("id").GetString() == "IR-019");
        Assert.Equal("EXACT_BOUND", ir019.GetProperty("bindingStatus").GetString());
        Assert.True(ir019.GetProperty("machineEvaluable").GetBoolean());
        Assert.Equal("PDF_LAYOUT_CANONICAL_EQUIVALENT", ir019.GetProperty("leftOccurrence").GetProperty("textMatch").GetString());
        Assert.Equal("PDF_LAYOUT_CANONICAL_EQUIVALENT", ir019.GetProperty("rightOccurrence").GetProperty("textMatch").GetString());
        Assert.Equal("semantic-identity-bindings.user-reviewed.v1.json",
            Path.GetFileName(root.GetProperty("previousBindingArtifact").GetProperty("path").GetString()));
    }

    private static IdentityGoldSourceOccurrence Source(string id, PdfRecoveredOccurrence occurrence) =>
        new("DOC-0252", Doc0252Sha, id, null, occurrence.RawVerbatimText, occurrence.Page,
            occurrence.ReadingOrder, id, null, null, occurrence.BoundingBox is null ? null : JsonSerializer.Serialize(occurrence.BoundingBox),
            occurrence.CanonicalComparisonText, occurrence.NormalizationsApplied);

    private static string RepoPath(string relative) => Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..")), relative);
}
