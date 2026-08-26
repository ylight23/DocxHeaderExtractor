using System.Security.Cryptography;
using System.Text.Json;
using DocxHeaderExtractor.Core.Eval;

namespace DocxHeaderExtractor.Tests;

public sealed class HumanAuditAgreementEvaluatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dhx-human-audit-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void WriteCommittedBlindResultTemplate()
    {
        var output = Environment.GetEnvironmentVariable("BENCH_N14_AUDIT_TEMPLATE");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, HumanAuditAgreementEvaluator.CreateTemplate(SourcePath(), BindingPath()).GetRawText());
    }

    [Fact]
    public void ReviewerTemplateIsBlindAndPreservesTheExact119ItemPopulation()
    {
        var template = HumanAuditAgreementEvaluator.CreateTemplate(SourcePath(), BindingPath());
        var root = template;
        Assert.Equal(119, root.GetProperty("reviewItems").GetArrayLength());
        var serialized = root.GetRawText();
        Assert.DoesNotContain("silverLabel", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("silverConfidence", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("candidateId", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("selected", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CommittedTemplateReproducesByteForByte()
    {
        var path = Path.Combine(PdfExtractorQualityBenchmarkProbe.RepositoryRoot(), "eval", "benchmark-n0", "audit-results", "n1.4-human-audit-result.template.v1.json");
        if (!File.Exists(path)) return;
        Assert.Equal(HumanAuditAgreementEvaluator.CreateTemplate(SourcePath(), BindingPath()).GetRawText().Replace("\r\n", "\n"),
            File.ReadAllText(path).Replace("\r\n", "\n"));
    }

    [Fact]
    public void IncompleteResultIsPartialAndProducesNoAgreementMetrics()
    {
        var result = WriteResult(labels: (_, _) => null);
        var report = HumanAuditAgreementEvaluator.Evaluate(SourcePath(), BindingPath(), result);
        Assert.Equal("NO_HUMAN_AUDIT", report.ClaimStatus);
        Assert.False(report.Complete);
        Assert.Null(report.Metrics);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("invalid")]
    public void InvalidResultPopulationIsRejected(string mutation)
    {
        var result = WriteResult(labels: (_, binding) => binding.GetProperty("silverLabel").GetString());
        using var document = JsonDocument.Parse(File.ReadAllText(result));
        var items = document.RootElement.GetProperty("reviewItems").EnumerateArray().Select(item => item.GetRawText()).ToList();
        switch (mutation)
        {
            case "duplicate": items[^1] = items[0]; break;
            case "missing": items.RemoveAt(0); break;
            case "extra": items.Add(items[0].Replace("\"auditItemId\":\"", "\"auditItemId\":\"unknown/", StringComparison.Ordinal)); break;
            case "invalid": items[0] = items[0].Replace("REVIEWED_", "INVALID_", StringComparison.Ordinal); break;
        }
        File.WriteAllText(result, $"{{\"schemaVersion\":1,\"artifactKind\":\"n1_4_human_audit_result\",\"sourcePacketSha256\":\"{Sha256(SourcePath())}\",\"bindingSha256\":\"{Sha256(BindingPath())}\",\"reviewItems\":[{string.Join(',', items)}]}}");
        Assert.Throws<InvalidDataException>(() => HumanAuditAgreementEvaluator.Evaluate(SourcePath(), BindingPath(), result));
    }

    [Fact]
    public void CompleteSyntheticFixtureProducesDeterministicAgreementAndOccurrenceSafeDisagreement()
    {
        var result = WriteResult(labels: (index, binding) => index == 0 ?
            binding.GetProperty("silverLabel").GetString() == HumanAuditAgreementEvaluator.Heading
                ? HumanAuditAgreementEvaluator.NonHeading : HumanAuditAgreementEvaluator.Heading
            : binding.GetProperty("silverLabel").GetString());
        var first = HumanAuditAgreementEvaluator.Evaluate(SourcePath(), BindingPath(), result);
        var second = HumanAuditAgreementEvaluator.Evaluate(SourcePath(), BindingPath(), result);
        Assert.Equal("HUMAN_AUDITED_SILVER", first.ClaimStatus);
        Assert.True(first.Complete);
        Assert.Equal(119, first.Metrics!.Overall.Total);
        Assert.Single(first.Disagreements);
        Assert.NotEmpty(first.Disagreements[0].Identity.SourceLineIds);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
    }

    [Fact]
    public void EvaluationDoesNotMutateSilverAuthorityFiles()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var silverFiles = Directory.GetFiles(Path.Combine(root, "eval", "benchmark-n0", "silver-labels"), "*.json")
            .ToDictionary(path => path, Sha256, StringComparer.OrdinalIgnoreCase);
        var result = WriteResult(labels: (_, binding) => binding.GetProperty("silverLabel").GetString());
        _ = HumanAuditAgreementEvaluator.Evaluate(SourcePath(), BindingPath(), result);
        Assert.All(silverFiles, item => Assert.Equal(item.Value, Sha256(item.Key)));
    }

    private string WriteResult(Func<int, JsonElement, string?> labels)
    {
        Directory.CreateDirectory(_directory);
        using var source = JsonDocument.Parse(File.ReadAllText(SourcePath()));
        using var binding = JsonDocument.Parse(File.ReadAllText(BindingPath()));
        var bindings = binding.RootElement.GetProperty("items").EnumerateArray().ToArray();
        var items = source.RootElement.GetProperty("items").EnumerateArray().Select((item, index) => new
        {
            auditItemId = item.GetProperty("AuditItemId").GetString(),
            humanLabel = labels(index, bindings[index]),
            reviewerNote = (string?)null,
        });
        var path = Path.Combine(_directory, "human-result.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            artifactKind = "n1_4_human_audit_result",
            sourcePacketSha256 = Sha256(SourcePath()),
            bindingSha256 = Sha256(BindingPath()),
            reviewItems = items,
        }));
        return path;
    }

    private static string SourcePath() => Path.Combine(PdfExtractorQualityBenchmarkProbe.RepositoryRoot(), "eval", "benchmark-n0", "audit-samples", "n1.4-silver-human-audit-source.v1.json");
    private static string BindingPath() => Path.Combine(PdfExtractorQualityBenchmarkProbe.RepositoryRoot(), "eval", "benchmark-n0", "audit-samples", "n1.4-silver-human-audit-binding.v1.json");
    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
