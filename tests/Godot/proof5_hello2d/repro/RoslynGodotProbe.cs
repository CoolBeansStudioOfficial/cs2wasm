// Repro (proof 5): what the self-hosted compiler does to GodotSharp's sources when a program references GodotSharp,
// without the rest of cs2wasmc: Roslyn, compiled by cs2wasm, binds every method body of GodotSharp's sources
// (GetOperation, as cs2wasmc's FunctionCompiler does). The self-hosted compiler of 2026-11-20 frees memory still in use
// on this path (docs/AUDIT.md, proof 5). Build it --checked (freed memory poisoned, never reused) with no threads:
//   CS2WASM_NO_THREADS=1 cs2wasmc RoslynGodotProbe.cs -r Microsoft.CodeAnalysis.dll -r Microsoft.CodeAnalysis.CSharp.dll
//     --checked --max-pages 65536 -o probe.wasm
//   node host/run.mjs probe.wasm main -- <dir of GodotSharp's sources> <dir of reference assemblies> [first] [count]
// (paths as the module sees them: host/fs.mjs). Prints how many files and bodies it bound.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

static class Probe
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var args = Environment.GetCommandLineArgs();
        string src = args[1], refs = args[2];
        int first = args.Length > 3 ? int.Parse(args[3]) : 0, count = args.Length > 4 ? int.Parse(args[4]) : int.MaxValue;
        var files = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToList();
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
        IEnumerable<SyntaxTree> chosen = trees.Skip(first).Take(count);
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
                model.GetOperation(body);
                bodies++;
            }
            if (++done % 50 == 0) Console.WriteLine($"probe: {first + done} files, {bodies} bodies");
        }
        Console.WriteLine($"probe: done, {done} files, {bodies} bodies");
    }
}
