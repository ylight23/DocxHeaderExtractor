using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Infrastructure.AI;

// Offline sizes only. Metadata is supplied as an already captured public JSON file.
// No tokenizer substitution, provider transport, credentials, raw responses, PDF, or Gold reads.
if (args.Length != 6) throw new ArgumentException("Usage: <repo> <control-root> <B-root> <context-root> <metadata-snapshot> <new-output>");
static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
static int Size(string text) => Encoding.UTF8.GetByteCount(text);
static void Require(bool valid, string code) { if (!valid) throw new InvalidOperationException(code); }
var output = Path.GetFullPath(args[5]); Require(!File.Exists(output), "budget-audit-output-exists");
var root = Path.Combine(args[0], "artifacts/web-pdf-semantic-diagnostic");
var bManifestBytes = File.ReadAllBytes(Path.Combine(root, "p7.interpretation-preflight.v2.json"));
var contextManifestBytes = File.ReadAllBytes(Path.Combine(root, "p7.page-context-preflight.v1.json"));
Require(Hash(bManifestBytes) == "348312e37a9e92295e0928ef13376c6b2fd266a54bc2dfea4a7d62aa89b1d76e", "budget-B-preflight-hash");
Require(Hash(contextManifestBytes) == "d7cdfd5ccebcfd2c771f66184f51e7781682c10ca270557806a5d4998bbb7b91", "budget-context-preflight-hash");
using var bManifest = JsonDocument.Parse(bManifestBytes); using var contextManifest = JsonDocument.Parse(contextManifestBytes);
var metadataBytes = File.ReadAllBytes(args[4]); using var metadata = JsonDocument.Parse(metadataBytes);
var model = metadata.RootElement.GetProperty("data");
Require(model.GetProperty("id").GetString() == "qwen/qwen3.7-flash", "budget-metadata-model-mismatch");
var endpoints = model.GetProperty("endpoints").EnumerateArray().Where(x => x.GetProperty("tag").GetString() == "alibaba")
    .Select(x => new { name = x.GetProperty("name").GetString(), contextLength = x.GetProperty("context_length").GetInt32(),
        maxPromptTokens = x.GetProperty("max_prompt_tokens").GetInt32(), maxCompletionTokens = x.GetProperty("max_completion_tokens").GetInt32() }).ToArray();
Require(endpoints.Length > 0, "budget-no-qualified-provider-metadata");
var composer = new OpenRouterQwen37InferenceRequestComposer();
var rows = new List<object>();
foreach (var call in bManifest.RootElement.GetProperty("plannedRequests").EnumerateArray())
{
    var name = call.GetProperty("name").GetString()!;
    var controlDirectory = name == "F1" ? "20261008-f1-01" : name == "G2A" ? "20261008-g2a-01" : "20261008-h2c-01/" + name[4..];
    var controlBytes = File.ReadAllBytes(Path.Combine(args[1], controlDirectory, "provider-body.json"));
    var bBytes = File.ReadAllBytes(Path.Combine(args[2], name + ".provider-body.json"));
    Require(Hash(controlBytes) == call.GetProperty("controlProviderBodySha256").GetString() &&
        Hash(bBytes) == call.GetProperty("providerBodySha256").GetString(), "budget-frozen-body-hash");
    using var control = JsonDocument.Parse(controlBytes); using var b = JsonDocument.Parse(bBytes);
    var controlSystem = control.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
    var controlUser = control.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
    var bSystem = b.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
    var bUser = b.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
    Require(Hash(composer.Build(bSystem, bUser, 32768)) == Hash(bBytes), "budget-composer-parity");
    using var user = JsonDocument.Parse(bUser);
    const string marker = "This is a versioned qualification interpretation treatment";
    var split = bSystem.IndexOf(marker, StringComparison.Ordinal); Require(split >= 0, "budget-contract-marker-missing");
    var semanticPrefixBytes = Size(bSystem[..split]);
    var interpretationContractBytes = Size(bSystem[split..]);
    Require(semanticPrefixBytes + interpretationContractBytes == Size(bSystem), "budget-system-partition-invalid");
    var storeEvidenceBytes = Size(user.RootElement.GetProperty("sourceEvidence").GetRawText());
    var stageInputBytes = Size(user.RootElement.GetProperty("stageInput").GetRawText());
    rows.Add(new { name, arm = "CONTROL_FROZEN", providerBodySha256 = Hash(controlBytes), providerBodyUtf8Bytes = controlBytes.Length,
        systemPromptUtf8Bytes = Size(controlSystem), userMessageUtf8Bytes = Size(controlUser),
        totalDecodedMessageUtf8Bytes = Size(controlSystem) + Size(controlUser), deltaProviderBodyVsControlBytes = 0,
        inputTokens = (int?)null, billingTokens = (int?)null });
    rows.Add(new { name, arm = "B_FROZEN", providerBodySha256 = Hash(bBytes), providerBodyUtf8Bytes = bBytes.Length,
        systemPromptUtf8Bytes = Size(bSystem), userMessageUtf8Bytes = Size(bUser),
        totalDecodedMessageUtf8Bytes = Size(bSystem) + Size(bUser), semanticStageInstructionUtf8Bytes = semanticPrefixBytes,
        interpretationReferencesAssertionsAndResponseContractUtf8Bytes = interpretationContractBytes,
        stageInputRawJsonUtf8Bytes = stageInputBytes, sourceEvidenceRawJsonUtf8Bytes = storeEvidenceBytes,
        deltaProviderBodyVsControlBytes = bBytes.Length - controlBytes.Length,
        deltaDecodedMessagesVsControlBytes = Size(bSystem) + Size(bUser) - Size(controlSystem) - Size(controlUser),
        inputTokens = (int?)null, billingTokens = (int?)null });
    foreach (var candidate in contextManifest.RootElement.GetProperty("contextCandidates").EnumerateArray().Where(x => x.GetProperty("name").GetString() == name))
    {
        var scope = candidate.GetProperty("scope").GetString()!;
        var bytes = File.ReadAllBytes(Path.Combine(args[3], name + "_" + scope + ".context.json"));
        Require(Hash(bytes) == candidate.GetProperty("contextSha256").GetString(), "budget-context-hash");
        var draft = JsonNode.Parse(bUser)!.AsObject();
        draft["contextCandidateVersion"] = "P7_PAGE_CONTEXT_ENVELOPE_DRAFT_V1";
        draft["pageEvidenceContext"] = JsonNode.Parse(bytes);
        var draftUser = draft.ToJsonString();
        Require(Hash(Encoding.UTF8.GetBytes(draftUser)) == candidate.GetProperty("draftUserEnvelopeSha256").GetString(), "budget-draft-envelope-hash");
        var draftBody = composer.Build(bSystem, draftUser, 32768);
        rows.Add(new { name, arm = "B_CONTEXT_ADDED_DRAFT_" + scope,
            draftProviderBodySha256 = Hash(draftBody), draftProviderBodyUtf8Bytes = draftBody.Length,
            systemPromptUtf8Bytes = Size(bSystem), draftUserMessageUtf8Bytes = Size(draftUser),
            totalDecodedMessageUtf8Bytes = Size(bSystem) + Size(draftUser), pageContextUtf8Bytes = bytes.Length,
            semanticStageInstructionUtf8Bytes = semanticPrefixBytes,
            interpretationReferencesAssertionsAndResponseContractUtf8Bytes = interpretationContractBytes,
            deltaProviderBodyVsControlBytes = draftBody.Length - controlBytes.Length,
            deltaProviderBodyVsBBytes = draftBody.Length - bBytes.Length,
            deltaDecodedMessagesVsControlBytes = Size(bSystem) + Size(draftUser) - Size(controlSystem) - Size(controlUser),
            additionalContextSystemInstructionsFrozen = false, providerRequestReady = false,
            inputTokens = (int?)null, billingTokens = (int?)null });
    }
}
var artifact = new {
    schemaVersion = "p7-offline-size-budget-audit-1", status = "OFFLINE_SIZE_MEASUREMENT_COMPLETE_TOKEN_GATES_OPEN",
    bPreflightSha256 = Hash(bManifestBytes), contextPreflightSha256 = Hash(contextManifestBytes),
    metadataSnapshotSha256 = Hash(metadataBytes), metadataRepresentation = "DECODED_HTTP_JSON_SNAPSHOT_UTF8_FILE_NOT_COMPRESSED_WIRE_BODY",
    metadata = new { id = model.GetProperty("id").GetString(), family = model.GetProperty("architecture").GetProperty("tokenizer").GetString(), endpoints },
    gates = new { EXACT_TOKENIZER_MAPPING = "NOT_ESTABLISHED", TOKEN_BUDGET_MEASUREMENT = "NOT_ESTABLISHED",
        metadataLimitsAreNotMeasuredTokens = true, bytesAreNotClaimedAsTokenUpperBound = true,
        proxyTokenizerUsed = false, inferenceCalls = 0, sourceSentExternally = false,
        goldRead = false, runtimeChanged = false, frozenRequestsChanged = false, productionPromotion = "LOCKED" },
    measurements = rows,
    measurementMeaning = new {
        systemAndUserSizes = "DECODED_MESSAGE_CONTENT_UTF8_BYTES",
        providerBodySize = "SERIALIZED_JSON_BODY_UTF8_BYTES_INCLUDING_ESCAPING_AND_ENVELOPE",
        systemContractPartition = "DISJOINT_PREFIX_AND_OUTPUT_INSTRUCTION_SUFFIX_SUM_TO_SYSTEM_SIZE",
        userSubcomponents = "RAW_JSON_COMPONENT_BYTES_NOT_A_DISJOINT_TOTAL_INCLUDING_FRAMING",
        interpretationReferences = "OUTPUT_INSTRUCTIONS_SIZE_NO_ACTUAL_MODEL_REFERENCES_EXIST_BEFORE_INFERENCE",
        draftBodies = "ENGINEERING_SIZE_RECONSTRUCTION_NOT_FROZEN_RUNNABLE_TREATMENTS" }
};
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var artifactBytes = JsonSerializer.SerializeToUtf8Bytes(artifact, new JsonSerializerOptions { WriteIndented = true });
using (var stream = new FileStream(output, FileMode.CreateNew)) { stream.Write(artifactBytes); stream.Flush(true); }
Console.WriteLine(JsonSerializer.Serialize(new { output, sha256 = Hash(artifactBytes), measurements = rows.Count }));
