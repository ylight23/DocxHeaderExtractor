using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.App;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Provider;
using DocxHeaderExtractor.HeadingAgent.Placement;

/// <summary>
/// Opt-in paid qualification: split-mode PLACE on one previously captured packet, once per named variant (reasoning, typography,
/// reason). Each variant changes one thing; SELECT and GROUP are not re-run. Capture validates the saved evidence.
/// </summary>
static class CapturedPlaceVariantsTrial
{
    static readonly Dictionary<string, PlacementVariant> Variants = new(StringComparer.Ordinal)
    {
        ["reasoning"] = new(ReasoningEffort: "low"),
        ["typography"] = new(TypographyAsEvidence: true),
        ["reason"] = new(AskReason: true),
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 5 || !double.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var maximum) ||
            !double.IsFinite(maximum) || maximum <= 0 || maximum > 1 || args[4..].Any(v => v != "outline" && !Variants.ContainsKey(v)))
            throw new ArgumentException("place-variants-captured <private-capture-directory> <placement-request.json> <max-usd:0..1> <variant...: reasoning|typography|reason|outline>");
        var working = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (working is not null && !Directory.Exists(Path.Combine(working.FullName, ".git")) && !File.Exists(Path.Combine(working.FullName, ".git"))) working = working.Parent;
        if (working is null) throw new InvalidOperationException("CAPTURE_REPOSITORY_ROOT_REQUIRED");
        var settings = new DiagnosticCaptureSettings(args[1], working.FullName);
        var bytes = await File.ReadAllBytesAsync(Path.GetFullPath(args[2]));
        var request = JsonSerializer.Deserialize<PlacementRequest>(bytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("PLACEMENT_REQUEST_INVALID");
        if (request.Evidence?.Source is null || request.Ordered.Count == 0) throw new InvalidDataException("PLACEMENT_SOURCE_OR_HEADINGS_MISSING");
        var provider = OpenRouterProvider.FromEnvironment();
        if (!provider.HasKey) throw new InvalidOperationException("OPENROUTER_API_KEY_MISSING");
        var capture = settings.Start(provider.CreateRoute().ApiKey) ?? throw new InvalidOperationException("CAPTURE_REQUIRED");
        var budget = new RunResourceBudget(maximum, 40);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var access = new ModelAccess(provider.CreateRoute().ApiKey!, () => deadline.Token.ThrowIfCancellationRequested());
        var summarized = false;
        var unsuccessful = 0;
        try
        {
            capture.Record("identity.json", new { inputSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), variants = args[4..], maxUsd = maximum,
                mode = "split", note = "One run per variant on the same saved input; no SELECT or GROUP calls." });
            using (budget.Enter())
                foreach (var name in args[4..])
                {
                    var placer = name == "outline" ? capture.OutlinePlacement(name) : capture.Placement(name, wholeDocument: false, Variants[name]);
                    var proposal = await placer.ProposeAsync(request, access, deadline.Token);
                    if (proposal.LogicalCalls == 0 || proposal.Failures.Count > 0) unsuccessful++;
                }
            var summary = capture.Summary();
            summarized = true;
            if (summary.MissingResponses > 0 || summary.MissingPlacementResults > 0 || summary.MissingAttemptMetadata > 0 ||
                summary.UnattributedResponses > 0 || summary.PlacementWireCalls < summary.PlacementCalls)
                throw new InvalidDataException("DIAGNOSTIC_CAPTURE_INCOMPLETE");
            Console.WriteLine($"Returned {args.Length - 4} proposals, {unsuccessful} with failures or no model calls; confirmed USD {budget.Observe().ConfirmedUsd}.");
            return unsuccessful == 0 ? 0 : 2;
        }
        finally { if (!summarized) capture.Summary(); }
    }
}
