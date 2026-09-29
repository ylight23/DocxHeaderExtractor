using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The PDF production lane against the configuration that was last measured (T3B: P05 packing, V4
/// semantic function, reasoning none). Provider-free.
/// <para>
/// What must not have moved - the system prompt and the response schema - is asserted byte for byte
/// against the T3B requests. What did move is recorded rather than hidden: the style facts are now raw
/// measurements and ratios instead of the harness's own bold/size judgements, which changes the owned
/// evidence bytes and therefore where P05 cuts. The T3B score does not carry over to that request, so
/// this artifact freezes the production plan a re-baseline provider run must execute.
/// </para>
/// </summary>
public sealed class PdfV4ProductionEquivalenceTests
{
    private const string Root = "eval/a99-closed-loop/request-architecture-v2";
    private const string T3c = Root + "/openrouter-streaming-t3c-efficiency-heavy-leaf-audit.v1.json";
    private const string NoneInitial = "openrouter-streaming-t3b-reasoning-none-full-cohort-20260928T091634Z";
    private const string NoneContinuation = "openrouter-streaming-t3b-reasoning-none-full-cohort-continuation-003-031-20260928T091819Z";
    private static readonly (string Id, string Pdf)[] Documents =
        [("SRC-089", SourcePdfCorpus.Src089), ("SRC-095", SourcePdfCorpus.Src095)];

    private sealed record Measured(string DocumentId, int Ordinal, string SystemPrompt, string UserMessage);

    [Fact]
    public async Task Production_pdf_route_keeps_the_measured_contract_and_freezes_its_rebaseline_plan()
    {
        var measured = LoadT3bRequests();
        var documents = new List<object>();
        foreach (var (id, pdf) in Documents)
        {
            using var capture = new RequestCapturingClassifier();
            await CanonicalSemanticPdfAuthorityAdapter.RunAsync(
                TestRepository.Path(pdf), capture, CancellationToken.None, runPlacement: false);
            var sent = capture.Requests;
            var before = measured.Where(x => x.DocumentId == id).ToArray();
            Assert.NotEmpty(sent);

            // Unchanged: one system prompt, and the same schema suffix on every request.
            Assert.All(sent, request => Assert.Equal(before[0].SystemPrompt, request.SystemPrompt));
            Assert.All(sent, request => Assert.Equal(Schema(before[0].UserMessage), Schema(request.UserMessage)));

            // Every atom is owned exactly once, in source order.
            var atoms = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(pdf)).Atoms;
            var owned = sent.SelectMany(request => OwnedAliases(request.UserMessage)).ToArray();
            Assert.Equal(atoms.Select(atom => atom.Alias), owned);

            // Changed, and named: the owned evidence's style facts.
            var styleKeysBefore = StyleKeys(before[0].UserMessage);
            var styleKeysNow = StyleKeys(sent[0].UserMessage);

            documents.Add(new
            {
                documentId = id,
                t3bRequests = before.Length,
                productionRequests = sent.Count,
                identicalUserMessages = sent.Count(request => before.Any(x => x.UserMessage == request.UserMessage)),
                productionOwnedPerRequest = sent.Select(request => OwnedAliases(request.UserMessage).Count).ToArray(),
                productionUserMessageBytes = sent.Select(request => Encoding.UTF8.GetByteCount(request.UserMessage)).ToArray(),
                productionRequestSha256 = sent.Select(request => Sha(request.SystemPrompt + "\n" + request.UserMessage)).ToArray(),
                styleKeysRemoved = styleKeysBefore.Except(styleKeysNow).Order(StringComparer.Ordinal).ToArray(),
                styleKeysAdded = styleKeysNow.Except(styleKeysBefore).Order(StringComparer.Ordinal).ToArray(),
            });
        }

        FreezeArtifact.AssertJson(Root, "pdf-v4-production-rebaseline-preflight.v1.json", new
        {
            artifactKind = "a99_pdf_v4_production_rebaseline_preflight",
            providerCalls = 0,
            goldRead = false,
            route = "CanonicalSemanticPdfAuthorityAdapter.RunAsync (P05 packing, V4 request, PdfSemanticFunctionMembershipV1)",
            unchangedVsT3b = new[] { "system prompt", "response schema", "coordinate contract", "packing budget P05", "exact-once ownership" },
            changedVsT3b = "owned style facts: raw measurements and ratios replace harness bold/italic/relative-size judgements",
            consequence = "T3B score (F1 0.7451) does not carry over; production requires a re-baseline provider run under this plan before any comparison",
            documents,
        });
    }

    private static string Schema(string userMessage) => userMessage[userMessage.IndexOf("\nSCHEMA=", StringComparison.Ordinal)..];

    private static JsonDocument Packet(string userMessage) =>
        JsonDocument.Parse(userMessage[..userMessage.IndexOf("\nSCHEMA=", StringComparison.Ordinal)]);

    private static List<string> OwnedAliases(string userMessage)
    {
        using var packet = Packet(userMessage);
        return packet.RootElement.GetProperty("ownedSourceAliases").EnumerateArray().Select(x => x.GetString()!).ToList();
    }

    private static HashSet<string> StyleKeys(string userMessage)
    {
        using var packet = Packet(userMessage);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in packet.RootElement.GetProperty("sourceEvidence").EnumerateArray())
        {
            if (!item.TryGetProperty("style", out var style) || style.ValueKind != JsonValueKind.Object) continue;
            foreach (var property in style.EnumerateObject())
            {
                keys.Add(property.Name);
                if (property.Value.ValueKind == JsonValueKind.Object)
                    foreach (var inner in property.Value.EnumerateObject()) keys.Add($"{property.Name}.{inner.Name}");
            }
        }
        return keys;
    }

    private static string Sha(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static List<Measured> LoadT3bRequests()
    {
        using var t3c = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(T3c)));
        var result = new List<Measured>();
        foreach (var row in t3c.RootElement.GetProperty("rows").EnumerateArray())
        {
            var run = row.GetProperty("acceptedRun").GetString() == "initial" ? NoneInitial : NoneContinuation;
            using var request = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(
                $"{Root}/{run}/{row.GetProperty("acceptedRequestFile").GetString()}")));
            var messages = request.RootElement.GetProperty("messages").EnumerateArray().ToArray();
            string Message(string role) => messages.First(m => m.GetProperty("role").GetString() == role)
                .GetProperty("content").GetString()!;
            result.Add(new Measured(row.GetProperty("documentId").GetString()!, row.GetProperty("ordinal").GetInt32(),
                Message("system"), Message("user")));
        }
        return result;
    }
}
