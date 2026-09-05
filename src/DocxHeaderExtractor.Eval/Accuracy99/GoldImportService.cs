using System.Text.Json;

namespace DocxHeaderExtractor.Eval.Accuracy99;

public static class A99GoldStoreGuard
{
    public static void EnsureDevPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.Contains("holdout-sealed", StringComparison.OrdinalIgnoreCase) ||
            full.Contains("generalization_holdout", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("DEV operation cannot open the sealed holdout store.");
    }

    public static void EnsureHoldoutIsNotReadByDev(IEnumerable<string> paths)
    {
        foreach (var path in paths) EnsureDevPath(path);
    }
}

public static class A99GoldImportService
{
    public static A99GoldImportCoverage ValidateAndImportDev(
        A99ReferenceCampaign campaign,
        string packetRoot,
        string goldRoot,
        string outputRoot)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        A99GoldStoreGuard.EnsureDevPath(goldRoot);
        A99GoldStoreGuard.EnsureDevPath(packetRoot);
        A99GoldStoreGuard.EnsureDevPath(outputRoot);
        var expected = campaign.DevDocuments;
        var errors = new List<string>();
        var validated = new List<(A99CampaignDocument Campaign, A99HumanGoldDocument Gold, string GoldPath)>();

        foreach (var document in expected)
        {
            var packetPath = ResolvePacketPath(packetRoot, document);
            var goldPath = Path.Combine(Path.GetFullPath(goldRoot), document.DocumentId + ".human-gold.json");
            if (!File.Exists(packetPath)) { errors.Add($"packet-missing:{document.DocumentId}"); continue; }
            if (!File.Exists(goldPath)) { errors.Add($"gold-missing:{document.DocumentId}"); continue; }
            try
            {
                var packet = A99ReviewJson.Deserialize<A99ReviewPacket>(File.ReadAllText(packetPath));
                var gold = A99ReviewJson.Deserialize<A99HumanGoldDocument>(File.ReadAllText(goldPath));
                if (!string.Equals(packet.SourceDocumentSha256, document.SourceSha256, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"{document.DocumentId}:packet-source-sha-not-bound-to-campaign");
                    continue;
                }
                if (!string.Equals(packet.PacketSha256, document.PacketSha256, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"{document.DocumentId}:packet-sha-not-bound-to-campaign");
                    continue;
                }
                var validation = A99HumanGoldValidator.Validate(packet, gold);
                if (!validation.IsValid)
                {
                    errors.AddRange(validation.Errors.Select(error => $"{document.DocumentId}:{error}"));
                    continue;
                }
                validated.Add((document, gold, goldPath));
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
            {
                errors.Add($"{document.DocumentId}:gold-unreadable:{ex.GetType().Name}");
            }
        }

        var coverage = new A99GoldImportCoverage
        {
            Status = errors.Count == 0 && validated.Count == expected.Count ? "READY" : "HUMAN_REFERENCE_REQUIRED",
            DevDocumentsExpected = expected.Count,
            DevDocumentsValidated = validated.Count,
            SourceOccurrencesExpected = expected.Sum(x => x.SourceOccurrenceCount),
            SourceOccurrencesValidated = validated.Sum(x => x.Gold.Rows.Count),
            HeadingYes = validated.Sum(x => x.Gold.Rows.Count(row => row.IsHeading == A99ReviewLabel.Yes)),
            HeadingNo = validated.Sum(x => x.Gold.Rows.Count(row => row.IsHeading == A99ReviewLabel.No)),
            HeadingUnsure = validated.Sum(x => x.Gold.Rows.Count(row => row.IsHeading == A99ReviewLabel.Unsure)),
            Errors = errors,
            ProviderCalls = 0,
        };

        Directory.CreateDirectory(outputRoot);
        var entries = validated.Select(item => new
        {
            documentId = item.Campaign.DocumentId,
            documentGroupId = item.Campaign.DocumentGroupId,
            split = item.Campaign.Split,
            familyId = item.Campaign.FamilyId,
            sourcePath = item.Campaign.SourcePath,
            sourceSha256 = item.Campaign.SourceSha256,
            packetSha256 = item.Gold.PacketSha256,
            goldPath = Path.GetRelativePath(outputRoot, item.GoldPath).Replace(Path.DirectorySeparatorChar, '/'),
            goldSha256 = HumanGoldValidator.ComputeSha256(item.GoldPath),
            officialDenominator = A99HumanGoldValidator.OfficialDenominatorCount(item.Gold.Rows),
        }).ToArray();
        var manifest = new
        {
            artifactKind = "a99_dev_reference_manifest",
            schemaVersion = "a99-dev-reference-v1",
            status = coverage.Status,
            source = "validated HUMAN_GOLD only; holdout store not opened",
            entries,
            errors,
            providerCalls = 0,
        };
        File.WriteAllText(Path.Combine(outputRoot, "dev-reference-manifest.v1.json"), A99ReviewJson.Serialize(manifest) + Environment.NewLine);
        File.WriteAllText(Path.Combine(outputRoot, "dev-gold-coverage.v1.json"), A99ReviewJson.Serialize(coverage) + Environment.NewLine);
        return coverage;
    }

    private static string ResolvePacketPath(string packetRoot, A99CampaignDocument document)
    {
        var root = Path.GetFullPath(packetRoot);
        var splitDirectory = document.Split == "DEV" ? "dev" : "holdout-sealed";
        var direct = Path.Combine(root, splitDirectory, document.DocumentId + ".v1.json");
        if (File.Exists(direct)) return direct;
        return Path.Combine(root, document.DocumentId + ".v1.json");
    }

    public static A99EarlyGoldImportCoverage ValidateAndImportEarlyDevV2(
        A99EarlyDevCampaign earlyCampaign,
        string packetRoot,
        string goldRoot,
        string outputRoot)
    {
        ArgumentNullException.ThrowIfNull(earlyCampaign);
        A99GoldStoreGuard.EnsureDevPath(goldRoot);
        A99GoldStoreGuard.EnsureDevPath(packetRoot);
        A99GoldStoreGuard.EnsureDevPath(outputRoot);

        var errors = new List<string>();
        var validated = new List<(A99EarlyDevDocument Document, A99HumanGoldV2Document Gold, string GoldPath)>();
        foreach (var document in earlyCampaign.Documents)
        {
            var packetPath = Path.Combine(Path.GetFullPath(packetRoot), "dev", document.DocumentId + ".v1.json");
            if (!File.Exists(packetPath)) packetPath = Path.Combine(Path.GetFullPath(packetRoot), document.DocumentId + ".v1.json");
            var goldPath = Path.Combine(Path.GetFullPath(goldRoot), document.DocumentId + ".human-gold-v2.json");
            if (!File.Exists(packetPath)) { errors.Add($"packet-missing:{document.DocumentId}"); continue; }
            if (!File.Exists(goldPath)) { errors.Add($"gold-missing:{document.DocumentId}"); continue; }
            try
            {
                var packet = A99ReviewJson.Deserialize<A99ReviewPacket>(File.ReadAllText(packetPath));
                var gold = A99ReviewJson.Deserialize<A99HumanGoldV2Document>(File.ReadAllText(goldPath));
                if (!string.Equals(packet.SourceDocumentSha256, document.SourceSha256, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"{document.DocumentId}:packet-source-sha-not-bound-to-campaign");
                    continue;
                }
                if (!string.Equals(packet.PacketSha256, document.PacketSha256, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(packet.PacketSha256, A99ReviewPacketBuilder.ComputeSha256(packet), StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"{document.DocumentId}:packet-sha-not-bound-to-campaign");
                    continue;
                }
                var validation = A99HumanGoldV2Validator.Validate(packet, gold);
                if (!validation.IsValid)
                {
                    errors.AddRange(validation.Errors.Select(error => $"{document.DocumentId}:{error}"));
                    continue;
                }
                validated.Add((document, gold, goldPath));
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
            {
                errors.Add($"{document.DocumentId}:gold-unreadable:{ex.GetType().Name}");
            }
        }

        var coverage = new A99EarlyGoldImportCoverage
        {
            Status = errors.Count == 0 && validated.Count == earlyCampaign.Documents.Count ? "READY_FOR_BASELINE" : "HUMAN_REFERENCE_REQUIRED",
            DocumentsExpected = earlyCampaign.Documents.Count,
            DocumentsValidated = validated.Count,
            SourceOccurrencesExpected = earlyCampaign.SourceOccurrences,
            SourceOccurrencesValidated = validated.Sum(x => x.Document.SourceOccurrenceCount),
            HeadingPositives = validated.Sum(x => x.Gold.Rows.Count),
            UnsureDocuments = validated.Count(x => x.Gold.UnsureSourceIds.Count > 0),
            Errors = errors,
            ProviderCalls = 0,
        };

        Directory.CreateDirectory(outputRoot);
        var manifest = new
        {
            artifactKind = "a99_early_dev_reference_manifest",
            schemaVersion = "a99-early-dev-reference-v2",
            status = coverage.Status,
            source = "validated positive-set HUMAN_GOLD only; holdout store not opened",
            entries = validated.Select(item => new
            {
                documentId = item.Document.DocumentId,
                documentGroupId = item.Document.DocumentGroupId,
                split = item.Document.Split,
                familyId = item.Document.FamilyId,
                sourcePath = item.Document.SourcePath,
                sourceSha256 = item.Document.SourceSha256,
                packetSha256 = item.Gold.PacketSha256,
                goldPath = Path.GetRelativePath(outputRoot, item.GoldPath).Replace(Path.DirectorySeparatorChar, '/'),
                goldSha256 = HumanGoldValidator.ComputeSha256(item.GoldPath),
                headingPositives = item.Gold.Rows.Count,
            }).ToArray(),
            errors,
            providerCalls = 0,
        };
        File.WriteAllText(Path.Combine(outputRoot, "early-dev-reference-manifest.v2.json"), A99ReviewJson.Serialize(manifest) + Environment.NewLine);
        File.WriteAllText(Path.Combine(outputRoot, "early-dev-gold-coverage.v2.json"), A99ReviewJson.Serialize(coverage) + Environment.NewLine);
        return coverage;
    }
}
