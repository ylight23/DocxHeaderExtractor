using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;
using DocxHeaderExtractor.Core.Eval;
using Xunit;

namespace DocxHeaderExtractor.Tests;

public sealed class FinalLegacyBoundaryTests
{
    [Fact]
    public void Generic_structure_has_no_heading_only_compatibility_view()
    {
        Assert.Null(typeof(ValidatedStructure).GetProperty("Headings"));
    }

    [Fact]
    public void Historical_pdf_legacy_policy_isolated_from_core_runtime_assembly()
    {
        Assert.Equal("DocxHeaderExtractor.Eval", typeof(PdfLegacyValidatedOutputPolicy).Assembly.GetName().Name);
    }

    [Fact]
    public void Domain_policy_exposes_evidence_without_obsolete_authority_methods()
    {
        var methods = typeof(DocumentDomainPolicy).GetMethods(
                System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic)
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("HierarchyTier", methods);
        Assert.DoesNotContain("IsExcludedFromOutline", methods);
        Assert.DoesNotContain("IsConventionalOutlineRole", methods);
        Assert.NotNull(typeof(DocumentDomainPolicy).GetMethod("EvidenceForRole"));
    }
}
