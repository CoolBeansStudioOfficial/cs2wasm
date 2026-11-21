// A stand-in for the self-hosted compiler's run that builds in minutes (tools/roslyn-bind-bench.sh builds and times it
// with cs2wasm and with NativeAOT-LLVM, interleaved). It uses Roslyn as the compiler's front end does on hello world:
// System.Private.CoreLib's sources read and parsed as Library.BuildOne parses them (the manifest's files from the
// dotnet/runtime cache with its defines and edits, then cs2wasm's own files), one compilation of them, then method
// bodies bound as the Driver discovers them: from a few roots, each body's operation tree (SemanticModel.GetOperation,
// one model per tree as Driver.ModelFor keeps them) walked, and the methods it calls, constructs, reads as properties
// or takes as delegates queued in turn, up to a number of bodies.
// usage: <program> <src/Cs2Wasm/Upstream dir> <runtime sources dir> [bodies (default 400)]
// Prints each phase's time and a check line (counts that both builds must print alike).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Operations;

static class BindBench
{
    public static void Run(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("usage: <Upstream dir> <runtime sources dir> [bodies]"); return; }
        string upstream = args[0], runtime = args[1];
        int maxBodies = args.Length > 2 ? int.Parse(args[2]) : 400;
        var total = Stopwatch.StartNew();
        var sw = Stopwatch.StartNew();

        // the manifest, as Library.BuildOne reads it
        const string lib = "System.Private.CoreLib";
        var lines = File.ReadAllText(Path.Combine(upstream, lib, "manifest.txt")).Split('\n').Select(l => l.Trim(' ', '\r')).ToList();
        var defines = lines.Where(l => l.StartsWith("define ")).Select(l => l[7..]).ToArray();
        var files = lines.Where(l => l.StartsWith("file ")).Select(l => l[5..]).ToList();
        var edits = lines.Where(l => l.StartsWith("edit\t")).Select(l => l.Split('\t')).Where(p => p.Length >= 4)
            .GroupBy(p => p[1]).ToDictionary(g => g.Key, g => g.Select(p => (p[2], p[3])).ToList());
        var po = new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: defines);
        var trees = new List<SyntaxTree>(files.Count + 64);
        long chars = 0;
        foreach (var f in files)
        {
            var text = File.ReadAllText(Path.Combine(runtime, f));
            if (edits.TryGetValue(f, out var es))
                foreach (var (from, to) in es) text = text.Replace(from, to);
            chars += text.Length;
            trees.Add(CSharpSyntaxTree.ParseText(text, po, path: "dotnet/runtime/" + f));
        }
        // cs2wasm's own files of the library, in the order of their resource names
        var ownDir = Path.Combine(upstream, lib);
        var own = Directory.GetFiles(ownDir, "*.cs", SearchOption.AllDirectories)
            .Select(p => (Name: p.Substring(ownDir.Length + 1).Replace('\\', '/'), Path: p)).OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        foreach (var (name, path) in own)
        {
            var text = File.ReadAllText(path);
            chars += text.Length;
            trees.Add(CSharpSyntaxTree.ParseText(text, po, path: "cs2wasm/upstream/" + lib + "/" + name));
        }
        long parseMs = sw.ElapsedMilliseconds;

        // the compilation, as Library's options make it
        sw.Restart();
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, concurrentBuild: false,
            nullableContextOptions: NullableContextOptions.Disable,
            specificDiagnosticOptions: new Dictionary<string, ReportDiagnostic>
            {
                ["CS0436"] = ReportDiagnostic.Suppress, ["CS0626"] = ReportDiagnostic.Suppress, ["CS8826"] = ReportDiagnostic.Suppress,
            });
        var comp = CSharpCompilation.Create(lib, trees, [], options);
        var queue = new Queue<IMethodSymbol>();
        var seen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        void Add(IMethodSymbol? m)
        {
            if (m == null) return;
            m = m.OriginalDefinition;
            if (m.PartialImplementationPart is { } impl) m = impl;
            if (m.DeclaringSyntaxReferences.Length == 0 || !seen.Add(m)) return;
            queue.Enqueue(m);
        }
        // roots: what Console.WriteLine(42) reaches first in the corlib
        (string Type, string Member)[] roots =
        [
            ("System.Number", "Int32ToDecStr"), ("System.Number", "FormatInt32"), ("System.String", "Concat"),
            ("System.Text.StringBuilder", "Append"), ("System.Collections.Generic.Dictionary`2", "TryInsert"),
            ("System.Collections.Generic.List`1", "Add"), ("System.IO.TextWriter", "WriteLine"),
            ("System.Text.UTF8Encoding", "GetBytes"), ("System.Threading.Tasks.Task", "Run"),
            ("System.AppContext", "Setup"), ("System.Environment", "GetEnvironmentVariable"),
        ];
        int types = 0;
        foreach (var (t, m) in roots)
        {
            if (comp.GetTypeByMetadataName(t) is not { } type) continue;
            types++;
            foreach (var s in type.GetMembers(m).OfType<IMethodSymbol>()) Add(s);
        }
        long declareMs = sw.ElapsedMilliseconds;

        // the bodies, as the Driver binds them
        sw.Restart();
        var models = new Dictionary<SyntaxTree, SemanticModel>();
        int bodies = 0;
        long ops = 0, calls = 0, typed = 0;
        while (queue.Count > 0 && bodies < maxBodies)
        {
            var m = queue.Dequeue();
            foreach (var sr in m.DeclaringSyntaxReferences)
            {
                if (!models.TryGetValue(sr.SyntaxTree, out var model)) models[sr.SyntaxTree] = model = comp.GetSemanticModel(sr.SyntaxTree);
                if (model.GetOperation(sr.GetSyntax()) is not { } body) continue;
                bodies++;
                foreach (var op in body.Descendants())
                {
                    ops++;
                    if (op.Type is { SpecialType: SpecialType.None, TypeKind: TypeKind.Class }) typed++;
                    switch (op)
                    {
                        case IInvocationOperation i: calls++; Add(i.TargetMethod); break;
                        case IObjectCreationOperation o: calls++; Add(o.Constructor); break;
                        case IPropertyReferenceOperation p: Add(p.Property.GetMethod); Add(p.Property.SetMethod); break;
                        case IMethodReferenceOperation r: Add(r.Method); break;
                    }
                }
            }
        }
        long bindMs = sw.ElapsedMilliseconds;
        Console.WriteLine($"parse {parseMs} ms ({trees.Count} files, {chars} chars), declare {declareMs} ms, bind {bindMs} ms, total {total.ElapsedMilliseconds} ms");
        Console.WriteLine($"check files={trees.Count} chars={chars} roots={types} bodies={bodies} queued={queue.Count} ops={ops} calls={calls} typed={typed} models={models.Count}");
    }
}
