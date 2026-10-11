using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

/// <summary>Issue #8 (P7_F1_REGION_HEADING_V5): contract tests. Offline only; the validator never decides semantics.</summary>
public sealed class P7F1RegionV5Tests
{
    static readonly P7F1DirectProtocol.Subject[] Subjects =
    [
        new("O1", "L0001:S0", 1, "1. Introduction"),
        new("O2", "L0002:S0", 1, "Body paragraph."),
        new("O3", "L0003:S0", 2, "Form No. 05"),
    ];
    static readonly Dictionary<string, string> IdByAlias = new(StringComparer.Ordinal)
    { ["L0001:S0"] = "kqzt", ["L0002:S0"] = "mbxr", ["L0003:S0"] = "hpdv" };
    static readonly HashSet<string> Citable = ["L0001:S0", "L0002:S0", "L0003:S0"];

    static F1QValidation V(string json) => P7F1RegionProtocolV5.Validate(json, "D001-C1", Subjects, IdByAlias, Citable);

    const string Head = "\"protocolVersion\":\"P7_F1_REGION_HEADING_V5\",\"requestId\":\"D001-C1\",\"assessment\":\"COMPLETE\"";

    [Fact] public void Prompt_asks_for_regions_then_headings_and_keeps_the_V1_evidence_text()
    {
        var p = P7F1RegionProtocolV5.SystemPrompt();
        Assert.Contains("STEP 1 - REGIONS", p);
        Assert.Contains("STEP 2 - HEADINGS", p);
        var v1 = P7F1DirectProtocol.SystemPrompt();
        Assert.Contains(v1[v1.IndexOf("EVIDENCE.", StringComparison.Ordinal)..v1.IndexOf("OUTPUT.", StringComparison.Ordinal)].Trim('\n'), p);
        foreach (var banned in new[] { "ESTABLISHES_STRUCTURE", "REPRESENTS_STRUCTURE", "semanticKind", "O7" }) Assert.DoesNotContain(banned, p);
        Assert.DoesNotContain('\r', p);
    }

    [Fact] public void A_valid_region_then_heading_is_accepted_and_rows_carry_the_region_function()
    {
        var v = V($"{{{Head},\"regions\":[{{\"from\":\"kqzt\",\"to\":\"mbxr\",\"function\":\"opening title and body\",\"evidence\":[\"kqzt\"]}}],\"headings\":[{{\"id\":\"kqzt\",\"region\":0,\"evidenceRefs\":[\"kqzt\"],\"justification\":\"title\"}}]}}");
        Assert.True(v.StrictAccepted, v.FailureCode);
        var row = Assert.Single(v.Rows.Where(r => r.Valid));
        Assert.Equal("O1", row.Occurrence);
        Assert.Equal("opening title and body", row.Decision!.Function);
    }

    [Fact] public void A_heading_inside_a_region_described_as_navigation_is_not_filtered_by_C_sharp()
    {
        var v = V($"{{{Head},\"regions\":[{{\"from\":\"kqzt\",\"to\":\"mbxr\",\"function\":\"table of contents entries\",\"evidence\":[]}}],\"headings\":[{{\"id\":\"kqzt\",\"region\":0,\"evidenceRefs\":[\"kqzt\"],\"justification\":\"opens content here\"}}]}}");
        Assert.True(v.StrictAccepted, v.FailureCode);
        Assert.Single(v.Rows.Where(r => r.Valid));
    }

    [Fact] public void Headings_must_name_a_region_index_that_exists()
    {
        var v = V($"{{{Head},\"regions\":[{{\"from\":\"kqzt\",\"to\":\"mbxr\",\"function\":\"body\",\"evidence\":[]}}],\"headings\":[{{\"id\":\"kqzt\",\"region\":3,\"evidenceRefs\":[\"kqzt\"],\"justification\":\"x\"}}]}}");
        Assert.False(v.StrictAccepted);
        Assert.Equal("HEADING_REGION_INVALID", v.FailureCode);
    }

    [Fact] public void A_heading_outside_the_region_it_cites_is_rejected()
    {
        var v = V($"{{{Head},\"regions\":[{{\"from\":\"kqzt\",\"to\":\"kqzt\",\"function\":\"title\",\"evidence\":[]}}],\"headings\":[{{\"id\":\"hpdv\",\"region\":0,\"evidenceRefs\":[\"hpdv\"],\"justification\":\"x\"}}]}}");
        Assert.Equal("HEADING_OUTSIDE_ITS_REGION", v.FailureCode);
    }

    [Fact] public void Regions_must_be_in_reading_order_and_not_overlap()
    {
        var reversed = V($"{{{Head},\"regions\":[{{\"from\":\"mbxr\",\"to\":\"kqzt\",\"function\":\"a\",\"evidence\":[]}}],\"headings\":[]}}");
        Assert.Equal("REGION_FROM_AFTER_TO", reversed.FailureCode);
        var overlap = V($"{{{Head},\"regions\":[{{\"from\":\"kqzt\",\"to\":\"mbxr\",\"function\":\"a\",\"evidence\":[]}},{{\"from\":\"mbxr\",\"to\":\"hpdv\",\"function\":\"b\",\"evidence\":[]}}],\"headings\":[]}}");
        Assert.Equal("REGIONS_NOT_IN_READING_ORDER_OR_OVERLAP", overlap.FailureCode);
    }

    [Fact] public void Region_ids_must_be_issued_and_functions_must_be_non_empty()
    {
        Assert.Equal("REGION_ID_NOT_ISSUED", V($"{{{Head},\"regions\":[{{\"from\":\"zzzz\",\"to\":\"mbxr\",\"function\":\"a\",\"evidence\":[]}}],\"headings\":[]}}").FailureCode);
        Assert.Equal("REGION_FUNCTION_INVALID", V($"{{{Head},\"regions\":[{{\"from\":\"kqzt\",\"to\":\"mbxr\",\"function\":\"  \",\"evidence\":[]}}],\"headings\":[]}}").FailureCode);
    }

    [Fact] public void Missing_region_key_and_truncated_json_are_rejected()
    {
        Assert.Equal("HEADING_KEYS_INVALID", V($"{{{Head},\"regions\":[{{\"from\":\"kqzt\",\"to\":\"mbxr\",\"function\":\"a\",\"evidence\":[]}}],\"headings\":[{{\"id\":\"kqzt\",\"evidenceRefs\":[\"kqzt\"],\"justification\":\"x\"}}]}}").FailureCode);
        Assert.Equal("RESPONSE_NOT_JSON", V($"{{{Head},\"regions\":[").FailureCode);
    }

    [Fact] public void RegionsOf_gives_each_heading_its_region_index_and_the_region_words()
    {
        var json = $"{{{Head},\"regions\":[{{\"from\":\"kqzt\",\"to\":\"kqzt\",\"function\":\"title\",\"evidence\":[\"kqzt\"]}},{{\"from\":\"mbxr\",\"to\":\"hpdv\",\"function\":\"form\",\"evidence\":[]}}],\"headings\":[{{\"id\":\"hpdv\",\"region\":1,\"evidenceRefs\":[\"hpdv\"],\"justification\":\"x\"}}]}}";
        var aliasById = IdByAlias.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);
        var (regions, of) = P7F1RegionProtocolV5.RegionsOf(json, aliasById);
        Assert.Equal(2, regions.Count);
        Assert.Equal("L0001:S0", regions[0].FromAlias);
        Assert.Equal("form", regions[1].Function);
        Assert.Equal(1, of["hpdv"]);
    }
}
