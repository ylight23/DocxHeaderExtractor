using System.Security.Cryptography;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class P7PortableReviewBundleTests
{
    private static byte[] Receipt() => File.ReadAllBytes(TestRepository.Path("artifacts/web-pdf-semantic-diagnostic/p7.d2.3.portable-review-bundle.v1.json"));

    [Fact] public void Portable_zip_receipt_is_hash_frozen_without_committing_source_documents()
    {
        Assert.Equal("d1a14e6d367ff397ba896eca6777c7c75682907042a0275a64c9f583a86abbe8", Convert.ToHexStringLower(SHA256.HashData(Receipt())));
        using var doc = JsonDocument.Parse(Receipt());
        Assert.Equal("e130cc7cd36a8e49cd20f09a013229e893d18c9dda32372a65bdebbdf37048dc", doc.RootElement.GetProperty("zipSha256").GetString());
    }

    [Fact] public void Zip_contains_full_originals_and_review_scope_but_context_does_not_expand_evaluation()
    {
        using var doc = JsonDocument.Parse(Receipt()); var r = doc.RootElement;
        Assert.Equal(5, r.GetProperty("originalPdfCopies").GetInt32()); Assert.Equal(6, r.GetProperty("selectedPageImages").GetInt32());
        Assert.Equal(213, r.GetProperty("pendingOccurrences").GetInt32()); Assert.Equal(10858, r.GetProperty("contextReferenceOccurrences").GetInt32());
        Assert.False(r.GetProperty("evaluationScopeAutomaticallyExpanded").GetBoolean());
        Assert.True(r.GetProperty("relativeHtmlLinksVerified").GetBoolean()); Assert.True(r.GetProperty("zipEntryHashesVerified").GetBoolean());
    }

    [Fact] public void Delivery_is_not_adjudication_or_exact_evaluability_claim()
    {
        using var doc = JsonDocument.Parse(Receipt()); var r = doc.RootElement;
        Assert.Equal(JsonValueKind.Null, r.GetProperty("exactEvaluabilityRate").ValueKind);
        Assert.Equal("PENDING", r.GetProperty("goldAdjudication").GetString()); Assert.Equal("OPEN", r.GetProperty("d23").GetString());
        Assert.Equal("LOCKED", r.GetProperty("providerExecution").GetString()); Assert.Equal("LOCKED", r.GetProperty("productionPromotion").GetString());
        Assert.Equal(0, r.GetProperty("providerCalls").GetInt32()); Assert.False(r.GetProperty("sourcePdfsModified").GetBoolean());
        Assert.False(r.GetProperty("productionChanged").GetBoolean());
    }
}
