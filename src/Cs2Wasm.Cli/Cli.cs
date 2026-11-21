using System.Diagnostics;
using Cs2Wasm;
using Microsoft.CodeAnalysis;

namespace Cs2Wasm;

// cs2wasm command-line wrapper (for tests; embedders call Cs2WasmCompiler.Compile in process).
// usage: cs2wasm <file.cs | dir>... -o out.wasm [--boundary table.json] [--map out.wasm.map]
//               [--section name=file] [--memory-pages N] [--max-pages N] [--no-opt] [-r ref.dll] [--watch]
//               [-a generators.dll] [--property Name=value] [--plugin Type]...
// --plugin: a plugin pass (SPEC section 7.1) runs over the program, by its type's name. The .NET build takes an
// assembly's path too (Program.cs loads it); the self-hosted compiler has its plugins compiled in (tools/selfhost.mjs)
// -a: source generators run over the program first (with the build properties --property gives them)
// --watch: builds, then builds again whenever a .cs file of the inputs changes (the library stays loaded: a rebuild
// skips its parsing and setup)
// (Cli.Run is the whole of it, so the self-hosted compiler runs the same code: src/Cs2Wasm.Wasm)
public static class Cli
{
    /// <summary>The plugin passes this command line knows, chosen with --plugin by type name: those of the assemblies
    /// the .NET build was given, or those compiled into the self-hosted compiler.</summary>
    public static readonly List<ICompilerPlugin> KnownPlugins = [];

    /// <summary>@file: more arguments, one per line (a response file).</summary>
    public static string[] Expand(string[] args) =>
        args.SelectMany(a => a.StartsWith('@') && File.Exists(a[1..]) ? File.ReadAllLines(a[1..]).Where(l => l.Length > 0) : [a]).ToArray();

    public static int Run(string[] args)
    {
        // CS2WASM_HASH_PERTURB=n (a diagnosis of output that depends on identity hash codes): n objects' hash codes
        // taken first, so every later object's RuntimeHelpers.GetHashCode differs from an unperturbed run's
        if (int.TryParse(Environment.GetEnvironmentVariable("CS2WASM_HASH_PERTURB"), out int perturb))
            for (int i = 0; i < perturb; i++) System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(new object());
        // CS2WASM_WATCHDOG=1 (a diagnosis of a build that hangs): the thread pool's counts every 5 s
        if (Environment.GetEnvironmentVariable("CS2WASM_WATCHDOG") == "1")
            new Thread(() =>
            {
                for (int s = 5; ; s += 5)
                {
                    Thread.Sleep(5000);
                    Console.Error.WriteLine($"[watchdog {s} s] pool threads {ThreadPool.ThreadCount}, pending {ThreadPool.PendingWorkItemCount}, completed {ThreadPool.CompletedWorkItemCount}");
                }
            }) { IsBackground = true }.Start();
        args = Expand(args);
        var plugins = new List<ICompilerPlugin>();
        var inputs = new List<string>();
        var pathMap = new List<(string From, string To)>();
        string? output = null, boundary = null, map = null;
        var sections = new List<(string, byte[])>();
        var resources = new List<(string, byte[])>();
        var refs = new List<MetadataReference>();
        var analyzers = new List<string>();
        var properties = new Dictionary<string, string>();
        int pages = 256, maxPages = 16384;
        bool optimize = true, watch = false, checkedBuild = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o": output = args[++i]; break;
                case "--boundary": boundary = args[++i]; break;
                case "--map": map = args[++i]; break;
                case "--section":
                    {
                        var kv = args[++i].Split('=', 2);
                        sections.Add((kv[0], File.ReadAllBytes(kv[1])));
                        break;
                    }
                case "--resource":
                    {
                        // a manifest resource of the program: logical name = file
                        var kv = args[++i].Split('=', 2);
                        resources.Add((kv[0], File.ReadAllBytes(kv[1])));
                        break;
                    }
                case "--plugin":
                    {
                        var name = args[++i];
                        var p = KnownPlugins.FirstOrDefault(x => x.GetType().FullName == name) ?? KnownPlugins.FirstOrDefault(x => x.GetType().Name == name);
                        if (p == null)
                        {
                            Console.Error.WriteLine($"cs2wasm: no plugin '{name}'" + (KnownPlugins.Count > 0 ? " (known: " + string.Join(", ", KnownPlugins.Select(x => x.GetType().FullName)) + ")" : ""));
                            return 2;
                        }
                        if (!plugins.Contains(p)) plugins.Add(p);
                        break;
                    }
                case "--memory-pages": pages = int.Parse(args[++i]); break;
                case "--max-pages": maxPages = int.Parse(args[++i]); break;
                case "--pathmap":
                    {
                        // from=to: source paths under `from` are written as under `to` (as csc's -pathmap: the module's
                        // file names do not depend on where the sources are; '/' separators)
                        var kv = args[++i].Split('=', 2);
                        pathMap.Add((Path.GetFullPath(kv[0]), kv.Length > 1 ? kv[1] : ""));
                        break;
                    }
                case "--no-opt": optimize = false; break;
                case "--checked": checkedBuild = true; break;
                case "--watch": watch = true; break;
                case "-r": refs.Add(MetadataReference.CreateFromFile(args[++i])); break;
                case "-a": analyzers.Add(args[++i]); break;
                case "--property":
                    {
                        var kv = args[++i].Split('=', 2);
                        properties[kv[0]] = kv.Length > 1 ? kv[1] : "";
                        break;
                    }
                case "-h": case "--help":
                    Console.WriteLine("usage: cs2wasm <file.cs|dir>... -o out.wasm [--boundary table.json] [--map out.map] [--section name=file] [--resource name=file] [--pathmap from=to] [--memory-pages N] [--max-pages N] [--no-opt] [-r ref.dll] [-a generators.dll] [--property Name=value] [--plugin Type|plugin.dll] [--watch]");
                    return 0;
                default: inputs.Add(args[i]); break;
            }
        }
        if (inputs.Count == 0 || output == null)
        {
            Console.Error.WriteLine("usage: cs2wasm <file.cs|dir>... -o out.wasm (see --help)");
            return 2;
        }

        int Build()
        {
            var sw = Stopwatch.StartNew();
            List<string> files;
            try
            {
                // (directories are listed again on every build: a file added while watching is compiled)
                files = inputs.SelectMany<string, string>(p => Directory.Exists(p) ? Directory.GetFiles(p, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal) : [p]).ToList();
                var compilation = Frontend.CreateCompilation(Path.GetFileNameWithoutExtension(output), files.Select(f => (MapPath(Path.GetFullPath(f)), File.ReadAllText(f))), refs);
                if (analyzers.Count > 0)
                {
                    compilation = Frontend.RunGenerators(compilation, analyzers, properties, out var gdiags);
                    foreach (var d in gdiags.Where(d => d.Severity >= DiagnosticSeverity.Warning)) Console.Error.WriteLine(d.ToString());
                }
                var options = new CompileOptions
                {
                    Boundary = boundary != null ? BoundaryTable.FromJson(File.ReadAllText(boundary)) : new BoundaryTable(),
                    CustomSections = sections,
                    Resources = resources,
                    MemoryInitialPages = pages,
                    MemoryMaximumPages = maxPages,
                    Optimize = optimize,
                    Checked = checkedBuild,
                    Plugins = plugins,
                };
                var result = Cs2WasmCompiler.Compile(compilation, options);
                foreach (var d in result.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning || d.Id.StartsWith("CW")))
                    Console.Error.WriteLine(d.ToString());
                if (!result.Success)
                {
                    if (watch) Console.Error.WriteLine($"cs2wasm: build failed in {sw.ElapsedMilliseconds} ms");
                    return 1;
                }
                File.WriteAllBytes(output, result.Module!);
                if (map != null && result.SourceMap != null) File.WriteAllText(map, result.SourceMap);
                Console.Error.WriteLine($"cs2wasm: {output} {result.Module!.Length} bytes in {sw.ElapsedMilliseconds} ms");
                return 0;
            }
            catch (IOException ex) when (watch)
            {
                // (an editor may still be writing a file: the next change builds again)
                Console.Error.WriteLine("cs2wasm: " + ex.Message);
                return 1;
            }
        }

        string MapPath(string full)
        {
            foreach (var (from, to) in pathMap)
                if (full.StartsWith(from, StringComparison.OrdinalIgnoreCase))
                    return (to.TrimEnd('/') + "/" + full[from.Length..].TrimStart('/', '\\').Replace('\\', '/')).Replace("//", "/");
            return full;
        }

        int code = Build();
        if (!watch) return code;

        // watch: every input file's directory, and every input directory (recursively), for .cs changes
        var changed = new SemaphoreSlim(0);
        var watchers = new List<FileSystemWatcher>();
        foreach (var p in inputs)
        {
            bool dir = Directory.Exists(p);
            var full = Path.GetFullPath(p);
            var w = new FileSystemWatcher(dir ? full : Path.GetDirectoryName(full)!, dir ? "*.cs" : Path.GetFileName(full))
            {
                IncludeSubdirectories = dir,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            FileSystemEventHandler on = (_, _) => changed.Release();
            w.Changed += on; w.Created += on; w.Deleted += on;
            w.Renamed += (_, _) => changed.Release();
            w.EnableRaisingEvents = true;
            watchers.Add(w);
        }
        Console.Error.WriteLine("cs2wasm: watching for changes (Ctrl+C to stop)");
        while (true)
        {
            changed.Wait();
            // (editors save in bursts: one build after the changes settle)
            while (changed.Wait(150)) { }
            Build();
            // (CS2WASM_TIMES: what a rebuild keeps after a full collection; it should not grow from build to build)
            if (Environment.GetEnvironmentVariable("CS2WASM_TIMES") == "1") Console.Error.WriteLine($"cs2wasm: heap after collection {GC.GetTotalMemory(true) >> 20} MB");
        }
    }
}
