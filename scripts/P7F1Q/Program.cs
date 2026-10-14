using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Source.Common;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.V6Qualification.P7;

// P7-F1Q runner, reduced on 2026-10-14 to the V6 heading-agent experiment (Issue #9: D1-D4.12 preflights, plans, canaries and runs).
// The earlier F1Q / held-out / DIRECT-F1 V1-V5 modes were removed with their qualification code; see git history.
if (args.Length < 1) throw new ArgumentException("mode required (see README)");
switch (args[0])
{
    case "preflight-v6":
        // Issue #9 D1 preflight (offline): preflight-v6 <docs.json> <out-dir>. No provider call.
        if (args.Length != 3) throw new ArgumentException("preflight-v6 <docs.json> <out-dir>");
        V6Preflight.Run(args[1], args[2]);
        break;
    case "preflight-d2":
        // Issue #9 D2 preflight (offline): preflight-d2 <docs.json> <out-dir>. Hierarchical run per shard size. No provider call.
        if (args.Length is not (3 or 4) || (args.Length == 4 && args[3] != "retrieve")) throw new ArgumentException("preflight-d2 <docs.json> <out-dir> [retrieve]");
        V6PreflightD2.Run(args[1], args[2], args.Length == 4);
        break;
    case "c0-pin":
        // Gate C0 (D3.4): c0-pin <repo-root> <artifact-dir>. Writes the pins for document 073. No network call.
        if (args.Length != 3) throw new ArgumentException("c0-pin <repo-root> <artifact-dir>");
        C0Replay.Pin(args[1], args[2]);
        break;
    case "c0-replay":
        // Gate C0 (D3.4): c0-replay <repo-root> <artifact-dir> <safe-root>. Refuses when the pins differ. No network call.
        if (args.Length != 4) throw new ArgumentException("c0-replay <repo-root> <artifact-dir> <safe-root>");
        C0Replay.Replay(args[1], args[2], args[3]);
        break;
    case "d4-size-audit":
        // Issue #9 D4 (offline): d4-size-audit <repo-root> <artifact-dir>. Sizes the DIRECT-F1 requests for RFC 9113. No network call.
        if (args.Length != 3) throw new ArgumentException("d4-size-audit <repo-root> <artifact-dir>");
        C0Replay.D4SizeAudit(args[1], args[2]);
        break;
    case "d43-size-audit":
        // Issue #9 D4.3 (offline): d43-size-audit <repo-root> <artifact-dir>. Sizes the DIRECT-F1 V2 requests for RFC 9113. No network call.
        if (args.Length != 3) throw new ArgumentException("d43-size-audit <repo-root> <artifact-dir>");
        C0Replay.D43SizeAudit(args[1], args[2]);
        break;
    case "d44-universe-check":
        // Issue #9 D4.4 (offline): d44-universe-check <repo-root> <artifact-dir>. Rebuilds PDF Gold universes and compares. No network call.
        if (args.Length != 3) throw new ArgumentException("d44-universe-check <repo-root> <artifact-dir>");
        C0Replay.D44UniverseCheck(args[1], args[2]);
        break;
    case "d44-plan":
        // Issue #9 D4.4 (offline): d44-plan <repo-root> <artifact-dir>. Unit-size study and task A/B/C requests. No network call.
        if (args.Length != 3) throw new ArgumentException("d44-plan <repo-root> <artifact-dir>");
        C0Replay.D44Plan(args[1], args[2]);
        break;
    case "d45-plan":
        // Issue #9 D4.5 (offline): d45-plan <repo-root> <artifact-dir>. Writes the pinned plan and the dry-run report. No network call.
        if (args.Length != 3) throw new ArgumentException("d45-plan <repo-root> <artifact-dir>");
        D45Plan.Run(args[1], args[2]);
        break;
    case "d410-plan":
        // Issue #9 D4.10 (offline): d410-plan <repo-root> <artifact-dir>. Pinned canary plan and per-arm dry-run. No network call.
        if (args.Length != 3) throw new ArgumentException("d410-plan <repo-root> <artifact-dir>");
        D410Plan.Plan(args[1], args[2]);
        break;
    case "d410-run":
        // Issue #9 D4.10: d410-run <repo-root> <plan> <confirmed-plan-sha> <authorization> <safe-root>. Sends requests; needs an APPROVED authorization.
        if (args.Length != 6) throw new ArgumentException("d410-run <repo-root> <plan> <confirmed-plan-sha> <authorization> <safe-root>");
        D410Plan.Live(args[1], args[2], args[3], args[4], args[5]);
        break;
    case "d411-plan":
        // Issue #9 D4.11 (offline): d411-plan <repo-root> <artifact-dir>. Pinned tool-calling canary plan and scripted dry-run. No network call.
        if (args.Length != 3) throw new ArgumentException("d411-plan <repo-root> <artifact-dir>");
        D411Plan.Plan(args[1], args[2]);
        break;
    case "d411-run":
        // Issue #9 D4.11: d411-run <repo-root> <plan> <confirmed-plan-sha> <authorization> <safe-root>. Sends requests; needs an APPROVED authorization.
        if (args.Length != 6) throw new ArgumentException("d411-run <repo-root> <plan> <confirmed-plan-sha> <authorization> <safe-root>");
        D411Plan.Live(args[1], args[2], args[3], args[4], args[5]);
        break;
    case "d411h-plan":
        // Issue #9 D4.11h (offline): d411h-plan <repo-root> <artifact-dir>. Pinned prefetch-canary plan and scripted dry-run. No network call.
        if (args.Length != 3) throw new ArgumentException("d411h-plan <repo-root> <artifact-dir>");
        D411hPlan.Plan(args[1], args[2]);
        break;
    case "d411h-run":
        // Issue #9 D4.11h: d411h-run <repo-root> <plan> <confirmed-plan-sha> <authorization> <safe-root>. Sends requests; needs an APPROVED authorization.
        if (args.Length != 6) throw new ArgumentException("d411h-run <repo-root> <plan> <confirmed-plan-sha> <authorization> <safe-root>");
        D411hPlan.Live(args[1], args[2], args[3], args[4], args[5]);
        break;
    case "d411i-plan":
        // Issue #9 D4.11i (offline): d411i-plan <repo-root> <artifact-dir> <authority-id>. Pinned repeat-canary plan for one document and a scripted dry-run. No network call.
        if (args.Length != 4) throw new ArgumentException("d411i-plan <repo-root> <artifact-dir> <authority-id>");
        D411iPlan.Plan(args[1], args[2], args[3]);
        break;
    case "d411i-run":
        // Issue #9 D4.11i: d411i-run <repo-root> <plan> <confirmed-plan-sha> <authorization> <safe-root>. Sends requests; needs an APPROVED authorization.
        if (args.Length != 6) throw new ArgumentException("d411i-run <repo-root> <plan> <confirmed-plan-sha> <authorization> <safe-root>");
        D411iPlan.Live(args[1], args[2], args[3], args[4], args[5]);
        break;
    case "d411j-plan":
        // Issue #9 D4.11j (offline): d411j-plan <repo-root> <artifact-dir> <document.pdf>. Pinned owner-document trial plan and a scripted dry-run. No network call; no line text is written.
        if (args.Length < 4) throw new ArgumentException("d411j-plan <repo-root> <artifact-dir> <document.pdf> [fixed|layout|layout96] [jsonall] [geometry] [unwrap] [single] [factsuse] [reps=N] [segments=N] [english|main|maintools|mainplain|mainrep|mainrep2] [toolrounds] [toolfacts] [toolfactsanalysis] [factsv2|factsv3] [promptv2] [nolayout] [retry1|retries=N] [repair|repairall] [nocache|cache3]");
        {
            var opts = args.Skip(4).ToHashSet();
            var segPolicy = opts.Contains("layout96") ? P7F1V6Universe.LayoutSegmentation96 : opts.Contains("layout") ? P7F1V6Universe.LayoutSegmentation : P7F1V6Universe.FixedSegmentation;
            var repsArg = opts.FirstOrDefault(o => o.StartsWith("reps=", StringComparison.Ordinal));
            var segsArg = opts.FirstOrDefault(o => o.StartsWith("segments=", StringComparison.Ordinal));
            var known = new HashSet<string> { "fixed", "layout", "layout96", "jsonall", "geometry", "unwrap", "single", "factsuse", "english", "main", "maintools", "mainplain", "mainrep", "mainrep2", "toolrounds", "toolfacts", "toolfactsanalysis", "factsv2", "factsv3", "promptv2", "nolayout", "retry1", "repair", "repairall", "nocache", "cache3" };
            if (opts.Any(o => !known.Contains(o) && o != repsArg && o != segsArg && !o.StartsWith("retries=", StringComparison.Ordinal))) throw new ArgumentException("unknown d411j-plan option");
            D411jPlan.Plan(args[1], args[2], args[3], segPolicy, opts.Contains("jsonall"), opts.Contains("geometry"), opts.Contains("unwrap"), opts.Contains("single"), opts.Contains("factsuse"),
                repsArg is null ? P7F1V6DirectRepeatCanary.Reps : int.Parse(repsArg[5..]), segsArg is null ? 0 : int.Parse(segsArg[9..]),
                opts.Contains("mainrep2") ? P7F1V6DirectD45.InstructionEnglishMainRepresentativeLines : opts.Contains("mainrep") ? P7F1V6DirectD45.InstructionEnglishMainRepresentative : opts.Contains("mainplain") ? P7F1V6DirectD45.InstructionEnglishMainPlain : opts.Contains("maintools") ? P7F1V6DirectD45.InstructionEnglishMainTools : opts.Contains("main") ? P7F1V6DirectD45.InstructionEnglishMain : opts.Contains("english") ? P7F1V6DirectD45.InstructionEnglishAll : P7F1V6DirectD45.Instruction, opts.Contains("toolrounds"), opts.Contains("toolfacts"), opts.Contains("toolfactsanalysis"), opts.Contains("factsv3") ? 3 : opts.Contains("factsv2") ? 2 : 1, opts.Contains("promptv2"), opts.Contains("nolayout"), opts.FirstOrDefault(o => o.StartsWith("retries=", StringComparison.Ordinal)) is { } rt ? int.Parse(rt[8..]) : opts.Contains("retry1") ? 1 : 0,
                opts.Contains("repair") || opts.Contains("repairall"), opts.Contains("repairall") ? -1 : 1, !opts.Contains("nocache"), opts.Contains("cache3"));   // D4.12s: two prompt-cache marks by default; cache3 adds the history mark
        }
        break;
    case "d411j-run":
        // Issue #9 D4.11j: d411j-run <repo-root> <plan> <confirmed-plan-sha> <authorization> <safe-root> <document.pdf>. Sends requests; needs an APPROVED authorization.
        if (args.Length != 7) throw new ArgumentException("d411j-run <repo-root> <plan> <confirmed-plan-sha> <authorization> <safe-root> <document.pdf>");
        D411jPlan.Live(args[1], args[2], args[3], args[4], args[5], args[6]);
        break;
    case "d411-publish":
        // Issue #9 D4.11: d411-publish <plan> <out-dir> <run-dir> [<run-dir>...]. Copies the safe-store runs of a consumed plan into the repo. No network call.
        if (args.Length < 4) throw new ArgumentException("d411-publish <plan> <out-dir> <run-dir> [<run-dir>...]");
        D411Plan.Publish(args[1], args[2], args.Skip(3).Where(a => a != "--gzip").ToArray(), args.Contains("--gzip"));
        break;
    case "d49-dryrun":
        // Issue #9 D4.9 (offline): d49-dryrun <repo-root> <artifact-dir>. Variants A/B/C with scripted models. No network call.
        if (args.Length != 3) throw new ArgumentException("d49-dryrun <repo-root> <artifact-dir>");
        D49DryRun.Run(args[1], args[2]);
        break;
    case "d49-replay":
        // Issue #9 D4.9 (offline): d49-replay <repo-root> <run-dir-with-raw> <out>. Representation-change replay. No network call.
        if (args.Length != 4) throw new ArgumentException("d49-replay <repo-root> <run-dir> <out>");
        D49DryRun.Replay(args[1], args[2], args[3]);
        break;
    case "d45-run":
        // Issue #9 D4.5: d45-run <repo-root> <plan-path> <confirmed-plan-sha> <authorization-path> <safe-root>. Sends requests; needs an APPROVED authorization.
        if (args.Length != 6) throw new ArgumentException("d45-run <repo-root> <plan-path> <confirmed-plan-sha> <authorization-path> <safe-root>");
        D45Plan.Live(args[1], args[2], args[3], args[4], args[5]);
        break;
    case "d45-safe-root":
        // Issue #9 D4.5a: d45-safe-root <repo-root> <safe-root> prepare|check. Creates (prepare) and checks the safe root. No network call.
        if (args.Length != 4 || args[3] is not ("prepare" or "check")) throw new ArgumentException("d45-safe-root <repo-root> <safe-root> prepare|check");
        D45Plan.SafeRoot(args[1], args[2], args[3] == "prepare");
        break;
    case "d45-authorization-draft":
        // Issue #9 D4.5a: d45-authorization-draft <plan-path> <safe-root> <out-path>. Writes a PENDING draft. No network call.
        if (args.Length != 4) throw new ArgumentException("d45-authorization-draft <plan-path> <safe-root> <out-path>");
        D45Plan.AuthorizationDraft(args[1], args[2], args[3]);
        break;
    case "d45-approve":
        // Issue #9 D4.5a: d45-approve <draft> <out> <approved-by> <approval-text> <data-collection-decision> accept-open-items|no. Only on the user's explicit approval.
        if (args.Length != 7) throw new ArgumentException("d45-approve <draft> <out> <approved-by> <approval-text> <data-collection-decision> accept-open-items|no");
        D45Plan.Approve(args[1], args[2], args[3], args[4], args[5], args[6] == "accept-open-items");
        break;
    case "d45-preview":
        // Issue #9 D4.5 (offline): d45-preview <repo-root> <scratch-dir>. The same simulations on uncommitted code; writes outside the repo; not a plan.
        if (args.Length != 3) throw new ArgumentException("d45-preview <repo-root> <scratch-dir>");
        if (Path.GetFullPath(args[2]).StartsWith(Path.GetFullPath(args[1]), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("PREVIEW_MUST_BE_OUTSIDE_REPO");
        D45Plan.Run(args[1], args[2], preview: true);
        break;
    case "d4-canary-artifact":
        // Issue #9 D4.1 (offline): d4-canary-artifact <artifact-dir> <generated-at-commit>. Writes the canary T1 body and audit. No network call.
        if (args.Length != 3) throw new ArgumentException("d4-canary-artifact <artifact-dir> <generated-at-commit>");
        C0Replay.D4CanaryArtifact(args[1], args[2]);
        break;
    case "d4-canary-send":
        // Issue #9 D4.2: d4-canary-send <artifact-dir> <confirmed-sha>. Sends the single pinned canary POST. Needs OPENROUTER_API_KEY. Not run in D4.2.
        if (args.Length != 3) throw new ArgumentException("d4-canary-send <artifact-dir> <confirmed-sha>");
        C0Replay.D4CanarySend(args[1], args[2]);
        break;
    case "d31-snapshot":
        // Issue #9 D3.1 candidate (offline): d31-snapshot <repo-root>. Writes the manifest and the candidate document. No network call.
        if (args.Length != 2) throw new ArgumentException("d31-snapshot <repo-root>");
        D31Snapshot.Run(args[1]);
        break;
    default: throw new ArgumentException("UNKNOWN_MODE");
}
