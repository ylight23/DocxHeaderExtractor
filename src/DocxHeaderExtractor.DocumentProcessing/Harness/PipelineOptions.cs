using DocxHeaderExtractor.Contracts.Chunking;
using DocxHeaderExtractor.Contracts.Docx;

namespace DocxHeaderExtractor.DocumentProcessing.Harness;

public sealed class PipelineOptions
{
    public ExtractionOptions Extraction { get; set; } = new();

    /// <summary>Kích thước chunk đầu ra — thuộc pipeline, không thuộc provider/runtime.</summary>
    public ChunkingOptions Chunking { get; set; } = new();

    /// <summary>Bỏ qua LLM, chỉ dùng luật (nhanh, để đối chiếu).</summary>
    public bool DisableLlm { get; set; }

    public Action<string>? Log { get; set; }

    /// <summary>Shared OCR/VLM evidence store for scanned DOCX files.</summary>
    public string ScanEvidenceRoot { get; set; } = ".dhx-evidence";
    /// <summary>The folder where a built PDF source is kept between runs; the file's bytes and this build of the code are its key. Null builds every time.</summary>
    public string? SourceCacheRoot { get; set; } = ".dhx-evidence/source";
    public int? ScanJobs { get; set; }
    public bool ScanVision { get; set; }
    /// <summary>Reuse a scan unit's selection answer when its first request is unchanged (stored under the evidence root); false asks again.</summary>
    public bool ScanReuseSelection { get; set; } = true;

    /// <summary>One extraction's provider ceiling across every stage, including transport retries and unknown billing.</summary>
    public double MaxProviderCostUsd { get; set; } = 5.0;
    public int MaxProviderAttempts { get; set; } = 2000;
    /// <summary>Admission deadline; cancellation may not cancel an upstream generation or its bill.</summary>
    public TimeSpan? ProviderBudgetDuration { get; set; }
    /// <summary>Null uses the versioned locked-route resource profile; these are operator overrides, not provider discovery.</summary>
    public int? MaxModelRequestBytes { get; set; }
    public int? ModelContextTokens { get; set; }
    public int? MaxModelPromptTokens { get; set; }
    public int? MaxModelOutputTokens { get; set; }
    public int? ContinuationReserveBytes { get; set; }
    public int? ImageTokenAllowance { get; set; }
    /// <summary>Qualification candidate: PDF selection also proposes local heading boundaries.</summary>
    public bool PdfInlineGrouping { get; set; }
    /// <summary>Qualification candidate: try all settled headings in one measured placement request.</summary>
    public bool WholeDocumentPlacement { get; set; }
    /// <summary>Native PDF default: every SELECT must echo the exact source text of its alias.</summary>
    public bool PdfSourceBoundSelection { get; set; } = true;
    /// <summary>Native PDF: attach measured OpenCV page geometry to parser text before request budgeting. Disable for legacy packet parity.</summary>
    public bool PdfRasterGeometryEvidence { get; set; } = true;
    /// <summary>
    /// Native PDF default (owner, 2026-10-26): attach the measured page images to each SELECT request.
    /// The selection instruction remains unchanged.
    /// </summary>
    public bool PdfAdaptiveVisualSelection { get; set; } = true;
    /// <summary>PDF-only hard ceiling for a final SELECT wire body after visual evidence is attached.</summary>
    public int? PdfVisualSelectionMaxRequestBytes { get; set; } = 786432;
    /// <summary>Native PDF default (owner, 2026-10-26): lossless column encoding of raster measurements.</summary>
    public bool PdfCompactGeometry { get; set; } = true;
    /// <summary>Native PDF default (owner, 2026-10-26): compact full-page evidence encoding.</summary>
    public int PdfVisualMaxWidth { get; set; } = 960;
    public int PdfVisualQuality { get; set; } = 50;
    /// <summary>Native PDF default (owner, 2026-10-26): transmit rendered page evidence as grayscale JPEG.</summary>
    public bool PdfVisualGrayscale { get; set; } = true;
    /// <summary>PDF trial: page images as 1-bit black and white PNG (threshold at mid-grey). Takes precedence over grayscale.</summary>
    public bool PdfVisualBilevel { get; set; }
    /// <summary>PDF qualification: each page image shows only the band from its first to its last inked row or parser line box, plus a
    /// bleed; the packet gives the band in full-page coordinates.</summary>
    public bool PdfVisualCropMargins { get; set; }
    /// <summary>Native PDF default (owner, 2026-10-26): size completion by units; retry truncated replies with a bounded larger allowance.</summary>
    public bool PdfDynamicCompletionBudget { get; set; } = true;
    /// <summary>PDF qualification: after selection, ask again as a whole look-alike lines that separate requests decided differently.</summary>
    public bool PdfShapeReview { get; set; }
    /// <summary>Native PDF default (owner, 2026-10-26): scheduler concurrency. The process-wide provider gate also applies.</summary>
    public int? PdfJobs { get; set; } = 8;
    /// <summary>Qualification candidate: bounded model repair for inconsistent proposed ancestor chains.</summary>
    public bool RepairParentChains { get; set; }
    /// <summary>Native PDF default: V12 shared structural policy, formatting evidence and exact source extent.</summary>
    public bool PdfClarifyMembership { get; set; } = true;
    /// <summary>Native PDF default (owner, 2026-10-26): 14 = structural geometry selection units with the V12 instruction.</summary>
    public int PdfMembershipInstructionVersion { get; set; } = 14;
    /// <summary>Qualification candidate: reconsider at most two complete PDF scopes before grouping.</summary>
    public bool PdfBoundaryReview { get; set; }
    /// <summary>Native PDF default: boundary-specific instruction; custom user instructions retain precedence.</summary>
    public bool PdfSeparateGroupingInstruction { get; set; } = true;
    /// <summary>Native PDF default: model-decided source-bound JOIN/SPLIT at every adjacent selected pair.</summary>
    public bool PdfPairwiseGrouping { get; set; } = true;
    /// <summary>Native PDF route only: bounded ancestor-chain repair. Generic RepairParentChains remains an explicit override.</summary>
    public bool PdfRepairParentChains { get; set; } = true;


}
