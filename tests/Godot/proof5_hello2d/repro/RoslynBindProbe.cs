// A desk-sized stand-in for the self-hosted compiler's binding (docs/AUDIT.md A3.5, "Test 088 through the self-hosted
// compiler"): RoslynGodotProbe.cs with several source directories at once (with '|'), the bodies of only the files
// whose path has the seventh argument in it bound, and the operations and their types kept, as cs2wasmc keeps them.
// On CoreLib's sources from the cache with System/Text bound, the branch claude/load-flakes-1's runtime binds in 6.5 s
// against 5.8 s with its switches off, and CS2WASM_DROP_STATS=1 shows its extra checks (2 minutes to build):
//   CS2WASM_NO_THREADS=1 cs2wasmc RoslynBindProbe.cs -r Microsoft.CodeAnalysis.dll -r Microsoft.CodeAnalysis.CSharp.dll
//     --max-pages 65536 -o probe.wasm
//   node host/run.mjs probe.wasm main -- "<CoreLib src>|<dir of a program>" <dir of reference assemblies> 0 1 x System/Text
// (paths as the module sees them: host/fs.mjs; mode "drop": the operations not kept).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

static class Probe
{
    static List<object> s_kept = new();
    static Dictionary<ITypeSymbol, int> s_types = new(SymbolEqualityComparer.Default);
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var args = Environment.GetCommandLineArgs();
        string src = args[1], refs = args[2];
        int first = args.Length > 3 ? int.Parse(args[3]) : 0, count = args.Length > 4 ? int.Parse(args[4]) : int.MaxValue;
        var files = src.Split('|').SelectMany(s => Directory.EnumerateFiles(s, "*.cs", SearchOption.AllDirectories)).OrderBy(f => f, StringComparer.Ordinal).ToList(); string only = args.Length > 6 ? args[6] : "";
        var options = new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: new[] { "GODOT", "GODOT4", "GODOT_WEB", "NET", "NET8_0_OR_GREATER" });
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), options, f)).ToList();
        var references = Directory.EnumerateFiles(refs, "*.dll").Select(f => (MetadataReference)MetadataReference.CreateFromFile(f)).ToList();
        var compilation = CSharpCompilation.Create("GodotSharp", trees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));
        Console.WriteLine($"probe: {files.Count} files parsed");
        // (a fifth argument: "nodes", only walk the syntax; "loop", the trees by index, no LINQ)
        string mode = args.Length > 5 ? args[5] : "";
        int bodies = 0, done = 0;
        if (mode == "nodes")
        {
            for (int i = first; i < trees.Count && i < first + (long)count; i++)
                foreach (var node in trees[i].GetRoot().DescendantNodes()) bodies++;
            Console.WriteLine($"probe: done, {bodies} nodes");
            return;
        }
        IEnumerable<SyntaxTree> chosen = only.Length > 0 ? trees.Where(t => t.FilePath.Contains(only)).ToList() : trees.Skip(first).Take(count); var swb = System.Diagnostics.Stopwatch.StartNew();
        if (mode == "loop") { var list = new List<SyntaxTree>(); for (int i = first; i < trees.Count && i < first + (long)count; i++) list.Add(trees[i]); chosen = list; }
        foreach (var tree in chosen)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                SyntaxNode? body = node switch
                {
                    BaseMethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody,
                    AccessorDeclarationSyntax a => (SyntaxNode?)a.Body ?? a.ExpressionBody,
                    PropertyDeclarationSyntax p => p.ExpressionBody,
                    _ => null,
                };
                if (body == null) continue;
                var op = model.GetOperation(body);
                if (op != null && mode != "drop")
                {
                    s_kept.Add(op);
                    foreach (var d in op.Descendants()) { s_kept.Add(d); if (d.Type != null && !s_types.ContainsKey(d.Type)) s_types[d.Type] = s_types.Count; }
                }
                bodies++;
            }
            if (++done % 50 == 0) Console.WriteLine($"probe: {first + done} files, {bodies} bodies");
        }
        Console.WriteLine($"probe: done, {done} files, {bodies} bodies, binding {swb.ElapsedMilliseconds} ms");
    }
}
