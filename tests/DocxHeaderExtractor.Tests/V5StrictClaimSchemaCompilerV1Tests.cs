using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Provider-free tests for the generic strict-schema compiler built for the qwen3.8-27b:free
/// model-capability canary. Never calls a provider; only checks the compiled schema's own shape.
/// </summary>
public sealed class V5StrictClaimSchemaCompilerV1Tests
{
    [Fact]
    public void Compiling_the_same_contract_twice_is_byte_identical()
    {
        var contract = Contract();
        var first = JsonSerializer.Serialize(V5StrictClaimSchemaCompilerV1.Compile(contract));
        var second = JsonSerializer.Serialize(V5StrictClaimSchemaCompilerV1.Compile(contract));
        Assert.Equal(first, second);
    }

    [Fact]
    public void Every_predicate_gets_its_own_oneOf_branch_with_a_single_value_enum()
    {
        var root = SerializeToElement(V5StrictClaimSchemaCompilerV1.Compile(Contract()));
        var branches = root.GetProperty("properties").GetProperty("claims").GetProperty("items").GetProperty("oneOf");
        Assert.Equal(7, branches.GetArrayLength()); // 3 unary + 4 relations in DocumentStructureTaskContract

        var names = branches.EnumerateArray()
            .Select(branch => branch.GetProperty("properties").GetProperty("predicate").GetProperty("enum")[0].GetString())
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[] { "CONTINUES", "DOCUMENT_IDENTITY", "NAVIGATION_REPRESENTATION", "PARENT_OF", "REFERENCES", "SAME_ENTITY", "STRUCTURAL_REGION" },
            names);
    }

    [Fact]
    public void A_unary_branch_has_no_object_property_and_a_relation_branch_has_no_value_property()
    {
        var root = SerializeToElement(V5StrictClaimSchemaCompilerV1.Compile(Contract()));
        var branches = root.GetProperty("properties").GetProperty("claims").GetProperty("items").GetProperty("oneOf")
            .EnumerateArray().ToArray();

        var unary = branches.Single(b => b.GetProperty("properties").GetProperty("predicate").GetProperty("enum")[0].GetString() == "STRUCTURAL_REGION");
        Assert.True(unary.GetProperty("properties").TryGetProperty("value", out _));
        Assert.False(unary.GetProperty("properties").TryGetProperty("object", out _));

        var relation = branches.Single(b => b.GetProperty("properties").GetProperty("predicate").GetProperty("enum")[0].GetString() == "REFERENCES");
        Assert.True(relation.GetProperty("properties").TryGetProperty("object", out _));
        Assert.False(relation.GetProperty("properties").TryGetProperty("value", out _));
    }

    [Fact]
    public void Every_declared_property_at_every_level_is_listed_in_required()
    {
        var root = SerializeToElement(V5StrictClaimSchemaCompilerV1.Compile(Contract()));
        void AssertAllRequired(JsonElement schema)
        {
            if (schema.ValueKind != JsonValueKind.Object) return;
            if (schema.TryGetProperty("properties", out var properties) && schema.TryGetProperty("required", out var required))
            {
                var propertyNames = properties.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
                var requiredNames = required.EnumerateArray().Select(r => r.GetString()!).Order(StringComparer.Ordinal).ToArray();
                Assert.Equal(propertyNames, requiredNames);
                foreach (var property in properties.EnumerateObject()) AssertAllRequired(property.Value);
            }
            if (schema.TryGetProperty("items", out var items)) AssertAllRequired(items);
            if (schema.TryGetProperty("oneOf", out var oneOf))
                foreach (var branch in oneOf.EnumerateArray()) AssertAllRequired(branch);
        }
        AssertAllRequired(root);
    }

    [Fact]
    public void No_property_is_missing_additionalProperties_false()
    {
        var json = JsonSerializer.Serialize(V5StrictClaimSchemaCompilerV1.Compile(Contract()));
        var root = SerializeToElement(V5StrictClaimSchemaCompilerV1.Compile(Contract()));
        var objectSchemaCount = CountObjectSchemas(root);
        var additionalPropertiesFalseCount = System.Text.RegularExpressions.Regex.Matches(json, "\"additionalProperties\":false").Count;
        Assert.Equal(objectSchemaCount, additionalPropertiesFalseCount);
    }

    private static int CountObjectSchemas(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object) return 0;
        var count = schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "object" ? 1 : 0;
        if (schema.TryGetProperty("properties", out var properties))
            count += properties.EnumerateObject().Sum(p => CountObjectSchemas(p.Value));
        if (schema.TryGetProperty("items", out var items)) count += CountObjectSchemas(items);
        if (schema.TryGetProperty("oneOf", out var oneOf)) count += oneOf.EnumerateArray().Sum(CountObjectSchemas);
        return count;
    }

    [Fact]
    public void Schema_avoids_the_narrower_strict_mode_keyword_subset()
    {
        var json = JsonSerializer.Serialize(V5StrictClaimSchemaCompilerV1.Compile(Contract()));
        foreach (var forbidden in new[] { "\"const\"", "\"minLength\"", "\"minItems\"", "\"minimum\"", "\"pattern\"", "\"format\"", "\"$ref\"", "\"$defs\"" })
            Assert.DoesNotContain(forbidden, json, StringComparison.Ordinal);
    }

    [Fact]
    public void Owned_and_visible_alias_enums_are_injected_only_where_given()
    {
        var root = SerializeToElement(V5StrictClaimSchemaCompilerV1.Compile(Contract(), ["A1", "A2"], ["A1", "A2", "A3"]));
        var branch = root.GetProperty("properties").GetProperty("claims").GetProperty("items").GetProperty("oneOf")
            .EnumerateArray().Single(b => b.GetProperty("properties").GetProperty("predicate").GetProperty("enum")[0].GetString() == "REFERENCES");

        var subjectAliasEnum = branch.GetProperty("properties").GetProperty("subject").GetProperty("properties")
            .GetProperty("sourceParts").GetProperty("items").GetProperty("properties").GetProperty("sourceAlias").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(["A1", "A2"], subjectAliasEnum);

        var objectAliasEnum = branch.GetProperty("properties").GetProperty("object").GetProperty("properties")
            .GetProperty("sourceParts").GetProperty("items").GetProperty("properties").GetProperty("sourceAlias").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(["A1", "A2", "A3"], objectAliasEnum);
    }

    [Fact]
    public void No_alias_enum_leaves_sourceAlias_an_unconstrained_string()
    {
        var root = SerializeToElement(V5StrictClaimSchemaCompilerV1.Compile(Contract()));
        var branch = root.GetProperty("properties").GetProperty("claims").GetProperty("items").GetProperty("oneOf")
            .EnumerateArray().First();
        var aliasSchema = branch.GetProperty("properties").GetProperty("subject").GetProperty("properties")
            .GetProperty("sourceParts").GetProperty("items").GetProperty("properties").GetProperty("sourceAlias");
        Assert.False(aliasSchema.TryGetProperty("enum", out _));
        Assert.Equal("string", aliasSchema.GetProperty("type").GetString());
    }

    private static JsonElement SerializeToElement(object value) => JsonSerializer.SerializeToElement(value);

    private static DocumentTaskContract Contract() =>
        DocxHeaderExtractor.DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
}
