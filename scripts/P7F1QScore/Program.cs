using System.Text;

// P7-F1Q post-freeze scorers (Issue #5). Each opens Gold only after its raw manifest is frozen and pinned.
//   <plan-v1.json> <capture-root> <raw-manifest-sha256> <pilot-gold-dir> <d3-raw-dir> <new-score.json>
//   heldout <plan.json> <capture-root> <raw-manifest-sha256> <gold-freeze.json> <gold-freeze-sha256> <page-selection.json> <contamination-audit.json> <new-score.json>
//   v3 <plan-v3.json> <v3-root> <v3-manifest-sha256> <v1-root> <pilot-gold-dir> <d3-raw-dir> <full-source-dir> <new-score.json>
byte[] bytes; string output;
if (args.Length == 9 && args[0] == "v3")
{
    output = args[8];
    if (File.Exists(output)) throw new InvalidOperationException("SCORE_EXISTS");
    bytes = F1QScorerV3.Run(args[1], args[2], args[3], args[4], args[5], args[6], args[7]);
}
else if (args.Length == 9 && args[0] == "heldout")
{
    output = args[8];
    if (File.Exists(output)) throw new InvalidOperationException("SCORE_EXISTS");
    bytes = F1QHeldoutScorer.Run(args[1], args[2], args[3], args[4], args[5], args[6], args[7]);
}
else if (args.Length == 6)
{
    output = args[5];
    if (File.Exists(output)) throw new InvalidOperationException("SCORE_EXISTS");
    bytes = F1QScorer.Run(args[0], args[1], args[2], args[3], args[4]);
}
else throw new ArgumentException("see header comment for usage");
using (var file = new FileStream(output, FileMode.CreateNew)) file.Write(bytes);
Console.WriteLine(Encoding.UTF8.GetString(bytes)[..Math.Min(400, bytes.Length)]);
