using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Section 2 of the per-segment recovery mission: explicit segment lifecycle states. A segment's
/// status is always stored on its record -- never inferred from artifact presence alone.
/// </summary>
public static class SegmentRecoveryState
{
    public const string Pending = "PENDING";
    public const string Running = "RUNNING";
    public const string Success = "SUCCESS";
    public const string TransientRetry = "TRANSIENT_RETRY";
    public const string WorkloadSplitRequired = "WORKLOAD_SPLIT_REQUIRED";
    public const string SplitParent = "SPLIT_PARENT";
    public const string FailedTerminal = "FAILED_TERMINAL";

    /// <summary>Section 9: a FAILED_TERMINAL leaf whose runtime contract (e.g. the visible-context
    /// halo policy) has changed underneath it since it was terminated. It is no longer validly
    /// "terminal" under the old, now-corrected contract, so it gets exactly one bounded re-attempt
    /// under the new contract before it can become FAILED_TERMINAL again.</summary>
    public const string StaleTerminal = "STALE_TERMINAL";
}

/// <summary>A half-open character slice [CharStart, CharEnd) of one source occurrence, identified
/// by its ordinal position in the document's ordered occurrence list. Segment owned/visible
/// ranges are lists of these atoms; children of a split exactly partition their parent's atoms.</summary>
public sealed record SegmentAtom(int OccurrenceIndex, int CharStart, int CharEnd)
{
    public int Length => CharEnd - CharStart;
}

/// <summary>One node in the recovery tree (section 2). Carries everything the mission asks for:
/// identity, lineage, depth, owned/visible ranges, attempt count, and hashes once resolved.</summary>
public sealed class SegmentNode
{
    public required string SegmentId { get; init; }
    public string? ParentSegmentId { get; init; }
    public required int Depth { get; init; }
    public required IReadOnlyList<SegmentAtom> Owned { get; init; }
    // Section 8/9: Visible is mutable (Owned never is) so a resumed tree can reconcile a
    // FAILED_TERMINAL leaf's context window against a corrected halo policy without changing the
    // node's identity (SegmentId is derived from Owned only, never Visible).
    public required IReadOnlyList<SegmentAtom> Visible { get; set; }
    public string Status { get; set; } = SegmentRecoveryState.Pending;
    public int Attempts { get; set; }
    public int HistoricalAttempts { get; set; }
    public List<string> FailureHistory { get; } = [];
    public string? FailureClass { get; set; }
    public string? RequestHash { get; set; }
    public string? ResponseHash { get; set; }
    public List<string> ChildSegmentIds { get; } = [];
    public int OwnedCharacters => Owned.Sum(a => a.Length);
}

/// <summary>
/// Sections 1, 4, 5, 6: the recovery tree itself. The root is the whole-document owned range
/// (the full-context attempt). On a workload-shape failure, exactly that node is split into two
/// children that exactly partition its owned range -- never the whole rung. A successful leaf is
/// frozen (its status becomes SUCCESS) and is never revisited by any later split elsewhere in the
/// tree. Recursion continues, independently per branch, until every leaf is terminal.
/// </summary>
public sealed class SegmentRecoveryTree
{
    private readonly Dictionary<string, SegmentNode> _nodes = new(StringComparer.Ordinal);

    public string DocumentId { get; }
    public int MinimumSegmentCharacters { get; }

    /// <summary>Section 8: the floor for VISIBLE context characters surrounding a leaf's OWNED
    /// range. Ownership (who may emit a heading) and visibility (how much surrounding context the
    /// model gets to reason with) are independent -- a tiny owned range must not collapse the
    /// visible window down to itself and one immediate neighbor.</summary>
    public int MinimumVisibleContextCharacters { get; }
    public string RootSegmentId { get; }
    public IReadOnlyList<int> OccurrenceCharacterLengths { get; }

    public SegmentRecoveryTree(
        string documentId, IReadOnlyList<int> occurrenceCharacterLengths, int minimumSegmentCharacters = 1_500,
        int minimumVisibleContextCharacters = 4_000)
    {
        DocumentId = documentId;
        OccurrenceCharacterLengths = occurrenceCharacterLengths;
        MinimumSegmentCharacters = Math.Max(1, minimumSegmentCharacters);
        MinimumVisibleContextCharacters = Math.Max(0, minimumVisibleContextCharacters);
        var rootAtoms = occurrenceCharacterLengths.Select((len, idx) => new SegmentAtom(idx, 0, len)).ToArray();
        RootSegmentId = ComputeSegmentId(documentId, rootAtoms);
        _nodes[RootSegmentId] = new SegmentNode
        {
            SegmentId = RootSegmentId,
            ParentSegmentId = null,
            Depth = 0,
            Owned = rootAtoms,
            Visible = rootAtoms,
        };
    }

    public SegmentNode Get(string segmentId) => _nodes[segmentId];
    public bool TryGet(string segmentId, out SegmentNode node) => _nodes.TryGetValue(segmentId, out node!);
    public IReadOnlyCollection<SegmentNode> AllNodes => _nodes.Values;

    /// <summary>A leaf is any node that has never been split (its status is not SPLIT_PARENT).
    /// This includes SUCCESS, FAILED_TERMINAL, and still-pending/retryable leaves.</summary>
    public IReadOnlyList<SegmentNode> Leaves => _nodes.Values.Where(n => n.Status != SegmentRecoveryState.SplitParent).ToArray();

    public IReadOnlyList<SegmentNode> PendingLeaves => Leaves
        .Where(n => n.Status is SegmentRecoveryState.Pending or SegmentRecoveryState.TransientRetry
            or SegmentRecoveryState.WorkloadSplitRequired or SegmentRecoveryState.StaleTerminal)
        .ToArray();

    public bool IsComplete => Leaves.Count > 0 && Leaves.All(n => n.Status == SegmentRecoveryState.Success);
    public bool HasFailedTerminal => Leaves.Any(n => n.Status == SegmentRecoveryState.FailedTerminal);

    /// <summary>Section 7/18(C): a leaf that is already SUCCESS is frozen -- it must never be
    /// re-run. Callers check this before attempting (or re-attempting after a resume) a leaf.</summary>
    public bool IsFrozenSuccess(string segmentId) => _nodes[segmentId].Status == SegmentRecoveryState.Success;

    public void MarkRunning(string segmentId) => _nodes[segmentId].Status = SegmentRecoveryState.Running;

    public void MarkSuccess(string segmentId, string requestHash, string responseHash)
    {
        var node = _nodes[segmentId];
        node.Status = SegmentRecoveryState.Success;
        node.RequestHash = requestHash;
        node.ResponseHash = responseHash;
    }

    /// <summary>Section 3: applies the existing retry policy to classify a failure. Transient
    /// transport noise moves the node to TRANSIENT_RETRY (same shape may be re-attempted, bounded
    /// by the caller); a workload-shape failure (timeout/output-limit) moves it to
    /// WORKLOAD_SPLIT_REQUIRED so the caller knows the next and only next action is Split, never
    /// another identical attempt. Anything non-recoverable becomes FAILED_TERMINAL directly.</summary>
    public string RecordFailure(string segmentId, string failureClass)
    {
        var node = _nodes[segmentId];
        node.Attempts++;
        node.FailureClass = failureClass;
        var retryClass = ReasoningRecoveryRetryPolicy.Classify(failureClass);
        node.Status = retryClass switch
        {
            ReasoningRecoveryRetryClass.TransientTransportRetry => SegmentRecoveryState.TransientRetry,
            ReasoningRecoveryRetryClass.WorkloadShapeRecovery => SegmentRecoveryState.WorkloadSplitRequired,
            _ => SegmentRecoveryState.FailedTerminal,
        };
        return node.Status;
    }

    public void MarkFailedTerminal(string segmentId, string failureClass)
    {
        var node = _nodes[segmentId];
        node.Status = SegmentRecoveryState.FailedTerminal;
        node.FailureClass = failureClass;
    }

    /// <summary>Section 6: whether this node can still be split, or is already at the minimum
    /// safe segment floor (one source occurrence, itself no larger than the character floor).</summary>
    public bool CanSplit(SegmentNode node) => node.OwnedCharacters > MinimumSegmentCharacters;

    /// <summary>Section 4/5: split exactly the failed segment. Deterministic: multiple whole
    /// occurrences split at the source boundary nearest the character-weighted halfway point;
    /// a single oversized occurrence is character-sliced in half. Halo occurrences (their full
    /// text, not just owned chars) are attached to Visible without touching Owned, so children's
    /// owned ranges still exactly partition the parent's -- coverage identical, zero overlap.</summary>
    public (SegmentNode Left, SegmentNode Right) Split(string segmentId, int haloOccurrences = 1, bool characterWeighted = false)
    {
        var parent = _nodes[segmentId];
        if (!CanSplit(parent))
            throw new InvalidOperationException($"segment-recovery-floor-reached:{segmentId}");

        IReadOnlyList<SegmentAtom> leftOwned, rightOwned;
        var running = 0;
        if (parent.Owned.Count > 1 && !characterWeighted)
        {
            var half = parent.OwnedCharacters / 2;
            var cut = 1;
            for (var i = 0; i < parent.Owned.Count; i++)
            {
                running += parent.Owned[i].Length;
                if (running >= half) { cut = i + 1; break; }
            }
            cut = Math.Clamp(cut, 1, parent.Owned.Count - 1);
            var boundaryLeft = parent.Owned.Take(cut).ToArray();
            var boundaryRight = parent.Owned.Skip(cut).ToArray();

            leftOwned = boundaryLeft;
            rightOwned = boundaryRight;
        }
        else
        {
            var target = Math.Max(1, parent.OwnedCharacters / 2);
            running = 0;
            var splitIndex = 0;
            for (; splitIndex < parent.Owned.Count; splitIndex++)
            {
                var next = running + parent.Owned[splitIndex].Length;
                if (target <= next) break;
                running = next;
            }
            var atom = parent.Owned[Math.Min(splitIndex, parent.Owned.Count - 1)];
            var offset = Math.Clamp(target - running, 1, Math.Max(1, atom.Length - 1));
            var mid = atom.CharStart + offset;
            leftOwned = parent.Owned.Take(splitIndex).Append(atom with { CharEnd = mid }).ToArray();
            rightOwned = parent.Owned.Skip(splitIndex + 1).Prepend(atom with { CharStart = mid }).ToArray();
        }

        var left = new SegmentNode
        {
            SegmentId = ComputeSegmentId(DocumentId, leftOwned), ParentSegmentId = segmentId, Depth = parent.Depth + 1,
            Owned = leftOwned, Visible = ExpandHalo(leftOwned, haloOccurrences),
        };
        var right = new SegmentNode
        {
            SegmentId = ComputeSegmentId(DocumentId, rightOwned), ParentSegmentId = segmentId, Depth = parent.Depth + 1,
            Owned = rightOwned, Visible = ExpandHalo(rightOwned, haloOccurrences),
        };
        _nodes[left.SegmentId] = left;
        _nodes[right.SegmentId] = right;
        parent.ChildSegmentIds.Add(left.SegmentId);
        parent.ChildSegmentIds.Add(right.SegmentId);
        parent.Status = SegmentRecoveryState.SplitParent;
        return (left, right);
    }

    /// <summary>Repairs a persisted pre-midpoint tree whose first split was made only at an
    /// occurrence boundary (for example 150/60K). It is safe only when no descendant leaf has
    /// succeeded: successful owned ranges are frozen and are never discarded. The failed
    /// subtree is replaced by one deterministic midpoint split of the original parent, retaining
    /// the parent's failure evidence while removing the giant halo workload.</summary>
    public bool RebalanceGrossSplitIfUnsuccessful(string segmentId, int haloOccurrences = 1)
    {
        if (!_nodes.TryGetValue(segmentId, out var parent) || parent.Status != SegmentRecoveryState.SplitParent)
            return false;

        var descendantIds = DescendantIds(parent).ToArray();
        var leaves = descendantIds.Select(id => _nodes[id]).Where(n => n.Status != SegmentRecoveryState.SplitParent).ToArray();
        if (leaves.Any(n => n.Status == SegmentRecoveryState.Success)) return false;
        var childSizes = parent.ChildSegmentIds.Select(id => _nodes[id].OwnedCharacters).ToArray();
        if (childSizes.Length < 2 || childSizes.Max() <= parent.OwnedCharacters * 0.75) return false;

        foreach (var id in descendantIds) _nodes.Remove(id);
        parent.ChildSegmentIds.Clear();
        parent.Status = SegmentRecoveryState.WorkloadSplitRequired;
        Split(segmentId, haloOccurrences, characterWeighted: true);
        return true;
    }

    private IEnumerable<string> DescendantIds(SegmentNode parent)
    {
        foreach (var childId in parent.ChildSegmentIds)
        {
            yield return childId;
            if (!_nodes.TryGetValue(childId, out var child)) continue;
            foreach (var descendant in DescendantIds(child)) yield return descendant;
        }
    }

    /// <summary>Section 8: expands OWNED into a VISIBLE window. At least <paramref name="haloOccurrences"/>
    /// neighboring occurrences are always included on each side (when available) -- but a tiny owned
    /// range does not stop there: each side keeps pulling further neighboring occurrences until it
    /// has contributed at least <see cref="MinimumVisibleContextCharacters"/> of halo, or the
    /// document boundary is reached. This is a pure function of (owned, haloOccurrences,
    /// MinimumVisibleContextCharacters, OccurrenceCharacterLengths) -- deterministic and safe to
    /// recompute on any resume to check whether a persisted node's Visible is still current.</summary>
    public IReadOnlyList<SegmentAtom> ExpandHalo(IReadOnlyList<SegmentAtom> owned, int haloOccurrences)
    {
        if (owned.Count == 0) return owned;
        var minOcc = owned[0].OccurrenceIndex;
        var maxOcc = owned[^1].OccurrenceIndex;

        var left = new List<SegmentAtom>();
        var leftOcc = minOcc - 1;
        while (leftOcc >= 0 && (minOcc - 1 - leftOcc < haloOccurrences || left.Sum(a => a.Length) < MinimumVisibleContextCharacters))
        {
            left.Insert(0, new SegmentAtom(leftOcc, 0, OccurrenceCharacterLengths[leftOcc]));
            leftOcc--;
        }

        var right = new List<SegmentAtom>();
        var rightOcc = maxOcc + 1;
        while (rightOcc < OccurrenceCharacterLengths.Count && (rightOcc - maxOcc - 1 < haloOccurrences || right.Sum(a => a.Length) < MinimumVisibleContextCharacters))
        {
            right.Add(new SegmentAtom(rightOcc, 0, OccurrenceCharacterLengths[rightOcc]));
            rightOcc++;
        }

        var visible = new List<SegmentAtom>(left.Count + owned.Count + right.Count);
        visible.AddRange(left);
        visible.AddRange(owned);
        visible.AddRange(right);
        return visible;
    }

    private static bool VisibleAtomsEqual(IReadOnlyList<SegmentAtom> a, IReadOnlyList<SegmentAtom> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    /// <summary>Section 9: recomputes a FAILED_TERMINAL leaf's Visible window under the tree's
    /// current halo policy. If the corrected window differs from what this leaf was actually
    /// evaluated against, the old FAILED_TERMINAL verdict no longer reflects the current runtime
    /// contract -- the leaf is promoted to STALE_TERMINAL (eligible for exactly one bounded
    /// re-attempt) instead of being silently reused or silently re-run forever. A leaf whose
    /// recomputed window is unchanged is left exactly as it was (a genuine, still-current
    /// terminal failure is never disturbed).</summary>
    public bool ReconcileTerminalContract(string segmentId, int haloOccurrences)
    {
        var node = _nodes[segmentId];
        if (node.Status != SegmentRecoveryState.FailedTerminal) return false;
        var recomputed = ExpandHalo(node.Owned, haloOccurrences);
        if (VisibleAtomsEqual(node.Visible, recomputed)) return false;
        node.Visible = recomputed;
        node.HistoricalAttempts += node.Attempts;
        if (!string.IsNullOrWhiteSpace(node.FailureClass)) node.FailureHistory.Add(node.FailureClass!);
        node.Status = SegmentRecoveryState.StaleTerminal;
        node.Attempts = 0;
        return true;
    }

    /// <summary>Execution-only contract change (for example a pinned provider or a larger
    /// bounded deadline) invalidates old terminal transport verdicts, but never invalidates a
    /// frozen SUCCESS leaf. The old evidence remains in the persisted history via its prior
    /// attempt count/failure class in the campaign report.</summary>
    public int ReopenFailedTerminalsForExecutionChange()
    {
        var reopened = 0;
        foreach (var node in _nodes.Values.Where(n => n.Status == SegmentRecoveryState.FailedTerminal))
        {
            node.HistoricalAttempts += node.Attempts;
            if (!string.IsNullOrWhiteSpace(node.FailureClass)) node.FailureHistory.Add(node.FailureClass!);
            node.Status = SegmentRecoveryState.StaleTerminal;
            node.Attempts = 0;
            reopened++;
        }
        return reopened;
    }

    /// <summary>Section 10: coverage=1.0, overlap=0, no unresolved leaf -- verified against the
    /// document's real occurrence-length table before any union is permitted.</summary>
    public bool VerifyFullCoverage()
    {
        var leaves = Leaves;
        if (leaves.Count == 0 || leaves.Any(n => n.Status != SegmentRecoveryState.Success)) return false;
        var byOccurrence = new Dictionary<int, List<(int Start, int End)>>();
        foreach (var atom in leaves.SelectMany(n => n.Owned))
        {
            if (!byOccurrence.TryGetValue(atom.OccurrenceIndex, out var list)) byOccurrence[atom.OccurrenceIndex] = list = [];
            list.Add((atom.CharStart, atom.CharEnd));
        }
        for (var occ = 0; occ < OccurrenceCharacterLengths.Count; occ++)
        {
            if (OccurrenceCharacterLengths[occ] == 0) continue;
            if (!byOccurrence.TryGetValue(occ, out var ranges)) return false;
            ranges.Sort();
            var cursor = 0;
            foreach (var (start, end) in ranges)
            {
                if (start != cursor) return false; // gap or overlap against the previous range
                cursor = end;
            }
            if (cursor != OccurrenceCharacterLengths[occ]) return false;
        }
        return true;
    }

    /// <summary>A serializable snapshot of one node, used to persist/restore the whole tree across
    /// process invocations (section 7/14: a resume must reconstruct prior splits, not just reuse
    /// individual leaf artifacts, or every resume would re-attempt the root from scratch).</summary>
    public sealed record SegmentNodeSnapshot(
        string SegmentId, string? ParentSegmentId, int Depth,
        IReadOnlyList<SegmentAtom> Owned, IReadOnlyList<SegmentAtom> Visible,
        string Status, int Attempts, string? FailureClass, string? RequestHash, string? ResponseHash,
        IReadOnlyList<string> ChildSegmentIds, int HistoricalAttempts = 0, IReadOnlyList<string>? FailureHistory = null);

    public IReadOnlyList<SegmentNodeSnapshot> ExportSnapshot() => _nodes.Values.Select(n => new SegmentNodeSnapshot(
        n.SegmentId, n.ParentSegmentId, n.Depth, n.Owned, n.Visible, n.Status, n.Attempts, n.FailureClass, n.RequestHash, n.ResponseHash, n.ChildSegmentIds, n.HistoricalAttempts, n.FailureHistory)).ToArray();

    /// <summary>Rebuilds a tree exactly as it stood at export time -- every node's status,
    /// attempts, and hashes are restored verbatim, so SUCCESS leaves are never revisited and
    /// already-split parents are never re-split.</summary>
    public static SegmentRecoveryTree RestoreFromSnapshot(
        string documentId, IReadOnlyList<int> occurrenceCharacterLengths, int minimumSegmentCharacters,
        IReadOnlyList<SegmentNodeSnapshot> snapshot, int minimumVisibleContextCharacters = 4_000)
    {
        var tree = new SegmentRecoveryTree(documentId, occurrenceCharacterLengths, minimumSegmentCharacters, minimumVisibleContextCharacters);
        if (snapshot.Count == 0) return tree;
        tree._nodes.Clear();
        foreach (var s in snapshot)
        {
            var node = new SegmentNode
            {
                SegmentId = s.SegmentId, ParentSegmentId = s.ParentSegmentId, Depth = s.Depth,
                Owned = s.Owned, Visible = s.Visible, Status = s.Status, Attempts = s.Attempts,
                FailureClass = s.FailureClass, RequestHash = s.RequestHash, ResponseHash = s.ResponseHash,
            };
            node.HistoricalAttempts = s.HistoricalAttempts;
            node.FailureHistory.AddRange(s.FailureHistory ?? []);
            node.ChildSegmentIds.AddRange(s.ChildSegmentIds);
            tree._nodes[node.SegmentId] = node;
        }
        return tree;
    }

    public static string ComputeSegmentId(string documentId, IReadOnlyList<SegmentAtom> owned)
    {
        var sb = new StringBuilder();
        sb.Append(documentId).Append('|');
        foreach (var atom in owned) sb.Append(atom.OccurrenceIndex).Append(':').Append(atom.CharStart).Append('-').Append(atom.CharEnd).Append(';');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
        return $"seg-{hash[..16]}";
    }
}

/// <summary>Section 10: a semantic proposal after conflict-aware union across leaves. A heading
/// that appears identically in two leaves' halo overlap dedupes to one row. If two leaves
/// disagree on role for the same exact span, existence is preserved plus explicit conflict
/// metadata -- never a silent pick-one-and-discard.</summary>
public sealed record UnionedProposal(
    string ProposalKey,
    string SourceId,
    int Start,
    int End,
    string Role,
    string SourceSegmentId,
    bool HasRoleConflict,
    IReadOnlyList<string> ConflictingRoles);

public static class SegmentProposalUnion
{
    public static IReadOnlyList<UnionedProposal> Union(
        IEnumerable<(string SegmentId, string SourceId, int Start, int End, string Role)> raw)
    {
        return raw
            .GroupBy(p => $"{p.SourceId}:{p.Start}:{p.End}", StringComparer.Ordinal)
            .Select(g =>
            {
                var first = g.First();
                var roles = g.Select(x => x.Role).Distinct(StringComparer.Ordinal).ToArray();
                return new UnionedProposal(g.Key, first.SourceId, first.Start, first.End, first.Role, first.SegmentId, roles.Length > 1, roles);
            })
            .OrderBy(p => p.SourceId, StringComparer.Ordinal)
            .ThenBy(p => p.Start)
            .ToArray();
    }
}

/// <summary>Section 12: the distinct document-level completion states this campaign introduces.
/// A generic "failure" is never reported -- callers pick exactly one of these.</summary>
public static class DocumentCompletionState
{
    public const string FullContextSuccess = "FULL_CONTEXT_SUCCESS";
    public const string SegmentedRecoverySuccess = "SEGMENTED_RECOVERY_SUCCESS";
    public const string SegmentedRecoveryPartialBlocked = "SEGMENTED_RECOVERY_PARTIAL_BLOCKED";
    public const string SemanticCompleteHierarchyBlocked = "SEMANTIC_COMPLETE_HIERARCHY_BLOCKED";
    public const string ProviderUnavailable = "PROVIDER_UNAVAILABLE";
}

/// <summary>Section 13: the Gold firewall as an explicit, testable gate rather than a convention.
/// Gold may not be read until the document's prediction/result/runtime-trace/freeze artifacts
/// have all been written and the document has been marked frozen.</summary>
public sealed class DocumentFreezeGate
{
    private bool _frozen;
    public bool IsFrozen => _frozen;
    public void MarkFrozen() => _frozen = true;

    public void GuardGoldRead()
    {
        if (!_frozen)
            throw new InvalidOperationException("GOLD_READ_BEFORE_FREEZE_FORBIDDEN");
    }
}

/// <summary>Section 7: the identity a leaf artifact is keyed by. On resume, a leaf whose stored
/// key matches exactly is reused verbatim; any mismatch (including a response-hash mismatch
/// discovered after reload) forces a fresh call for that leaf only.</summary>
public sealed record SegmentLeafArtifactKey(
    string DocumentId,
    string SegmentId,
    string SourceSha256,
    string ConfigurationSignature,
    string SegmentPlanHash,
    string Model,
    string ReasoningMode);

public sealed record SegmentLeafArtifact(string SegmentId, string PredictionJson, string RequestHash, string ResponseHash);

/// <summary>Section 7 persistence: one directory per leaf, written immediately on SUCCESS. A
/// resume that finds a leaf directory whose freeze.json key matches the requested key reuses the
/// stored prediction without calling the provider again.</summary>
public static class SegmentLeafPersistence
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static string PlanHash(IReadOnlyList<SegmentAtom> owned) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join(';', owned.Select(a => $"{a.OccurrenceIndex}:{a.CharStart}-{a.CharEnd}")))))[..32].ToLowerInvariant();

    public static string LeafDirectory(string outputRoot, string documentId, string segmentId) =>
        Path.Combine(outputRoot, "documents", documentId, "segments", segmentId);

    public static bool TryLoad(string outputRoot, SegmentLeafArtifactKey key, out SegmentLeafArtifact? artifact)
    {
        artifact = null;
        var dir = LeafDirectory(outputRoot, key.DocumentId, key.SegmentId);
        var freezePath = Path.Combine(dir, "freeze.json");
        var predictionPath = Path.Combine(dir, "prediction.json");
        if (!File.Exists(freezePath) || !File.Exists(predictionPath)) return false;

        using var doc = JsonDocument.Parse(File.ReadAllText(freezePath));
        var root = doc.RootElement;
        if (!MatchesKey(root, key)) return false;

        var predictionJson = File.ReadAllText(predictionPath);
        // Restore the immutable request/response lineage; do not replace the original request
        // hash with a synthetic reuse marker when a SUCCESS leaf is resumed.
        var requestHash = root.TryGetProperty("requestHash", out var req) ? req.GetString() ?? "" : "";
        var responseHash = root.TryGetProperty("responseHash", out var rh) ? rh.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(requestHash) || string.IsNullOrWhiteSpace(responseHash)) return false;
        artifact = new SegmentLeafArtifact(key.SegmentId, predictionJson, requestHash, responseHash);
        return true;
    }

    private static bool MatchesKey(JsonElement root, SegmentLeafArtifactKey key) =>
        Field(root, "documentId") == key.DocumentId &&
        Field(root, "segmentId") == key.SegmentId &&
        Field(root, "sourceSha256") == key.SourceSha256 &&
        Field(root, "configurationSignature") == key.ConfigurationSignature &&
        Field(root, "segmentPlanHash") == key.SegmentPlanHash &&
        Field(root, "model") == key.Model &&
        Field(root, "reasoningMode") == key.ReasoningMode;

    private static string? Field(JsonElement root, string name) => root.TryGetProperty(name, out var v) ? v.GetString() : null;

    public static void Save(
        string outputRoot, SegmentLeafArtifactKey key, string requestManifestJson, string predictionJson,
        string executionJson, string requestHash, string responseHash)
    {
        var dir = LeafDirectory(outputRoot, key.DocumentId, key.SegmentId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "request-manifest.json"), requestManifestJson);
        File.WriteAllText(Path.Combine(dir, "prediction.json"), predictionJson);
        File.WriteAllText(Path.Combine(dir, "execution.json"), executionJson);
        var freezeJson = JsonSerializer.Serialize(new
        {
            documentId = key.DocumentId,
            segmentId = key.SegmentId,
            sourceSha256 = key.SourceSha256,
            configurationSignature = key.ConfigurationSignature,
            segmentPlanHash = key.SegmentPlanHash,
            model = key.Model,
            reasoningMode = key.ReasoningMode,
            requestHash,
            responseHash,
            frozenUtc = DateTimeOffset.UtcNow,
        }, WriteOptions);
        File.WriteAllText(Path.Combine(dir, "freeze.json"), freezeJson);
    }
}
