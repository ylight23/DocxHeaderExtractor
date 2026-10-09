using System.Text;

// P7-F1Q post-freeze scorer (Issue #5). Opens Gold only after the raw manifest is frozen and pinned.
if (args.Length != 6) throw new ArgumentException("<plan.json> <capture-root> <raw-manifest-sha256> <pilot-gold-dir> <d3-raw-dir> <new-score.json>");
if (File.Exists(args[5])) throw new InvalidOperationException("SCORE_EXISTS");
var bytes = F1QScorer.Run(args[0], args[1], args[2], args[3], args[4]);
using (var file = new FileStream(args[5], FileMode.CreateNew)) file.Write(bytes);
Console.WriteLine(Encoding.UTF8.GetString(bytes)[..Math.Min(400, bytes.Length)]);
