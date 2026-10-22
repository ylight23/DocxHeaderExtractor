namespace DocxHeaderExtractor.Cli.Commands;

/// <summary>The files the command-line inputs name: files, directories (top level) and wildcards.</summary>
internal static class Inputs
{
    public static List<string> Expand(IEnumerable<string> inputs)
    {
        var files = new List<string>();
        foreach (var input in inputs)
        {
            if (File.Exists(input)) { files.Add(Path.GetFullPath(input)); continue; }

            if (Directory.Exists(input))
            {
                files.AddRange(Directory.EnumerateFiles(input, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(IsSupported));
                continue;
            }

            var dir = Path.GetDirectoryName(input);
            var pattern = Path.GetFileName(input);
            dir = string.IsNullOrEmpty(dir) ? Directory.GetCurrentDirectory() : dir;

            if (Directory.Exists(dir) && (pattern.Contains('*') || pattern.Contains('?')))
                files.AddRange(Directory.EnumerateFiles(dir, pattern).Where(IsSupported));
            else
                Console.Error.WriteLine($"Bỏ qua (không tồn tại): {input}");
        }
        return files.Distinct().OrderBy(f => f).ToList();

        // A glob filter, not a routing decision: the lane is still chosen from the bytes. This only
        // decides which files a wildcard expands to, and leaving .pdf out of it meant the PDF lane was
        // unreachable from the CLI however the file was named.
        static bool IsSupported(string f) =>
            Path.GetExtension(f).ToLowerInvariant()
                is ".docx" or ".docm" or ".doc" or ".rtf" or ".odt" or ".pdf";
    }
}
