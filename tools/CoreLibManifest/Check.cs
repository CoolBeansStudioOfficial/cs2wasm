using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

// CoreLibManifest check <runtime-root> <manifest> [extra-source-dir]: compiles the manifest as a corlib (no references)
// and summarizes the errors (what the runtime layer still has to provide).
static class Check
{
    public static CSharpCompilation Compile(string root, string manifest, string? extra, string? exclude = null)
    {
        var lines = File.ReadAllLines(manifest);
        var defines = lines.Where(l => l.StartsWith("define ")).Select(l => l[7..].Trim()).ToArray();
        var files = lines.Where(l => l.StartsWith("file ")).Select(l => Path.Combine(root, l[5..].Trim())).ToList();
        if (extra != null) files.AddRange(Directory.GetFiles(extra, "*.cs", SearchOption.AllDirectories)
            .Where(f => exclude == null || !string.Equals(Path.GetFullPath(f), Path.GetFullPath(exclude), StringComparison.OrdinalIgnoreCase)));
        var po = new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: defines);
        var trees = files.AsParallel().AsOrdered().Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), po, f)).ToList();
        return CSharpCompilation.Create("System.Private.CoreLib", trees, [],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Disable));
    }

    public static int Run(string root, string manifest, string? extra)
    {
        var comp = Compile(root, manifest, extra);
        var trees = comp.SyntaxTrees.ToList();
        var errs = comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Console.WriteLine($"{trees.Count} files, {errs.Count} errors");
        foreach (var g in errs.GroupBy(e => e.Id).OrderByDescending(g => g.Count()).Take(15)) Console.WriteLine($"  {g.Key} x{g.Count()}  e.g. {g.First().GetMessage()}");
        var names = errs.Where(e => e.Id is "CS0246" or "CS0234" or "CS0103" or "CS0117")
            .Select(e => Regex.Match(e.GetMessage(), "'([^']+)'").Groups[1].Value).GroupBy(x => x).OrderByDescending(g => g.Count()).Take(60);
        foreach (var g in errs.Where(e => e.Id != "CS8795").GroupBy(e => Path.GetFileName(e.Location.SourceTree?.FilePath ?? "?")).OrderByDescending(g => g.Count()).Take(40))
            Console.WriteLine($"  file {g.Key} x{g.Count()}  {g.First().Id} {g.First().GetMessage()}");
        if (Environment.GetEnvironmentVariable("CHECK_ALL") == "1") foreach (var e in errs) Console.WriteLine(e.ToString());
        Console.WriteLine("missing names: " + string.Join(", ", names.Select(g => g.Key + " x" + g.Count())));
        return 0;
    }
}
