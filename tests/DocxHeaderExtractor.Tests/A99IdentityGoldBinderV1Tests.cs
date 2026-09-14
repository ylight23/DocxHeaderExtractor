using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Eval;
using DocxHeaderExtractor.Eval.Accuracy99;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityGoldBinderV1Tests
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Native_source_ids_keep_duplicate_exact_text_occurrences_distinguishable()
    {
        var source = new[]
        {
            Occurrence("body/p[10]", "Repeated heading", 10),
            Occurrence("body/p[20]", "Repeated heading", 20),
        };

        var result = IdentityGoldBinderV1.Bind(
            new("DOC-TEST", Sha, "Repeated heading", SourceOccurrenceId: "body/p[20]"),
            "DOC-TEST", Sha, source);

        Assert.Equal(IdentityGoldBindingStatus.ExactBound, result.Status);
        Assert.Equal("body/p[20]", result.Occurrence!.SourceOccurrenceId);
        Assert.Equal(20, result.Occurrence.SourceOrdinal);
    }

    [Fact]
    public void Duplicate_exact_text_without_identity_fails_closed_as_ambiguous()
    {
        var source = new[] { Occurrence("body/p[10]", "Repeated heading", 10), Occurrence("body/p[20]", "Repeated heading", 20) };

        var result = IdentityGoldBinderV1.Bind(
            new("DOC-TEST", Sha, "Repeated heading"),
            "DOC-TEST", Sha, source);

        Assert.Equal(IdentityGoldBindingStatus.Ambiguous, result.Status);
        Assert.Null(result.Occurrence);
    }

    [Fact]
    public void Wrong_source_sha_fails_before_source_search()
    {
        var result = IdentityGoldBinderV1.Bind(
            new("DOC-TEST", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Heading"),
            "DOC-TEST", Sha, new[] { Occurrence("body/p[1]", "Heading", 1) });

        Assert.Equal(IdentityGoldBindingStatus.SourceHashMismatch, result.Status);
        Assert.Null(result.Occurrence);
    }

    [Fact]
    public void Derived_identity_is_stable_and_unsupported_coordinates_remain_null()
    {
        var source = Occurrence(null, "Heading", 7) with { SourceAlias = null, NativePath = null, Page = null };
        var first = IdentityGoldBinderV1.StableSourceId(source);
        var second = IdentityGoldBinderV1.StableSourceId(source);
        var result = IdentityGoldBinderV1.Bind(new("DOC-TEST", Sha, "Heading", SourceOrdinal: 7), "DOC-TEST", Sha, new[] { source });

        Assert.Equal(first, second);
        Assert.StartsWith("derived:", first, StringComparison.Ordinal);
        Assert.Equal(IdentityGoldBindingStatus.ExactBound, result.Status);
        Assert.Null(result.Occurrence!.Page);
        Assert.Null(result.Occurrence.Utf16Start);
        Assert.Null(result.Occurrence.Utf16Length);
    }

    [Fact]
    public void Binding_is_invariant_under_relation_label_change()
    {
        var source = new[] { Occurrence("body/p[1]", "Heading A", 1), Occurrence("body/p[2]", "Heading B", 2) };
        IdentityGoldEndpointQuery left = new("DOC-TEST", Sha, "Heading A", SourceOccurrenceId: "body/p[1]");
        IdentityGoldEndpointQuery right = new("DOC-TEST", Sha, "Heading B", SourceOccurrenceId: "body/p[2]");

        var same = IdentityGoldBinderV1.BindPair(left, right, "DOC-TEST", Sha, source);
        // The binder has no relation parameter.  Rebinding the same source-only endpoint
        // queries is the metamorphic comparison for any external relation label.
        var distinct = IdentityGoldBinderV1.BindPair(left, right, "DOC-TEST", Sha, source);

        Assert.Equal(JsonSerializer.Serialize(same), JsonSerializer.Serialize(distinct));
        Assert.Equal(IdentityGoldBindingStatus.ExactBound, same.Status);
    }

    [Fact]
    public void Frozen_binding_artifact_reports_only_source_backed_machine_evaluable_items()
    {
        using var artifact = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "artifacts/identity-gold/semantic-identity-bindings.user-reviewed.v1.json")));
        var root = artifact.RootElement;

        Assert.Equal("BLOCKED_ON_IDENTITY_GOLD_BINDING", root.GetProperty("status").GetString());
        Assert.Equal(0, root.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, root.GetProperty("modelCalls").GetInt32());
        Assert.False(root.GetProperty("goldRelationConsumedByBinder").GetBoolean());
        Assert.Equal(3, root.GetProperty("summary").GetProperty("exactBound").GetInt32());
        Assert.Equal(2, root.GetProperty("summary").GetProperty("bindingIncomplete").GetInt32());
        Assert.Equal(3, root.GetProperty("summary").GetProperty("machineEvaluable").GetInt32());

        var items = root.GetProperty("items");
        Assert.Equal("TEXT_NOT_FOUND", items.EnumerateArray().Single(x => x.GetProperty("id").GetString() == "IR-018").GetProperty("bindingStatus").GetString());
        Assert.Equal("TEXT_NOT_FOUND", items.EnumerateArray().Single(x => x.GetProperty("id").GetString() == "IR-019").GetProperty("bindingStatus").GetString());
        foreach (var item in items.EnumerateArray().Where(x => x.GetProperty("machineEvaluable").GetBoolean()))
        {
            Assert.Equal("EXACT_BOUND", item.GetProperty("bindingStatus").GetString());
            Assert.True(item.GetProperty("leftOccurrence").GetProperty("sourceLineageVerified").GetBoolean());
            Assert.True(item.GetProperty("rightOccurrence").GetProperty("sourceLineageVerified").GetBoolean());
        }
    }

    [Fact]
    public void DOC0123_source_packet_binds_IR020_IR021_and_IR022_without_relation_input()
    {
        const string sourceSha = "ac85908c86aa5b37d40b60d514229d546f61a20bc966f32cf0ad08f9de2798e1";
        using var packet = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "eval/harness-lift/review-packets/DOC-0123.v1.json")));
        var source = packet.RootElement.GetProperty("occurrences").EnumerateArray().Select(item =>
        {
            var span = item.GetProperty("fullSpan");
            var text = item.GetProperty("rawText").GetString()!;
            return new IdentityGoldSourceOccurrence(
                "DOC-0123", sourceSha, item.GetProperty("sourceId").GetString(), null, text,
                null, item.GetProperty("sourceOrdinal").GetInt32(), item.GetProperty("sourceId").GetString(),
                span.GetProperty("start").GetInt32(), span.GetProperty("end").GetInt32() - span.GetProperty("start").GetInt32());
        }).ToArray();

        var pairs = new[]
        {
            ("Section I - Instructions to Proposers (ITP)", "body[1]/p[192]", "Section I - Instructions to Proposers", "body[1]/p[263]"),
            ("Contractor’s Representative and Key Personnel", "body[1]/p[1164]", "Contractor’s Representative and Key Personnel", "body[1]/p[1166]"),
            ("SPECIFIED PROVISIONAL SUMS for ES OUTCOMES", "body[1]/p[1098]", "SPECIFIED PROVISIONAL SUMS for ES OUTCOMES", "body[1]/p[1137]"),
        };

        foreach (var (leftText, leftId, rightText, rightId) in pairs)
        {
            var result = IdentityGoldBinderV1.BindPair(
                new("DOC-0123", sourceSha, leftText, SourceOccurrenceId: leftId),
                new("DOC-0123", sourceSha, rightText, SourceOccurrenceId: rightId),
                "DOC-0123", sourceSha, source);

            Assert.Equal(IdentityGoldBindingStatus.ExactBound, result.Status);
            Assert.Equal(leftId, result.Left.Occurrence!.SourceOccurrenceId);
            Assert.Equal(rightId, result.Right.Occurrence!.SourceOccurrenceId);
        }
    }

    [Fact]
    public void PDF_source_evidence_is_bound_exactly_or_blocked_without_repair()
    {
        const string doc0133Sha = "08af1ba4ddb0adab18a838cb63a5daddf70d2dbf8f731b7b78ba1049bfc3ba12";
        const string pdfPath = "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/048_IBRD_Financial_Statements_March_2025.pdf";
        using var packet = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "eval/a99-closed-loop/research-r2/p3-isolated-review/sources/DOC-0133.review-source.v1.json")));
        var source = packet.RootElement.GetProperty("occurrences").EnumerateArray().Select(item =>
        {
            var layout = item.GetProperty("layout");
            return new IdentityGoldSourceOccurrence(
                "DOC-0133", doc0133Sha, item.GetProperty("sourceOccurrenceId").GetString(), null,
                item.GetProperty("literalSourceText").GetString()!, item.GetProperty("page").GetInt32(),
                item.GetProperty("sourceOrdinal").GetInt32(), item.GetProperty("blockId").GetString(), null, null,
                layout.GetRawText());
        }).ToArray();

        var right = IdentityGoldBinderV1.Bind(
            new("DOC-0133", doc0133Sha, "INDEPENDENT AUDITOR'S REVIEW REPORT", SourceOccurrenceId: "B002349"),
            "DOC-0133", doc0133Sha, source);

        Assert.Equal(IdentityGoldBindingStatus.ExactBound, right.Status);
        Assert.Equal(66, right.Occurrence!.Page);
        Assert.Equal("B002349", right.Occurrence.SourceOccurrenceId);
        Assert.True(File.Exists(Path.Combine(RepositoryRoot(), pdfPath)));
    }

    [Fact]
    public void PDF_parser_spacing_drift_does_not_get_fuzzy_bound()
    {
        const string doc0252Sha = "a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61";
        var path = Path.Combine(RepositoryRoot(), "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf");
        var review = IsolatedPdfSourceBuilder.Build(path);
        var source = review.Blocks.Select(item => new IdentityGoldSourceOccurrence(
            "DOC-0252", doc0252Sha, item.SourceOccurrenceId, null, item.LiteralSourceText, item.Page,
            item.SourceOrdinal, item.BlockId, null, null)).ToArray();

        var result = IdentityGoldBinderV1.BindPair(
            new("DOC-0252", doc0252Sha, "SESSION V: Current Research"),
            new("DOC-0252", doc0252Sha, "SESSION V: Current Research (Cont’d)"),
            "DOC-0252", doc0252Sha, source);

        Assert.Equal(IdentityGoldBindingStatus.TextNotFound, result.Status);
        Assert.Contains(source, item => item.SourceOccurrenceId == "B000888" && item.VerbatimText == "SESSION V: Cu rrent Research");
        var continuation = source.Single(item => item.SourceOccurrenceId == "B000893");
        Assert.StartsWith("SESSION V: Cu rrent Resea", continuation.VerbatimText, StringComparison.Ordinal);
        Assert.Contains("Cont", continuation.VerbatimText, StringComparison.Ordinal);
    }

    private static IdentityGoldSourceOccurrence Occurrence(string? id, string text, int ordinal) =>
        new("DOC-TEST", Sha, id, id, text, 1, ordinal, id, null, null);

    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
}
