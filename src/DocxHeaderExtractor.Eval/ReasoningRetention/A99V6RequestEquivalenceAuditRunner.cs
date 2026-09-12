using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Offline request-equivalence audit for DOC-0205. It reconstructs the legacy semantic-text
/// request and the current v6 RunAsync text request from the source only. It never loads Gold or
/// prediction artifacts and never constructs an inference client.
/// </summary>
public static class A99V6RequestEquivalenceAuditRunner
{
    private const string DocumentId = "DOC-0205";
    private const string InventoryPath = "eval/a99-dataset/document-inventory.v1.json";
    private const string OutputPath = "eval/a99-closed-loop/request-equivalence/DOC-0205/audit.v1.json";
    private const string Model = "qwen/qwen3.7-flash";
    private const string Route = "ModelCapabilityCeiling";
    private const string SchemaName = "semantic_text_exact_binding_v1";
    private const int MaxCompletionTokens = 48_000;
    private static readonly JsonSerializerOptions OutputOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var inventoryPath = Path.Combine(repoRoot, InventoryPath.Replace('/', Path.DirectorySeparatorChar));
        using var inventory = JsonDocument.Parse(await File.ReadAllTextAsync(inventoryPath, ct));
        var inventoryItem = inventory.RootElement.GetProperty("documents").EnumerateArray()
            .Single(item => string.Equals(item.GetProperty("documentId").GetString(), DocumentId, StringComparison.Ordinal));
        var sourcePath = Path.Combine(repoRoot, inventoryItem.GetProperty("sourcePath").GetString()!
            .Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
        var expectedSourceSha = inventoryItem.GetProperty("sourceSha256").GetString()!;
        var actualSourceSha = Sha256File(sourcePath);
        if (!string.Equals(actualSourceSha, expectedSourceSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"SOURCE_HASH_MISMATCH:{DocumentId}");

        var source = new OpenXmlDocumentSource().Read(sourcePath) with { DocumentId = DocumentId };
        var oldRows = source.Paragraphs
            .Where(paragraph => !string.IsNullOrWhiteSpace(paragraph.Text))
            .Select((paragraph, index) => new SourceRow($"S{index + 1:0000}", paragraph.SourceId,
                paragraph.SourceOrdinal, paragraph.Text))
            .ToArray();
        var oldPacket = BuildPacket(oldRows);

        // This is the exact source preparation used by RunAsync for the text route, including
        // its deterministic visual/evidence preparation. It is offline and does not call a model.
        var prepared = await VisualSourceEvidenceBuilder.BuildAsync(sourcePath, int.MaxValue, ct);
        var newAliases = SemanticSourceAliasCatalog.FromCatalog(prepared.Catalog);
        var newPacket = BuildPacket(newAliases.Select(alias =>
            new SourceRow(alias.Alias, alias.SourceId, alias.SourceOrdinal, alias.Text)).ToArray());
        var targetEvidence = oldRows.Select(row => $"{row.Alias}: {row.Text}").ToArray();
        var localContext = Array.Empty<string>();
        var globalContext = new[] { oldPacket };
        var newPackedContext = SemanticContextPacker.Pack(targetEvidence, localContext, globalContext);

        var oldUser = SemanticTextExactBindingContract.BuildUser(oldPacket, Route);
        var newUser = SemanticTextExactBindingContract.BuildUser(newPacket, Route);
        var system = SemanticTextExactBindingContract.System;
        var schema = SemanticTextExactBindingContract.Schema();
        var reasoning = new { enabled = true, exclude = true };
        var oldBody = OpenRouterCeilingReasoningModel.BuildRequestBodyForAudit(
            Model, system, oldUser, MaxCompletionTokens, schema, SchemaName, reasoning, null, true);
        var newBody = OpenRouterCeilingReasoningModel.BuildRequestBodyForAudit(
            Model, system, newUser, MaxCompletionTokens, schema, SchemaName, reasoning, null, true);
        var oldBodyJson = Encoding.UTF8.GetString(OpenRouterCeilingReasoningModel.SerializeRequestBodyForAudit(oldBody));
        var newBodyJson = Encoding.UTF8.GetString(OpenRouterCeilingReasoningModel.SerializeRequestBodyForAudit(newBody));

        var aliasMappingOld = oldRows.Select(row => new { alias = row.Alias, sourceId = row.SourceId }).ToArray();
        var aliasMappingNew = newAliases.Select(alias => new { alias = alias.Alias, sourceId = alias.SourceId }).ToArray();
        var oldRowsForComparison = oldRows.Select(row => new
        {
            alias = row.Alias,
            sourceId = row.SourceId,
            sourceOrdinal = row.SourceOrdinal,
            text = row.Text,
        }).ToArray();
        var newRowsForComparison = newAliases.Select(alias => new { alias = alias.Alias, sourceId = alias.SourceId, sourceOrdinal = alias.SourceOrdinal, text = alias.Text }).ToArray();
        var oldContext = new { targetEvidence = Array.Empty<string>(), localContext = Array.Empty<string>(), globalContext = Array.Empty<string>(), packedContext = (object?)null };
        var newContext = new { targetEvidence, localContext, globalContext, packedContext = newPackedContext };

        var report = new
        {
            schemaVersion = "a99-v6-request-equivalence-audit-v1",
            documentId = DocumentId,
            sourcePath = inventoryItem.GetProperty("sourcePath").GetString(),
            sourceSha256 = actualSourceSha,
            startHead = GitSha(repoRoot),
            model = Model,
            route = Route,
            noModelCalls = true,
            modelCalls = 0,
            providerCalls = 0,
            goldRead = false,
            predictionRead = false,
            requestShape = new
            {
                maxTokens = MaxCompletionTokens,
                reasoning = new { enabled = true, exclude = true },
                providerRoute = "MODEL_DEFAULT",
                allowNonZdrPublicBenchmark = true,
                note = "Reasoning shape is fixed only to compare old/new; both branches use the same runtime capability shape."
            },
            deltas = new
            {
                systemPrompt = CompareText(system, system),
                userMessage = CompareText(oldUser, newUser),
                structuredOutputSchema = CompareJson(schema, schema),
                sourceAliasCatalog = CompareJson(oldRowsForComparison, newRowsForComparison),
                aliasSourceIdMapping = CompareJson(aliasMappingOld, aliasMappingNew),
                sourceRowsCount = new { old = oldRows.Length, @new = newAliases.Count, delta = oldRows.Length == newAliases.Count ? 0 : 1 },
                sourceRowsOrder = CompareJson(oldRowsForComparison.Select(row => new { sourceId = row.sourceId, sourceOrdinal = row.sourceOrdinal, text = row.text }), newRowsForComparison.Select(row => new { sourceId = row.sourceId, sourceOrdinal = row.sourceOrdinal, text = row.text })),
                targetEvidence = CompareJson(oldContext.targetEvidence, newContext.targetEvidence),
                localContext = CompareJson(oldContext.localContext, newContext.localContext),
                globalContext = CompareJson(oldContext.globalContext, newContext.globalContext),
                packedContext = CompareText(JsonSerializer.Serialize(oldContext.packedContext), JsonSerializer.Serialize(newContext.packedContext)),
                packedContextProviderVisible = false,
                actualSerializedProviderJson = CompareText(oldBodyJson, newBodyJson),
            },
            providerRequest = new
            {
                oldSerializedJson = oldBodyJson,
                newSerializedJson = newBodyJson,
                oldSha256 = Sha256Text(oldBodyJson),
                newSha256 = Sha256Text(newBodyJson),
                byteEqual = string.Equals(oldBodyJson, newBodyJson, StringComparison.Ordinal),
                canonicalRequestConclusion = string.Equals(oldBodyJson, newBodyJson, StringComparison.Ordinal)
                    ? "PROVIDER_VISIBLE_REQUEST_IDENTICAL"
                    : "PROVIDER_VISIBLE_REQUEST_DIFFERENT"
            },
            sourcePreparation = new
            {
                oldNonWhitespaceRows = oldRows.Length,
                newCatalogUnits = newAliases.Count,
                oldPacketSha256 = Sha256Text(oldPacket),
                newPacketSha256 = Sha256Text(newPacket),
                newVisualPages = prepared.VisualPages.Count,
                newPages = prepared.Pages.Count,
                newCatalogUses = "DocumentSourceCatalogBuilder.FromSourceDocument"
            },
            conclusion = string.Equals(oldBodyJson, newBodyJson, StringComparison.Ordinal)
                ? "No provider-visible request delta was found; the 71-to-49 change is not explained by serialized request content. Investigate provider/model stochasticity or unpersisted runtime state next."
                : "A provider-visible request delta was found; inspect the component hashes above before attributing the 71-to-49 change to provider behavior."
        };

        var output = Path.Combine(repoRoot, OutputPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, OutputOptions) + Environment.NewLine, ct);
        Console.WriteLine("MODEL_CALLS=0");
        Console.WriteLine("PROVIDER_CALLS=0");
        Console.WriteLine($"DOC0205_REQUEST_EQUIVALENCE={Path.Combine(OutputPath)}");
        Console.WriteLine($"SOURCE_ROWS_OLD={oldRows.Length}");
        Console.WriteLine($"SOURCE_ROWS_NEW={newAliases.Count}");
        Console.WriteLine($"PACKET_HASH_OLD={Sha256Text(oldPacket)}");
        Console.WriteLine($"PACKET_HASH_NEW={Sha256Text(newPacket)}");
        Console.WriteLine($"PROVIDER_JSON_EQUAL={string.Equals(oldBodyJson, newBodyJson, StringComparison.Ordinal)}");
        return 0;
    }

    private static string BuildPacket(IReadOnlyList<SourceRow> rows) => JsonSerializer.Serialize(new
    {
        sourceAliases = rows.Select(row => new { alias = row.Alias, text = row.Text, sourceOrdinal = row.SourceOrdinal }).ToArray()
    });

    private static object CompareText(string oldValue, string newValue) => new
    {
        oldSha256 = Sha256Text(oldValue),
        newSha256 = Sha256Text(newValue),
        oldBytes = Encoding.UTF8.GetByteCount(oldValue),
        newBytes = Encoding.UTF8.GetByteCount(newValue),
        delta = string.Equals(oldValue, newValue, StringComparison.Ordinal) ? 0 : 1,
        firstDifference = FirstDifference(oldValue, newValue)
    };

    private static object CompareJson(object? oldValue, object? newValue) => CompareText(
        JsonSerializer.Serialize(oldValue), JsonSerializer.Serialize(newValue));

    private static object? FirstDifference(string oldValue, string newValue)
    {
        var length = Math.Min(oldValue.Length, newValue.Length);
        for (var i = 0; i < length; i++)
            if (oldValue[i] != newValue[i])
                return new { utf16Index = i, oldChar = oldValue[i].ToString(), newChar = newValue[i].ToString() };
        return oldValue.Length == newValue.Length ? null : new { utf16Index = length, oldChar = "<end>", newChar = "<end>" };
    }

    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string GitSha(string repoRoot)
    {
        using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD")
        {
            WorkingDirectory = repoRoot, RedirectStandardOutput = true, UseShellExecute = false,
        });
        process?.WaitForExit();
        return process?.StandardOutput.ReadToEnd().Trim() ?? "NOT_PERSISTED";
    }

    private sealed record SourceRow(string Alias, string SourceId, int SourceOrdinal, string Text);
}
