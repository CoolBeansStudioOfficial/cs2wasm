using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

// CoreLibManifest check <runtime-root> <upstream-dir> [library]: compiles the libraries (each against those before it)
// and summarizes the errors (what the runtime layer still has to provide). CHECK_ALL=1 prints every error.
static class Check
{
    /// <summary>A library's compilation: the manifest's files, then cs2wasm's own (everything under
    /// upstream-dir/Library), with the libraries before it as references (CoreLib is the corlib).</summary>
    public static CSharpCompilation Compile(string root, string upstream, string lib, IReadOnlyDictionary<string, CSharpCompilation> built, string? exclude = null)
    {
        var dir = Path.Combine(upstream, lib);
        var lines = File.ReadAllLines(Path.Combine(dir, "manifest.txt"));
        var defines = lines.Where(l => l.StartsWith("define ")).Select(l => l[7..].Trim()).ToArray();
        // `root <dir>`: the files are under another source tree of the cache (Roslyn's), beside the runtime's
        var baseDir = lines.FirstOrDefault(l => l.StartsWith("root ")) is { } rl ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar))!, rl[5..].Trim()) : root;
        var files = lines.Where(l => l.StartsWith("file ")).Select(l => Path.Combine(baseDir, l[5..].Trim())).ToList();
        files.AddRange(Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Where(f => exclude == null || !string.Equals(Path.GetFullPath(f), Path.GetFullPath(exclude), StringComparison.OrdinalIgnoreCase)));
        var po = new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: defines);
        var trees = files.AsParallel().AsOrdered().Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), po, f)).ToList();
        var refs = Libraries.All.TakeWhile(l => l != lib).Where(built.ContainsKey).Select(l => (MetadataReference)built[l].ToMetadataReference()).ToList();
        return CSharpCompilation.Create(lib, trees, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                nullableContextOptions: lines.Contains("nullable enable") ? NullableContextOptions.Enable : NullableContextOptions.Disable,
                specificDiagnosticOptions: Suppressed));
    }

    /// <summary>Diagnostics the libraries' own builds do not report as errors.</summary>
    public static readonly Dictionary<string, ReportDiagnostic> Suppressed = new()
    {
        ["CS8826"] = ReportDiagnostic.Suppress,
        // (Roslyn's experimental APIs, used inside Roslyn)
        ["RSEXPERIMENTAL001"] = ReportDiagnostic.Suppress, ["RSEXPERIMENTAL002"] = ReportDiagnostic.Suppress, ["RSEXPERIMENTAL003"] = ReportDiagnostic.Suppress,
        ["RSEXPERIMENTAL004"] = ReportDiagnostic.Suppress, ["RSEXPERIMENTAL005"] = ReportDiagnostic.Suppress, ["RSEXPERIMENTAL006"] = ReportDiagnostic.Suppress,
        ["RSEXPERIMENTAL007"] = ReportDiagnostic.Suppress, ["RSEXPERIMENTAL008"] = ReportDiagnostic.Suppress, ["RSEXPERIMENTAL009"] = ReportDiagnostic.Suppress,
            // (the libraries' experimental APIs, which their projects suppress: ML-KEM, ML-DSA, HPKE...)
            ["SYSLIB5001"] = ReportDiagnostic.Suppress, ["SYSLIB5002"] = ReportDiagnostic.Suppress, ["SYSLIB5003"] = ReportDiagnostic.Suppress, ["SYSLIB5004"] = ReportDiagnostic.Suppress, ["SYSLIB5005"] = ReportDiagnostic.Suppress,
            ["SYSLIB5006"] = ReportDiagnostic.Suppress, ["SYSLIB5007"] = ReportDiagnostic.Suppress, ["SYSLIB5008"] = ReportDiagnostic.Suppress, ["SYSLIB5009"] = ReportDiagnostic.Suppress, ["SYSLIB5010"] = ReportDiagnostic.Suppress,
    };


    public static int Run(string root, string upstream, string? only)
    {
        var built = new Dictionary<string, CSharpCompilation>();
        int total = 0;
        foreach (var lib in Libraries.All)
        {
            var comp = Compile(root, upstream, lib, built);
            built[lib] = comp;
            if (only != null && lib != only) continue;
            var errs = comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            total += errs.Count;
            Console.WriteLine($"{lib}: {comp.SyntaxTrees.Count()} files, {errs.Count} errors");
            foreach (var g in errs.GroupBy(e => e.Id).OrderByDescending(g => g.Count()).Take(15)) Console.WriteLine($"  {g.Key} x{g.Count()}  e.g. {g.First().GetMessage()}");
            foreach (var g in errs.GroupBy(e => Path.GetFileName(e.Location.SourceTree?.FilePath ?? "?")).OrderByDescending(g => g.Count()).Take(25))
                Console.WriteLine($"  file {g.Key} x{g.Count()}  {g.First().Id} {g.First().GetMessage()}");
            if (Environment.GetEnvironmentVariable("CHECK_ALL") == "1") foreach (var e in errs) Console.WriteLine(e.ToString());
            var names = errs.Where(e => e.Id is "CS0246" or "CS0234" or "CS0103" or "CS0117")
                .Select(e => Regex.Match(e.GetMessage(), "'([^']+)'").Groups[1].Value).GroupBy(x => x).OrderByDescending(g => g.Count()).Take(40).ToList();
            if (names.Count > 0) Console.WriteLine("  missing names: " + string.Join(", ", names.Select(g => g.Key + " x" + g.Count())));
        }
        return total == 0 ? 0 : 1;
    }
}
