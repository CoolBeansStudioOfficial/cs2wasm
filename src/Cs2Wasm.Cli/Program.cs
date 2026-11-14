using System.Diagnostics;
using Cs2Wasm;
using Microsoft.CodeAnalysis;

// cs2wasm command-line wrapper (for tests; embedders call Cs2WasmCompiler.Compile in process).
// usage: cs2wasm <file.cs | dir>... -o out.wasm [--boundary table.json] [--map out.wasm.map]
//               [--section name=file] [--memory-pages N] [--max-pages N] [--no-opt] [-r ref.dll]

var inputs = new List<string>();
string? output = null, boundary = null, map = null;
var sections = new List<(string, byte[])>();
var refs = new List<MetadataReference>();
int pages = 256, maxPages = 16384;
bool optimize = true;
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
        case "--memory-pages": pages = int.Parse(args[++i]); break;
        case "--max-pages": maxPages = int.Parse(args[++i]); break;
        case "--no-opt": optimize = false; break;
        case "-r": refs.Add(MetadataReference.CreateFromFile(args[++i])); break;
        case "-h": case "--help":
            Console.WriteLine("usage: cs2wasm <file.cs|dir>... -o out.wasm [--boundary table.json] [--map out.map] [--section name=file] [--memory-pages N] [--no-opt] [-r ref.dll]");
            return 0;
        default: inputs.Add(args[i]); break;
    }
}
if (inputs.Count == 0 || output == null)
{
    Console.Error.WriteLine("usage: cs2wasm <file.cs|dir>... -o out.wasm (see --help)");
    return 2;
}
var files = inputs.SelectMany<string, string>(p => Directory.Exists(p) ? Directory.GetFiles(p, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal) : [p]).ToList();
var sw = Stopwatch.StartNew();
var compilation = Frontend.CreateCompilation(Path.GetFileNameWithoutExtension(output), files.Select(f => (Path.GetFullPath(f), File.ReadAllText(f))), refs);
var options = new CompileOptions
{
    Boundary = boundary != null ? BoundaryTable.FromJson(File.ReadAllText(boundary)) : new BoundaryTable(),
    CustomSections = sections,
    MemoryInitialPages = pages,
    MemoryMaximumPages = maxPages,
    Optimize = optimize,
};
var result = Cs2WasmCompiler.Compile(compilation, options);
foreach (var d in result.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning || d.Id.StartsWith("CW")))
    Console.Error.WriteLine(d.ToString());
if (!result.Success) return 1;
File.WriteAllBytes(output, result.Module!);
if (map != null && result.SourceMap != null) File.WriteAllText(map, result.SourceMap);
Console.Error.WriteLine($"cs2wasm: {output} {result.Module!.Length} bytes in {sw.ElapsedMilliseconds} ms");
return 0;
