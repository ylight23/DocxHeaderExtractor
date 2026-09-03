using System.Reflection;
using System.Runtime.Loader;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Cli;

/// <summary>
/// Explicit bridge for evaluation-only legacy projections. The normal CLI extraction path never
/// loads this plugin. Evaluation commands may provide DHX_EVAL_ASSEMBLY, or use the adjacent
/// source-tree build output during local development.
/// </summary>
internal static class EvaluationProjectionBridge
{
    private const string PolicyTypeName = "DocxHeaderExtractor.Core.Eval.PdfLegacyValidatedOutputPolicy";
    private const string AssemblyName = "DocxHeaderExtractor.Eval.dll";

    public static IReadOnlyList<HeadingRecord> ProjectDocumentOutline(
        IReadOnlyList<HeadingRecord> headings,
        IReadOnlyList<PdfValidatedStructure> structures)
    {
        ArgumentNullException.ThrowIfNull(headings);
        ArgumentNullException.ThrowIfNull(structures);
        var policy = LoadEvaluationAssembly().GetType(PolicyTypeName, throwOnError: true)!;
        var method = policy.GetMethod(
            "ProjectDocumentOutline",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            [typeof(IReadOnlyList<HeadingRecord>), typeof(IReadOnlyList<PdfValidatedStructure>)],
            modifiers: null);
        if (method is null)
            throw new InvalidOperationException("Eval plugin thiếu PdfLegacyValidatedOutputPolicy.ProjectDocumentOutline.");

        return (IReadOnlyList<HeadingRecord>)(method.Invoke(null, [headings, structures]) ??
            throw new InvalidOperationException("Eval plugin trả kết quả rỗng."));
    }

    private static Assembly LoadEvaluationAssembly()
    {
        var configured = Environment.GetEnvironmentVariable("DHX_EVAL_ASSEMBLY");
        var path = !string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(configured)
            : FindAdjacentDevelopmentBuild();
        if (path is null || !File.Exists(path))
            throw new FileNotFoundException(
                "Không tìm thấy Eval plugin. Chỉ các lệnh evaluation mới cần " +
                "DHX_EVAL_ASSEMBLY trỏ tới DocxHeaderExtractor.Eval.dll.", path);

        return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }

    private static string? FindAdjacentDevelopmentBuild()
    {
        var baseDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = baseDirectory.Parent?.Parent?.Name;
        var sourceRoot = baseDirectory.Parent?.Parent?.Parent?.Parent?.Parent;
        if (configuration is null || sourceRoot is null) return null;

        return Path.Combine(sourceRoot.FullName, "DocxHeaderExtractor.Eval", "bin", configuration,
            "net9.0", AssemblyName);
    }
}
