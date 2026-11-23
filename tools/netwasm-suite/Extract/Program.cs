// Extracts NetWasm's differential test cases from a pinned checkout (outside this repository) into standalone net11.0
// console projects in a work directory (also outside it), for tools/netwasm-suite.mjs to run under .NET (the oracle)
// and through il2wasm.
//   dotnet run --project tools/netwasm-suite/Extract -- --checkout <netwasm checkout> --work <dir> --commit <sha>
// Writes <work>/cases/<id>/ (the case's sources, a generated driver, a project), <work>/cases.json (the index),
// <work>/not-extracted.json (each test method reached but not turned into a case, with why) and
// <work>/classification.json (the cases that encode NetWasm's deviations from .NET). Nothing of NetWasm's harness is
// used: only the programs its tests compile, their inputs and their options. NetWasm's stored expectations are
// ignored; the oracle is .NET.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Il2Wasm.NetWasmSuite;

var opts = new Dictionary<string, string>();
for (var i = 0; i + 1 < args.Length; i += 2) opts[args[i].TrimStart('-')] = args[i + 1];
var checkout = Path.GetFullPath(opts.GetValueOrDefault("checkout") ?? throw new ArgumentException("--checkout"));
var work = Path.GetFullPath(opts.GetValueOrDefault("work") ?? throw new ArgumentException("--work"));
var commit = opts.GetValueOrDefault("commit") ?? "unknown";
var casesDir = Path.Combine(work, "cases");
if (Directory.Exists(casesDir)) Directory.Delete(casesDir, true);
Directory.CreateDirectory(casesDir);

var cases = new List<CaseInfo>();
var byHash = new Dictionary<string, CaseInfo>();
var notExtracted = new List<JsonObject>();

// ---- 1. the runner cases of tests/NetWasm.Compiler.Tests ----
var testRoot = Path.Combine(checkout, "tests", "NetWasm.Compiler.Tests");
var files = Directory.EnumerateFiles(testRoot, "*.cs", SearchOption.AllDirectories)
    .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
    .OrderBy(f => f, StringComparer.Ordinal).ToList();
var parse = new CSharpParseOptions(LanguageVersion.Preview);
var trees = files.Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), parse, f)).ToList();
var ev = new Evaluator(trees) { ResourceRoot = testRoot };
var reached = 0;
foreach (var type in ev.AllTypes.OrderBy(t => t.SyntaxTree.FilePath, StringComparer.Ordinal).ThenBy(t => t.SpanStart))
    foreach (var m in type.Members.OfType<MethodDeclarationSyntax>())
    {
        var attrs = m.AttributeLists.SelectMany(a => a.Attributes).ToList();
        bool Has(string n) => attrs.Any(a => a.Name.ToString() is var s && (s == n || s.EndsWith("." + n, StringComparison.Ordinal)));
        if (!Has("Fact") && !Has("Theory") && !Has("SkippableFact") && !Has("SkippableTheory")) continue;
        var testName = $"{type.Identifier.ValueText}.{m.Identifier.ValueText}";
        var file = Path.GetRelativePath(checkout, m.SyntaxTree.FilePath).Replace('\\', '/');
        // the rows of a theory: [InlineData(...)]; a theory fed otherwise runs once with its parameters unbound
        var rows = attrs.Where(a => a.Name.ToString() is "InlineData" or "Xunit.InlineData")
            .Select(a => (a.ArgumentList?.Arguments.Select(x => x.Expression).ToList() ?? []).Select(x => (Func<object?>)(() => ev.Eval(x, new Scope(null, type)))).ToList()).ToList();
        foreach (var md in attrs.Where(a => a.Name.ToString() is "MemberData" or "Xunit.MemberData"))
        {
            try
            {
                var memberName = Evaluator.Str(ev.Eval(md.ArgumentList!.Arguments[0].Expression, new Scope(null, type)));
                foreach (var item in Evaluator.AsList(ev.MemberData(type, memberName)))
                {
                    var values = m.ParameterList.Parameters.Count > 1 && item is List<object?> l ? l : [item];
                    rows.Add(values.Select(v => (Func<object?>)(() => v)).ToList());
                }
            }
            catch (Unsupported e) when (Regex.IsMatch(m.ToString(), @"[Rr]unner\.Run\(new\b|\bRun\(new\b"))
            { notExtracted.Add(new JsonObject { ["test"] = testName, ["file"] = file, ["reason"] = "member data: " + e.Message }); }
            catch (Exception e) when (e is Unsupported or InvalidCastException or FormatException or ArgumentException or KeyNotFoundException or ReturnSignal) { }
        }
        if (rows.Count == 0) rows.Add(null!);
        var skip = attrs.Any(a => a.ArgumentList?.Arguments.Any(x => x.NameEquals?.Name.Identifier.ValueText == "Skip") == true);
        for (var r = 0; r < rows.Count; r++)
        {
            var scope = new Scope(null, type);
            var row = rows[r];
            for (var p = 0; p < m.ParameterList.Parameters.Count; p++)
            {
                var pname = m.ParameterList.Parameters[p].Identifier.ValueText;
                if (row is not null && p < row.Count) scope.Declare(pname, new Cell(row[p]));
                else scope.Declare(pname, new Cell(() => throw new Unsupported($"theory parameter {pname} has no inline data")));
            }
            var label = rows.Count > 1 ? $"{testName}#{r}" : testName;
            var found = 0;
            ev.OnRun = (arg, s) =>
            {
                found++;
                reached++;
                var id = found > 1 ? $"{label}@{found}" : label;
                try
                {
                    if (ev.Eval(arg, s) is not Obj o) throw new Unsupported("the runner's argument is not an object");
                    Add(FromFixture(o, id, file, skip));
                }
                catch (Exception e) when (e is Unsupported or InvalidCastException or FormatException or OverflowException or ArgumentException or KeyNotFoundException or IndexOutOfRangeException)
                {
                    notExtracted.Add(new JsonObject { ["test"] = id, ["file"] = file, ["reason"] = e.Message });
                }
            };
            try
            {
                if (m.ExpressionBody is not null) ev.Exec(SyntaxFactory.ExpressionStatement(m.ExpressionBody.Expression), scope);
                else if (m.Body is not null) ev.Exec(m.Body, scope);
                if (found == 0 && Regex.IsMatch(m.ToString(), @"[Rr]unner\.Run\(new\b|\bRun\(new\b"))
                    notExtracted.Add(new JsonObject { ["test"] = label, ["file"] = file, ["reason"] = "no runner call reached (inside a lambda or a branch not taken)" });
            }
            catch (Exception e) when (e is Unsupported or ReturnSignal or InvalidCastException or FormatException or ArgumentException or KeyNotFoundException)
            {
                // a test method the interpreter could not walk to the end: only a problem if it held a runner call later
                if (e is Unsupported && Regex.IsMatch(m.ToString(), @"[Rr]unner\.Run\(|\bRun\(new\b") && found == 0)
                    notExtracted.Add(new JsonObject { ["test"] = label, ["file"] = file, ["reason"] = "method body: " + e.Message });
            }
        }
    }

// ---- 2. the fixtures with a JSON manifest ----
var fixturesDir = Path.Combine(testRoot, "Correctness", "Fixtures");
foreach (var manifest in Directory.EnumerateFiles(fixturesDir, "*.case.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
{
    var rel = Path.GetRelativePath(checkout, manifest).Replace('\\', '/');
    var j = JsonNode.Parse(File.ReadAllText(manifest))!.AsObject();
    var caseId = (string?)j["caseId"] ?? Path.GetFileName(manifest);
    if ((string?)j["inputKind"] != "CSharp") { notExtracted.Add(new JsonObject { ["test"] = "fixture:" + caseId, ["file"] = rel, ["reason"] = $"input kind {(string?)j["inputKind"]} (IL emitted by the harness, no C# source)" }); continue; }
    var sources = j["sourceFiles"]!.AsArray().Select(s => (string)s!).Select(s => (Name: Path.GetFileName(s).Replace(".cs.txt", ".cs"), Text: File.ReadAllText(Path.Combine(fixturesDir, s)))).ToList();
    var ci = new CaseInfo
    {
        Id = "fixture:" + caseId,
        Origin = "fixture",
        File = rel,
        Name = (string)j["name"]!,
        Namespace = (string)j["namespace"]!,
        Entry = (string?)j["entryMethod"] ?? "Run",
        DesktopEntry = (string?)j["desktopEntryMethod"] ?? (string?)j["entryMethod"] ?? "Run",
        Inputs = j["inputs"]!.AsArray().Select(x => (int)x!).ToList(),
        Sources = sources,
        AllowUnsafe = (bool?)j["allowUnsafe"] ?? false,
        UsesTypedTrace = (bool?)j["usesTypedTrace"] ?? false,
        OracleMode = (string?)j["oracleMode"],
        Capabilities = j["requiredRuntimeCapabilities"]?.ToString(),
        NetWasmReferences = (j["referencePaths"] as JsonArray)?.Count > 0,
        ReferenceAliases = j["referenceAssemblyAliases"] is JsonObject al && al.Count > 0,
        CaseId = caseId,
    };
    Add(ci);
}

// ---- 3. the open-source programs of eng/open-source-suts (MIT) ----
var sutRoot = Path.Combine(checkout, "eng", "open-source-suts");
if (Directory.Exists(sutRoot))
    foreach (var dir in Directory.EnumerateDirectories(sutRoot).OrderBy(d => d, StringComparer.Ordinal))
    {
        var pins = new List<(string, string)>();
        foreach (var pf in new[] { "packages.txt", "packages.desktop.txt" })
            if (File.Exists(Path.Combine(dir, pf)))
                foreach (var line in File.ReadAllLines(Path.Combine(dir, pf)))
                    if (line.Trim() is { Length: > 0 } l && !l.StartsWith('#')) { var p = l.Split(' ', 2, StringSplitOptions.TrimEntries); pins.Add((p[0], p[1])); }
        var name = Path.GetFileName(dir);
        Add(new CaseInfo
        {
            Id = "sut:" + name,
            Origin = "sut",
            File = Path.GetRelativePath(checkout, dir).Replace('\\', '/'),
            Name = name,
            Namespace = "",
            Entry = "Main",
            DesktopEntry = "Main",
            Inputs = [],
            Sources = Directory.EnumerateFiles(dir, "*.cs").OrderBy(f => f, StringComparer.Ordinal).Select(f => (Path.GetFileName(f), File.ReadAllText(f))).ToList(),
            Packages = pins,
            AllowUnsafe = true,
        });
    }

// ---- 3b. the library suites of tests/libraries (MIT): TUnit projects, compiled against il2wasm's TUnit stand-in ----
var libRoot = Path.Combine(checkout, "tests", "libraries");
var shim = opts.GetValueOrDefault("shim");
if (Directory.Exists(libRoot) && shim is not null)
    foreach (var proj in Directory.EnumerateFiles(libRoot, "*.csproj", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
    {
        var dir = Path.GetDirectoryName(proj)!;
        var name = Path.GetFileName(dir);
        var rel = Path.GetRelativePath(checkout, dir).Replace('\\', '/');
        var xml = File.ReadAllText(proj);
        if (!xml.Contains("TUnit", StringComparison.Ordinal) || Regex.IsMatch(xml, @"<Compile\s+Include=""\.\./"))
        {
            notExtracted.Add(new JsonObject { ["test"] = "library:" + name, ["file"] = rel, ["reason"] = "not a TUnit suite of its own sources (an xUnit project that compiles NetWasm's library sources)" });
            continue;
        }
        // <Compile Remove="..."> without a condition: those files are not part of the suite
        var removed = Regex.Matches(xml, @"<Compile\s+Remove=""([^""]+)""\s*/>").SelectMany(m => m.Groups[1].Value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sources = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => !removed.Contains(Path.GetRelativePath(dir, f).Replace('\\', '/')) && !removed.Contains(Path.GetFileName(f)))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (Path.GetRelativePath(dir, f).Replace('\\', '/'), File.ReadAllText(f))).ToList();
        Add(new CaseInfo
        {
            Id = "library:" + name,
            Origin = "library",
            File = rel,
            Name = name,
            Namespace = "",
            Entry = "Main",
            DesktopEntry = "Main",
            Inputs = [],
            Sources = sources,
            ShimPath = shim,
            AllowUnsafe = true,
        });
    }

// ---- 4. NetWasm's accepted-difference list: KnownNonBugCorpusPartitioner ----
var knownNonBug = new JsonArray();
foreach (var t in ev.TypesNamed("KnownNonBugCorpusPartitioner").OfType<TypeDeclarationSyntax>())
    foreach (var f in t.Members.OfType<FieldDeclarationSyntax>())
        foreach (var v in f.Declaration.Variables.Where(v => v.Identifier.ValueText == "Rules"))
            foreach (var asg in v.DescendantNodes().OfType<AssignmentExpressionSyntax>().Where(a => a.Left is ImplicitElementAccessSyntax))
            {
                var key = ((ImplicitElementAccessSyntax)asg.Left).ArgumentList.Arguments[0].Expression;
                string caseKey; try { caseKey = Evaluator.Str(ev.Eval(key, new Scope(null, t))); } catch (Unsupported) { caseKey = key.ToString(); }
                JsonNode? inputs = null;
                if (asg.Right is BaseObjectCreationExpressionSyntax { ArgumentList.Arguments.Count: > 0 } oc)
                    try { inputs = new JsonArray(Evaluator.AsList(ev.Eval(oc.ArgumentList!.Arguments[0].Expression, new Scope(null, t))).Select(x => (JsonNode?)Convert.ToInt32(x)).ToArray()); }
                    catch (Unsupported e) { inputs = "unevaluated: " + e.Message; }
                var target = asg.Right is BaseObjectCreationExpressionSyntax { ArgumentList.Arguments.Count: > 1 } oc2 ? oc2.ArgumentList!.Arguments[1].Expression.ToString() : null;
                var matches = cases.Where(c => c.CaseId == caseKey).Select(c => c.Id).ToList();
                knownNonBug.Add(new JsonObject { ["caseId"] = caseKey, ["inputs"] = inputs, ["target"] = target, ["cases"] = new JsonArray(matches.Select(x => (JsonNode?)x).ToArray()) });
                foreach (var c in cases.Where(c => c.CaseId == caseKey)) c.Tags.Add("accepted-difference");
            }

// ---- 5. write the projects ----
foreach (var c in cases) WriteProject(c);
var index = new JsonArray(cases.Select(c => (JsonNode?)c.ToJson()).ToArray());
File.WriteAllText(Path.Combine(work, "cases.json"), index.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
File.WriteAllText(Path.Combine(work, "not-extracted.json"), new JsonArray(notExtracted.Select(x => (JsonNode?)x).ToArray()).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
var classification = new JsonObject
{
    ["commit"] = commit,
    ["acceptedDifferences"] = knownNonBug,
    ["specDeviationsDoc"] = File.Exists(Path.Combine(checkout, "docs", "spec-deviations.md")) ? new JsonArray(File.ReadAllLines(Path.Combine(checkout, "docs", "spec-deviations.md")).Where(l => l.StartsWith("#", StringComparison.Ordinal)).Select(l => (JsonNode?)l).ToArray()) : null,
};
foreach (var tag in cases.SelectMany(c => c.Tags).Distinct().Order(StringComparer.Ordinal))
    classification[tag] = new JsonArray(cases.Where(c => c.Tags.Contains(tag)).Select(c => (JsonNode?)c.Id).ToArray());
File.WriteAllText(Path.Combine(work, "classification.json"), classification.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
var origins = cases.GroupBy(c => c.Origin).Select(g => $"{g.Key} {g.Count()}");
Console.WriteLine($"extracted {cases.Count} cases ({string.Join(", ", origins)}; {cases.Sum(c => c.Aliases.Count)} duplicates folded, {reached} runner calls reached); {notExtracted.Count} not extracted");
return 0;

// ---- helpers ----

CaseInfo FromFixture(Obj o, string test, string file, bool skipped)
{
    object? Arg(int i, string prop) => o.Props.TryGetValue(prop, out var c) ? c.Get() : i < o.Args.Count ? o.Args[i].Get() : null;
    T Opt<T>(string prop, T dflt)
    {
        if (!o.Props.TryGetValue(prop, out var c)) return dflt;
        try { return c.Get() is T t ? t : dflt; } catch (Unsupported) { return dflt; }
    }
    var name = (string?)Arg(0, "Name") ?? throw new Unsupported("no name");
    var ns = (string?)Arg(1, "Namespace") ?? throw new Unsupported("no namespace");
    var source = Arg(2, "Source") as string ?? throw new Unsupported("source is not a string");
    var inputs = Evaluator.AsList(Arg(3, "Inputs")).Select(Convert.ToInt32).ToList();
    var sources = new List<(string, string)> { ("Program.cs", source) };
    if (o.Props.TryGetValue("AdditionalSources", out var extra))
        foreach (var s in Evaluator.AsList(extra.Get()))
        {
            if (s is not Obj so) throw new Unsupported("an additional source is not an object");
            var sname = Evaluator.Str(so.Args.Count > 0 ? so.Args[0].Get() : so.Props["Name"].Get());
            var stext = Evaluator.Str(so.Args.Count > 1 ? so.Args[1].Get() : so.Props["Content"].Get());
            if (!sname.EndsWith(".cs", StringComparison.Ordinal)) sname += ".cs";
            sources.Add((sname, stext));
        }
    var entry = Opt("WasmEntryMethod", "Run");
    var ci = new CaseInfo
    {
        Id = test,
        Origin = "runner",
        File = file,
        Name = name,
        Namespace = ns,
        Entry = entry,
        DesktopEntry = Opt("DesktopEntryMethod", "Run"),
        Inputs = inputs,
        Sources = sources,
        AllowUnsafe = Opt("AllowUnsafe", false),
        UsesTypedTrace = Opt("UsesTypedTrace", false),
        CallbackEntry = Opt("CallbackEntry", false),
        CollectsAtEveryAllocation = Opt("CollectsAtEveryAllocation", false),
        OracleMode = o.Props.ContainsKey("OracleMode") ? Opt<object?>("OracleMode", null)?.ToString() : null,
        Capabilities = o.Props.ContainsKey("RequiredRuntimeCapabilities") ? (o.Props["RequiredRuntimeCapabilities"] is var rc && Try(() => rc.Get()?.ToString()) is { } cap ? cap : "unevaluated") : null,
        NetWasmReferences = o.Props.ContainsKey("NetWasmReferencePaths"),
        ReferenceAliases = o.Props.ContainsKey("ReferenceAssemblyAliases"),
        Skipped = skipped,
    };
    return ci;
}

static string? Try(Func<string?> f) { try { return f(); } catch (Unsupported) { return null; } }

void Add(CaseInfo c)
{
    var key = Hash(string.Join("\0", c.Sources.Select(s => s.Name + "\0" + s.Text)) + "\0" + c.Namespace + "\0" + c.Entry + "\0" + string.Join(",", c.Inputs) + "\0" + string.Join(",", c.Packages.Select(p => p.Item1 + p.Item2)) + c.CallbackEntry);
    if (byHash.TryGetValue(key, out var first)) { first.Aliases.Add(c.Id); return; }
    byHash[key] = c;
    c.Dir = Unique(Sanitize(c.Id));
    Classify(c);
    cases.Add(c);
}

string Unique(string d)
{
    var x = d; var n = 2;
    while (cases.Any(c => string.Equals(c.Dir, x, StringComparison.OrdinalIgnoreCase))) x = $"{d}-{n++}";
    return x;
}

static string Sanitize(string id)
{
    var s = Regex.Replace(id, "[^A-Za-z0-9_.-]+", "-");
    return s.Length > 90 ? s[..70] + "-" + Hash(id)[..12] : s;
}

static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

// The cases that encode one of NetWasm's deviations from .NET. None is skipped: each one's expected output is .NET's.
void Classify(CaseInfo c)
{
    var text = string.Join("\n", c.Sources.Select(s => s.Text));
    if (Regex.IsMatch(text, @"PlatformNotSupportedException")) c.Tags.Add("platform-not-supported");
    if (Regex.IsMatch(text, @"type#|\.GetType\(\)\.(Name|FullName)|typeof\([^)]*\)\.(Name|FullName)|\.AssemblyQualifiedName|Type\.GetType\(")) c.Tags.Add("type-names");
    if (Regex.IsMatch(text, @"\bGC\.(Collect|WaitForPendingFinalizers|GetTotalMemory|KeepAlive|AddMemoryPressure|GetGeneration|TryStartNoGCRegion|SuppressFinalize|ReRegisterForFinalize|GetTotalAllocatedBytes|GetGCMemoryInfo)|\bWeakReference\b|WeakReference<|ConditionalWeakTable|GCHandle|~[A-Z][A-Za-z0-9_]*\s*\(\s*\)")) c.Tags.Add("gc-dependent");
    if (Regex.IsMatch(text, @"\bIntPtr\.Size\b|\bUIntPtr\.Size\b|sizeof\s*\(\s*(nint|nuint|IntPtr|UIntPtr|void\s*\*|[A-Za-z_]+\s*\*)\s*\)|Unsafe\.SizeOf<\s*(nint|nuint|IntPtr|UIntPtr|object|string)|Environment\.Is64BitProcess|\bnint\b|\bnuint\b|Marshal\.SizeOf")) c.Tags.Add("pointer-size");
    if (Regex.IsMatch(text, @"(?i)desktop comparison|off the desktop|desktop oracle|RunDesktop|desktop\b.*\bdiffer")) c.Tags.Add("desktop-carve-out");
    if (c.DesktopEntry != c.Entry) c.Tags.Add("desktop-entry-differs");
    if (c.OracleMode is "SameSource") c.Tags.Add("same-source-oracle");
    if (c.Capabilities is { } cap && cap.Contains("GarbageCollection", StringComparison.Ordinal)) c.Tags.Add("needs-gc-semantics");
    if (c.CollectsAtEveryAllocation) c.Tags.Add("collects-at-every-allocation");
    if (c.NetWasmReferences || c.ReferenceAliases || Regex.IsMatch(text, @"\bNetWasm\.(Runtime|CoreLib|Interop|Hosting|Wit|Component)|JSImport|JSExport|\bextern alias\b")) c.Tags.Add("netwasm-only-api");
    if (Regex.IsMatch(text, @"\bThread\b|\bTask\.Run\b|Parallel\.|ThreadPool|\bTimer\b|async\b")) c.Tags.Add("threads-or-async");
    if (Regex.IsMatch(text, @"\bfloat\b|\bdouble\b|\bMath\.|\bMathF\.|\bHalf\b")) c.Tags.Add("floating-point");
    if (c.Skipped) c.Tags.Add("skipped-by-netwasm");
}

void WriteProject(CaseInfo c)
{
    var dir = Path.Combine(casesDir, c.Dir);
    Directory.CreateDirectory(dir);
    foreach (var (name, text) in c.Sources)
    {
        var path = Path.Combine(dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
    var sb = new StringBuilder();
    sb.Append("<Project Sdk=\"Microsoft.NET.Sdk\">\n  <!-- generated by il2wasm's tools/netwasm-suite from NetWasm ").Append(commit).Append(", ").Append(c.File).Append(" (").Append(c.Id.Replace("--", "-")).Append(") -->\n  <PropertyGroup>\n");
    sb.Append("    <AssemblyName>case</AssemblyName>\n");
    if (c.Origin == "library")
    {
        File.Copy(c.ShimPath!, Path.Combine(dir, "Il2WasmTUnitShim.cs"), true);
        var (driver, tests, problem) = Driver.GenerateLibrary(c);
        c.DriverProblem = problem;
        c.Tests = tests;
        if (driver is not null)
        {
            File.WriteAllText(Path.Combine(dir, "Il2WasmCaseMain.cs"), driver);
            sb.Append("    <StartupObject>Il2WasmSuite.CaseMain</StartupObject>\n");
        }
    }
    else if (c.Origin != "sut")
    {
        var sig = Driver.Analyze(c);
        c.DriverProblem = sig.Problem;
        if (sig.Problem is null)
        {
            File.WriteAllText(Path.Combine(dir, "Il2WasmCaseMain.cs"), Driver.Generate(c, sig));
            sb.Append("    <StartupObject>Il2WasmSuite.CaseMain</StartupObject>\n");
        }
        c.HasTrace = sig.Trace; c.HasTypedTrace = sig.TypedTrace;
    }
    sb.Append("  </PropertyGroup>\n");
    if (c.Packages.Count > 0)
    {
        sb.Append("  <ItemGroup>\n");
        foreach (var (id, ver) in c.Packages) sb.Append("    <PackageReference Include=\"").Append(id).Append("\" Version=\"[").Append(ver).Append("]\" />\n");
        sb.Append("  </ItemGroup>\n");
    }
    sb.Append("</Project>\n");
    File.WriteAllText(Path.Combine(dir, "case.csproj"), sb.ToString());
}

sealed class CaseInfo
{
    public string Id = "", Origin = "", File = "", Name = "", Namespace = "", Entry = "Run", DesktopEntry = "Run", Dir = "";
    public List<int> Inputs = [];
    public string? ShimPath;
    public int Tests;
    public List<(string Name, string Text)> Sources = [];
    public List<(string, string)> Packages = [];
    public bool AllowUnsafe, UsesTypedTrace, CallbackEntry, CollectsAtEveryAllocation, NetWasmReferences, ReferenceAliases, Skipped, HasTrace, HasTypedTrace;
    public string? OracleMode, Capabilities, CaseId, DriverProblem;
    public List<string> Aliases = [];
    public SortedSet<string> Tags = new(StringComparer.Ordinal);
    public JsonObject ToJson() => new()
    {
        ["id"] = Id, ["dir"] = Dir, ["origin"] = Origin, ["file"] = File, ["name"] = Name, ["namespace"] = Namespace,
        ["entry"] = Entry, ["desktopEntry"] = DesktopEntry, ["inputs"] = Origin == "library" ? Tests : Inputs.Count,
        ["sources"] = new JsonArray(Sources.Select(s => (JsonNode?)s.Name).ToArray()),
        ["packages"] = new JsonArray(Packages.Select(p => (JsonNode?)$"{p.Item1} {p.Item2}").ToArray()),
        ["trace"] = HasTrace, ["typedTrace"] = HasTypedTrace, ["callbackEntry"] = CallbackEntry,
        ["oracleMode"] = OracleMode, ["capabilities"] = Capabilities, ["caseId"] = CaseId,
        ["driverProblem"] = DriverProblem,
        ["tags"] = new JsonArray(Tags.Select(t => (JsonNode?)t).ToArray()),
        ["aliases"] = new JsonArray(Aliases.Select(a => (JsonNode?)a).ToArray()),
    };
}
