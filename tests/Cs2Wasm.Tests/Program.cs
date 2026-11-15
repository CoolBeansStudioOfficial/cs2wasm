// Conformance runner (section 10): each program in tests/Conformance runs under .NET (the oracle) and through
// cs2wasm + the Node host; outputs must match exactly.
// usage: dotnet run --project tests/Cs2Wasm.Tests [-- filter] [--keep]
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Cs2Wasm;
using Microsoft.CodeAnalysis;

var root = FindRoot();
var dir = Path.Combine(root, "tests", "Conformance");
var outDir = Path.Combine(root, "tests", "out");
Directory.CreateDirectory(outDir);
string? filter = args.FirstOrDefault(a => !a.StartsWith("--"));
bool verbose = args.Contains("--verbose");
// programs: each file in tests/Conformance, and each folder in tests/Godot (its files plus the upstream GodotSharp
// sources it lists in godot-sources.txt, from $CS2WASM_GODOTSHARP or ../bazoodle/godot/modules/mono/glue/GodotSharp/GodotSharp)
var programs = new List<(string name, List<(string path, string text)> sources, string[] symbols)>();
foreach (var f in Directory.GetFiles(dir, "*.cs").Order(StringComparer.Ordinal))
    programs.Add((Path.GetFileNameWithoutExtension(f), [(f, File.ReadAllText(f))], []));
var godotDir = Path.Combine(root, "tests", "Godot");
var godotSharp = Environment.GetEnvironmentVariable("CS2WASM_GODOTSHARP")
    ?? Path.GetFullPath(Path.Combine(root, "..", "bazoodle", "godot", "modules", "mono", "glue", "GodotSharp", "GodotSharp"));
var skipped = new List<string>();
if (Directory.Exists(godotDir))
    foreach (var d in Directory.GetDirectories(godotDir).Order(StringComparer.Ordinal))
    {
        var name = "godot_" + Path.GetFileName(d);
        // (a Godot project whose engine calls are compared with stock Godot's: expected-calls.txt, its own check)
        if (File.Exists(Path.Combine(d, "expected-calls.txt"))) continue;
        var list = Path.Combine(d, "godot-sources.txt");
        var srcs = Directory.GetFiles(d, "*.cs").Order(StringComparer.Ordinal).Select(f => (f, File.ReadAllText(f))).ToList();
        if (File.Exists(list))
        {
            if (!Directory.Exists(godotSharp)) { skipped.Add(name); continue; }
            foreach (var rel in File.ReadAllLines(list).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')))
            {
                var full = Path.Combine(godotSharp, rel);
                srcs.Add((full, File.ReadAllText(full)));
            }
        }
        programs.Add((name, srcs, ["GODOT"]));
    }
int pass = 0, fail = 0;
var report = new StringBuilder();
foreach (var n in skipped) { var l = $"SKIP {n} (GodotSharp sources not found at {godotSharp})"; Console.WriteLine(l); report.AppendLine(l); }
foreach (var (name, sources, symbols) in programs.Where(p => filter == null || p.name.Contains(filter)))
{
    var src = sources[0].text;
    var file = sources[0].path;
    var expectError = src.Contains("// expect-error:");
    var comp = Frontend.CreateCompilation(name, sources, preprocessorSymbols: symbols);
    var errs = comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
    if (errs.Count > 0) { Fail(name, "C# errors:\n" + string.Join("\n", errs.Take(20))); continue; }

    var sw = Stopwatch.StartNew();
    var result = Cs2WasmCompiler.Compile(comp, new CompileOptions());
    var buildMs = sw.ElapsedMilliseconds;
    if (expectError)
    {
        var want = src[(src.IndexOf("// expect-error:") + 16)..].Split('\n')[0].Trim();
        var got = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Id + ": " + d.GetMessage()).ToList();
        if (!result.Success && got.Any(g => g.Contains(want))) { Pass(name, $"rejected as expected ({want})"); }
        else Fail(name, $"expected error containing '{want}', got: {(result.Success ? "success" : string.Join("; ", got))}");
        continue;
    }
    if (!result.Success)
    {
        Fail(name, "cs2wasm failed:\n" + string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        continue;
    }
    var wasmPath = Path.Combine(outDir, name + ".wasm");
    File.WriteAllBytes(wasmPath, result.Module!);
    if (result.SourceMap != null) File.WriteAllText(wasmPath + ".map", result.SourceMap);

    string expected;
    try { expected = RunDotNet(comp); }
    catch (Exception ex) { Fail(name, ".NET run failed: " + ex); continue; }
    var (actual, stderr, code) = RunNode(wasmPath);
    if (actual == expected && code == 0) Pass(name, $"{result.Module!.Length} bytes, build {buildMs} ms");
    else Fail(name, $"output differs (exit {code})\n{Diff(expected, actual)}{(stderr.Length > 0 ? "\nstderr: " + stderr : "")}");
}
if (filter == null || filter == "api")
    foreach (var (n, failure) in ApiChecks.Run(root, outDir))
        if (failure == null) Pass("api: " + n, "ok"); else Fail("api: " + n, failure);
Console.WriteLine($"\n{pass} passed, {fail} failed");
File.WriteAllText(Path.Combine(outDir, "report.txt"), report.ToString());
return fail == 0 ? 0 : 1;

void Pass(string n, string info) { pass++; var l = $"PASS {n}  ({info})"; Console.WriteLine(l); report.AppendLine(l); }
void Fail(string n, string why) { fail++; var l = $"FAIL {n}\n  " + why.Replace("\n", "\n  "); Console.WriteLine(l); report.AppendLine(l); }

static string RunDotNet(Microsoft.CodeAnalysis.Compilation comp)
{
    using var ms = new MemoryStream();
    // embedded PDB: .NET then prints file and line in stack traces, as cs2wasm does
    var emit = comp.Emit(ms, options: new Microsoft.CodeAnalysis.Emit.EmitOptions(debugInformationFormat: Microsoft.CodeAnalysis.Emit.DebugInformationFormat.Embedded));
    if (!emit.Success) throw new Exception(string.Join("\n", emit.Diagnostics));
    ms.Position = 0;
    var alc = new AssemblyLoadContext("oracle", isCollectible: true);
    try
    {
        var asm = alc.LoadFromStream(ms);
        var entry = asm.GetTypes().SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .First(m => m.GetCustomAttributesData().Any(a => a.AttributeType.Name == "UnmanagedCallersOnlyAttribute" && a.NamedArguments.Any(n => (string?)n.TypedValue.Value == "main")));
        var old = Console.Out;
        var sw = new StringWriter { NewLine = "\n" };
        Console.SetOut(sw);
        try { unsafe { ((delegate* unmanaged<void>)entry.MethodHandle.GetFunctionPointer())(); } }
        finally { Console.SetOut(old); }
        // Environment.NewLine is CR LF on Windows; cs2wasm (like .NET on Linux and in browsers) uses LF
        return sw.ToString().Replace("\r\n", "\n");
    }
    finally { alc.Unload(); }
}

static (string stdout, string stderr, int code) RunNode(string wasm)
{
    var psi = new ProcessStartInfo("node", [Path.Combine(FindRoot(), "host", "run.mjs"), wasm, "main"])
    { RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
    using var p = Process.Start(psi)!;
    var o = p.StandardOutput.ReadToEndAsync();
    var e = p.StandardError.ReadToEndAsync();
    if (!p.WaitForExit(60000)) { p.Kill(); return (o.Result, "timeout", -1); }
    return (o.Result, e.Result, p.ExitCode);
}

static string Diff(string expected, string actual)
{
    var a = expected.Split('\n'); var b = actual.Split('\n');
    var sb = new StringBuilder();
    int shown = 0;
    for (int i = 0; i < Math.Max(a.Length, b.Length) && shown < 40; i++)
    {
        var x = i < a.Length ? a[i] : "<missing>"; var y = i < b.Length ? b[i] : "<missing>";
        if (x != y) { sb.AppendLine($"line {i + 1}: expected «{x}»\n           actual   «{y}»"); shown++; }
    }
    return sb.ToString();
}

static string FindRoot()
{
    var d = AppContext.BaseDirectory;
    while (d != null && !File.Exists(Path.Combine(d, "cs2wasm.sln"))) d = Path.GetDirectoryName(d);
    return d ?? throw new Exception("repo root not found");
}
