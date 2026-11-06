using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.App;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Provider;

/// <summary>Opt-in paid qualification of PLACE on one identical, previously captured packet.</summary>
static class CapturedPlaceAbTrial
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 5 || !int.TryParse(args[3], out var repetitions) || repetitions is < 1 or > 5 ||
            !double.TryParse(args[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var maximum) ||
            !double.IsFinite(maximum) || maximum <= 0 || maximum > 1)
            throw new ArgumentException("place-ab-captured <private-capture-directory> <placement-request.json> <runs-per-mode:1..5> <max-usd:0..1>");
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
        var budget = new RunResourceBudget(maximum, 80);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var access = new ModelAccess(provider.CreateRoute().ApiKey!, () => deadline.Token.ThrowIfCancellationRequested());
        var summarized = false;
        var unsuccessful = 0;
        try
        {
            capture.Record("identity.json", new { inputSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), repetitions,
                maxUsd = maximum, note = "Same saved input for both modes; no SELECT or GROUP calls; evaluate all resulting trees." });
            using (budget.Enter())
                foreach (var mode in new[] { "split", "whole" })
                    for (var i = 1; i <= repetitions; i++)
                    {
                        var proposal = await capture.Placement($"{mode}-{i:00}", mode == "whole").ProposeAsync(request, access, deadline.Token);
                        if (proposal.LogicalCalls == 0 || proposal.Failures.Count > 0) unsuccessful++;
                    }
            var summary = capture.Summary();
            summarized = true;
            if (summary.MissingResponses > 0 || summary.MissingPlacementResults > 0 || summary.MissingAttemptMetadata > 0 ||
                summary.UnattributedResponses > 0 || summary.PlacementWireCalls < summary.PlacementCalls)
                throw new InvalidDataException("DIAGNOSTIC_CAPTURE_INCOMPLETE");
            Console.WriteLine($"Returned {2 * repetitions} proposals, {unsuccessful} with failures or no model calls; confirmed USD {budget.Observe().ConfirmedUsd}. Inspect protocol errors in private evidence.");
            return unsuccessful == 0 ? 0 : 2;
        }
        finally { if (!summarized) capture.Summary(); }
    }
}
