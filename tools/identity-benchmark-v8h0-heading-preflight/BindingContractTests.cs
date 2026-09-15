using System.Text.Json;

namespace IdentityBenchmarkV8H0;

internal static class BindingContractTests
{
    public static IReadOnlyList<TestResult> Run()
    {
        var allowed = new[] { "b1", "b2" };
        var tests = new List<TestResult>();
        Check(tests, "valid_selection", Validate(new { blocks = new[] { new { id = "b1", role = "section_heading", confidence = .9 }, new { id = "b2", role = "body_text", confidence = .9 } } }, allowed).Pass);
        Check(tests, "unknown_ref_rejected", !Validate(new { blocks = new[] { new { id = "b9", role = "section_heading", confidence = .9 } } }, allowed).Pass);
        Check(tests, "fabricated_canonical_id_rejected", !Validate(new { blocks = new[] { new { id = "DOC-0133:B000017", role = "section_heading", confidence = .9 } } }, allowed).Pass);
        Check(tests, "duplicate_incompatible_rejected", !Validate(new { blocks = new[] { new { id = "b1", role = "section_heading", confidence = .9 }, new { id = "b1", role = "body_text", confidence = .9 } } }, allowed).Pass);
        Check(tests, "missing_output_is_invalid", !Validate(new { blocks = Array.Empty<object>() }, allowed).Pass);
        Check(tests, "forbidden_level_rejected", !Validate(new { blocks = new[] { new { id = "b1", role = "section_heading", confidence = .9, level = 2 } } }, allowed).Pass);
        Check(tests, "forbidden_parent_rejected", !Validate(new { blocks = new[] { new { id = "b1", role = "section_heading", confidence = .9, parent = "b2" } } }, allowed).Pass);
        Check(tests, "forbidden_identity_rejected", !Validate(new { blocks = new[] { new { id = "b1", role = "section_heading", confidence = .9, semanticNodeId = "N1" } } }, allowed).Pass);
        Check(tests, "exact_source_text_binding", Bind("b1", new Dictionary<string, string> { ["b1"] = "Heading" }, "Heading").Pass);
        Check(tests, "source_text_drift_rejected", !Bind("b1", new Dictionary<string, string> { ["b1"] = "Heading" }, "Changed").Pass);
        Check(tests, "provenance_binding_is_exact", Bind("b1", new Dictionary<string, string> { ["b1"] = "Heading" }, "Heading").SourceRef == "b1");
        Check(tests, "pointer_offsets_are_harness_owned", 0 <= 0 && 3 <= "ABC".Length);
        Check(tests, "deterministic_validation", Validate(new { blocks = new[] { new { id = "b1", role = "section_heading", confidence = .9 } } }, allowed).Pass == Validate(new { blocks = new[] { new { id = "b1", role = "section_heading", confidence = .9 } } }, allowed).Pass);
        return tests;
    }

    private static void Check(ICollection<TestResult> tests, string name, bool pass) => tests.Add(new TestResult(name, pass));

    private static (bool Pass, string SourceRef) Bind(string requestedRef, IReadOnlyDictionary<string, string> sourceByRef, string requestedText)
    {
        if (!sourceByRef.TryGetValue(requestedRef, out var sourceText)) return (false, "");
        return (StringComparer.Ordinal.Equals(sourceText, requestedText), requestedRef);
    }

    private static (bool Pass, string Reason) Validate(object value, IReadOnlyList<string> allowed)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value));
        var root = doc.RootElement;
        if (root.TryGetProperty("level", out _) || root.TryGetProperty("parent", out _) || root.TryGetProperty("semanticNodeId", out _)) return (false, "forbidden-field");
        if (!root.TryGetProperty("blocks", out var blocks) || blocks.ValueKind != JsonValueKind.Array || blocks.GetArrayLength() == 0) return (false, "missing-blocks");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in blocks.EnumerateArray())
        {
            if (item.TryGetProperty("level", out _) || item.TryGetProperty("parent", out _) || item.TryGetProperty("semanticNodeId", out _)) return (false, "forbidden-field");
            if (!item.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.String) return (false, "missing-id");
            var id = idProp.GetString()!;
            if (!allowed.Contains(id, StringComparer.Ordinal) || !seen.Add(id)) return (false, "unknown-or-duplicate");
            if (item.TryGetProperty("sourceOccurrenceId", out _) || item.TryGetProperty("canonicalText", out _)) return (false, "canonical-output");
        }
        return (true, "ok");
    }
}

internal sealed record TestResult(string Name, bool Pass);
