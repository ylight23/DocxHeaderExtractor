using System.Text.Json;
using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

/// <summary>Issue #7 V4.2a: evidence tools accept only this request's issued ids (no O# wording) and stay V4.1-identical otherwise.</summary>
public sealed class P7F1DirectV42ToolTests
{
    static JsonElement Box(double l, double r, double b, double t) => JsonSerializer.SerializeToElement(new { left = l, right = r, bottom = b, top = t });
    static JsonElement Font(double size, double bold) => JsonSerializer.SerializeToElement(new { fontSize = size, fontName = "times", boldRatio = bold, italicRatio = 0.0 });

    static readonly F1QSourceLine[] Lines =
    [
        new("L0000:S0", 0, 1, "Table of Contents", Box(50, 120, 700, 712), Font(14, 1)),
        new("L0001:S0", 1, 1, "1. Introduction", Box(50, 200, 680, 690), Font(11, 0)),
        new("L0002:S0", 2, 1, "Body paragraph text.", Box(50, 500, 660, 670), Font(11, 0)),
        new("L0003:S0", 3, 2, "1. Introduction", Box(50, 200, 680, 690), Font(14, 1)),
    ];

    [Fact] public void V3_definitions_ask_for_issued_ids_and_never_mention_O_numbers()
    {
        var v3 = P7F1QEvidenceTools.DefinitionsV2(issuedIds: true).ToJsonString();
        Assert.Contains("issuedSubjects", v3);
        Assert.DoesNotContain("O7", v3);
        // V2 (frozen for the V4 pilot and V4.1) keeps its O# wording, so its tool definition hash does not move.
        var v2 = P7F1QEvidenceTools.DefinitionsV2().ToJsonString();
        Assert.Contains("such as O7", v2);
        Assert.NotEqual(v2, v3);
    }

    [Fact] public void V3_instance_resolves_only_its_request_ids_and_reports_the_v3_version()
    {
        var doc = new P7F1QEvidenceTools("s", "u", "st", Lines, new Dictionary<string, string> { ["O1"] = "L0001:S0" }, toolSet: 2);
        var request = doc.WithIssued(new Dictionary<string, string> { ["kqzt"] = "L0001:S0" });

        var ok = request.Execute("E1.1", "get_occurrence_context", """{"target":"kqzt","before":0,"after":1}""");
        Assert.Equal("OK", ok.Status);
        Assert.Contains("P7_F1Q_READ_ONLY_EVIDENCE_TOOLS_V3", System.Text.Encoding.UTF8.GetString(ok.Content));
        // The id issued to this request is echoed as the occurrence key of its own line.
        Assert.Contains("\"occurrence\":\"kqzt\"", System.Text.Encoding.UTF8.GetString(ok.Content));

        // Another request's O-number is not issued here, so it is refused rather than silently resolved.
        var refused = request.Execute("E1.2", "get_occurrence_context", """{"target":"O1","before":0,"after":1}""");
        Assert.Equal("INVALID_ARGUMENT", refused.Status);
        Assert.Equal("TARGET_NOT_ISSUED_OR_NOT_IN_DOCUMENT", refused.ErrorCode);
    }

    [Fact] public void V3_instance_still_resolves_source_aliases_outside_the_issued_set()
    {
        var request = new P7F1QEvidenceTools("s", "u", "st", Lines, new Dictionary<string, string> { ["O1"] = "L0001:S0" }, toolSet: 2)
            .WithIssued(new Dictionary<string, string> { ["kqzt"] = "L0001:S0" });
        var r = request.Execute("E1.3", "get_occurrence_context", """{"target":"L0002:S0","before":0,"after":0}""");
        Assert.Equal("OK", r.Status);
    }

    [Fact] public void NextOf_stays_on_the_same_page()
    {
        var tools = new P7F1QEvidenceTools("s", "u", "st", Lines, new Dictionary<string, string> { ["O1"] = "L0001:S0" }, toolSet: 2);
        Assert.Equal("L0002:S0", tools.NextOf("L0001:S0")!.Alias);
        Assert.Null(tools.NextOf("L0002:S0"));   // next line is on page 2
    }
}
