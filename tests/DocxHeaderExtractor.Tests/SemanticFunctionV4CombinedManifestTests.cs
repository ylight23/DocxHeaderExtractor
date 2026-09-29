using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free V4 lineage proof. It deliberately does not inspect response content or Gold.</summary>
public sealed class SemanticFunctionV4CombinedManifestTests
{
    private const string Root = "eval/a99-closed-loop/semantic-function-single-authority-v4";
    private const string Preflight = Root + "/preflight.v1.json";
    private const string Attempt1 = Root + "/run.v1.json";
    private const string Continuation = Root + "/continuation.v1.json";

    [Fact]
    public async Task Freeze_the_combined_raw_run_manifest_before_gold_is_opened()
    {
        using var preflight = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Preflight)));
        using var first = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Attempt1)));
        using var continuation = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Continuation)));
        Assert.False(first.RootElement.GetProperty("goldRead").GetBoolean());
        Assert.False(continuation.RootElement.GetProperty("goldRead").GetBoolean());
        Assert.Null(continuation.RootElement.GetProperty("stopped").GetString());

        var planned = preflight.RootElement.GetProperty("documents").EnumerateArray()
            .SelectMany(d => d.GetProperty("requestSha256").EnumerateArray().Select(h => new { Document = d.GetProperty("documentId").GetString()!, Hash = h.GetString()! }))
            .ToArray();
        var attempts = first.RootElement.GetProperty("ledger").EnumerateArray().Concat(continuation.RootElement.GetProperty("ledger").EnumerateArray())
            .Select(x => new Attempt(x.GetProperty("DocumentId").GetString()!, x.GetProperty("RequestSha256").GetString()!,
                x.GetProperty("Error").ValueKind == JsonValueKind.Null && x.GetProperty("Response").ValueKind == JsonValueKind.String)).ToArray();
        var successful = attempts.Where(a => a.Success).ToArray();
        var unexpected = successful.Where(a => !planned.Any(p => p.Hash == a.Hash)).ToArray();
        var duplicates = successful.GroupBy(a => a.Hash).Where(g => g.Count() > 1).ToArray();
        var pending = planned.Where(p => !successful.Any(a => a.Hash == p.Hash)).ToArray();
        Assert.Equal(26, attempts.Length); Assert.Equal(25, successful.Length); Assert.Single(attempts.Where(a => !a.Success));
        Assert.Equal(5, successful.Count(a => a.Document == "SRC-089")); Assert.Equal(20, successful.Count(a => a.Document == "SRC-095"));
        Assert.Empty(unexpected); Assert.Empty(duplicates); Assert.Empty(pending);
        var totals = continuation.RootElement.GetProperty("totals");
        Assert.Equal(26, totals.GetProperty("calls").GetInt32()); Assert.Equal(712286L, totals.GetProperty("input").GetInt64()); Assert.Equal(46095L, totals.GetProperty("output").GetInt64());

        FreezeArtifact.AssertJson(Root, "combined-run-manifest.v1.json", new {
            artifactKind="a99_semantic_function_v4_combined_raw_manifest", status="COMPLETE_25_OF_25", modelProviderCalls=0,
            lineage=new { preflightSha256=Hash(Preflight), attempt1Sha256=Hash(Attempt1), continuationSha256=Hash(Continuation) },
            attempts=new { total=attempts.Length, successful=successful.Length, failed=attempts.Count(a=>!a.Success), successfulByDocument=successful.GroupBy(a=>a.Document).ToDictionary(g=>g.Key,g=>g.Count()), pending=pending.Length, duplicateSuccessful=duplicates.Length, unexpected=unexpected.Length },
            totals=new { calls=totals.GetProperty("calls").GetInt32(), input=totals.GetProperty("input").GetInt64(), output=totals.GetProperty("output").GetInt64(), caps=new { calls=30,input=2000000,output=250000 } },
            invariants=new { goldRead=false, hierarchyRun=false, postFilterApplied=false, rawResponsesNotCopiedOrInterpreted=true }
        });
    }

    [Fact]
    public void The_v4_scorer_gate_refuses_any_lineage_other_than_complete_25_of_25()
    {
        var path=TestRepository.Path(Root+"/combined-run-manifest.v1.json"); Assert.True(File.Exists(path));
        using var manifest=JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("COMPLETE_25_OF_25",manifest.RootElement.GetProperty("status").GetString());
        Assert.Equal(25,manifest.RootElement.GetProperty("attempts").GetProperty("successful").GetInt32());
    }
    private sealed record Attempt(string Document,string Hash,bool Success);
    private static string Hash(string path)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(TestRepository.Path(path)).ReplaceLineEndings("\n"))));
}
