// CoreLibManifest <runtime-root> <upstream-dir>                       evaluate the libraries, generate SR and the externs
// CoreLibManifest check <runtime-root> <upstream-dir> [library]          compile the libraries, summarize errors
//
// Evaluates dotnet/runtime's projects for cs2wasm's target and writes, per library, <upstream-dir>/<Library>/:
//   manifest.txt              the defines, the source files (relative to the runtime root) in project order, and the
//                             libraries it references;
//   Generated/SR.g.cs         its resource strings, as constants;
//   Generated/Interop.Unavailable.g.cs   the platform invokes cs2wasm's boundary does not implement (Externs).
// Only what those projects use of MSBuild is implemented: properties with conditions, $(X) expansion, projitems
// imports, and Compile items (Include/Remove, conditions).
//
// The target: a 32-bit WASM build like Mono's browser one, with no hardware intrinsics and no event tracing.
// System.Private.CoreLib is Mono's project; its runtime-specific half (src/mono/System.Private.CoreLib/src) is listed
// as `runtime`: cs2wasm supplies its own versions of those files, against its runtime (<Library>/Runtime).
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

if (args.Length >= 3 && args[0] == "intrinsics") return Intrinsics.Run(args[1], args[2]);
if (args.Length >= 3 && args[0] == "check") return Check.Run(args[1], args[2], args.Length > 3 ? args[3] : null);
if (args.Length < 2) { Console.Error.WriteLine("usage: CoreLibManifest <runtime-root> <upstream-dir> | check <runtime-root> <upstream-dir> [library]"); return 2; }
return Libraries.Run(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));

static class Libraries
{
    /// <summary>The libraries cs2wasm builds, each after those it references.</summary>
    public static readonly string[] All =
    [
        "System.Private.CoreLib", "System.Collections", "System.Collections.NonGeneric", "System.ObjectModel", "System.Memory",
        "System.Threading", "System.Collections.Concurrent", "System.Linq", "System.Console", "System.Runtime.Numerics",
        "System.Threading.Tasks.Parallel", "System.Text.RegularExpressions",
    ];

    public static string Project(string root, string lib) => lib == "System.Private.CoreLib"
        ? Path.Combine(root, "src", "mono", "System.Private.CoreLib", "System.Private.CoreLib.csproj")
        : Path.Combine(root, "src", "libraries", lib, "src", lib + ".csproj");

    public static string Resx(string root, string lib) => lib == "System.Private.CoreLib"
        ? Path.Combine(root, "src", "libraries", "System.Private.CoreLib", "src", "Resources", "Strings.resx")
        : Path.Combine(root, "src", "libraries", lib, "src", "Resources", "Strings.resx");

    public static int Run(string root, string upstream)
    {
        foreach (var lib in All)
        {
            var dir = Path.Combine(upstream, lib);
            Directory.CreateDirectory(dir);
            new Manifest(root, lib).Write(Path.Combine(dir, "manifest.txt"));
            var resx = Resx(root, lib);
            if (File.Exists(resx)) global::Resx.Run(resx, Path.Combine(dir, "Generated", "SR.g.cs"));
        }
        // the externs need each library compiled, with those it references
        var built = new Dictionary<string, Microsoft.CodeAnalysis.CSharp.CSharpCompilation>();
        foreach (var lib in All) built[lib] = Externs.Run(root, upstream, lib, built);
        return 0;
    }
}

sealed class Manifest
{
    readonly Dictionary<string, string> props = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> files = [];
    readonly List<string> references = [];
    readonly List<(string Path, string Why)> replaced = [];
    readonly List<string> substitutionFiles = [];

    /// <summary>The feature switches cs2wasm fixes (the trimmer's substitutions for them are applied, section 7.4).</summary>
    static readonly Dictionary<string, string> Features = new()
    {
        ["System.Diagnostics.Tracing.EventSource.IsSupported"] = "false",
        ["System.Globalization.Invariant"] = "true",
        ["System.Globalization.PredefinedCulturesOnly"] = "true",
        ["System.Globalization.Hybrid"] = "false",
        ["System.Diagnostics.Debugger.IsSupported"] = "false",
    };

    static readonly (string Path, string Why)[] Replaced =
    [
        ("src/libraries/System.Private.CoreLib/src/System/Threading/ThreadPool.Browser.Threads.cs", "cs2wasm's workers have no JS event loop to yield to"),
        ("src/libraries/System.Private.CoreLib/src/System/TimeZoneInfo.Unix.NonAndroid.cs", "no time zone database: the local zone has the engine clock's offset (Boundary/TimeZoneInfo.Cs2Wasm.cs)"),
    ];
    readonly SortedSet<string> unknown = [];
    // cs2wasm's choices: the projects may not override them
    readonly HashSet<string> pinned = new(StringComparer.OrdinalIgnoreCase);
    readonly string root, lib;

    public Manifest(string root, string lib)
    {
        this.root = root; this.lib = lib;
        var monoDir = Path.Combine(root, "src", "mono", "System.Private.CoreLib");
        var sep = Path.DirectorySeparatorChar.ToString();
        props["CommonPath"] = Path.Combine(root, "src", "libraries", "Common", "src") + sep;
        props["LibrariesProjectRoot"] = Path.Combine(root, "src", "libraries") + sep;
        props["BclSourcesRoot"] = Path.Combine(monoDir, "src");
        props["ILLinkSharedDirectory"] = Path.Combine(root, "src", "libraries", "System.Private.CoreLib", "src", "ILLink") + sep;
        props["ILLinkDirectory"] = Path.Combine(monoDir, "src", "ILLink") + sep;
        props["CoreLibSharedDir"] = Path.Combine(root, "src", "libraries", "System.Private.CoreLib", "src") + sep;
        props["CoreLibProject"] = Libraries.Project(root, "System.Private.CoreLib");
        props["Platform"] = "wasm";
        props["TargetArchitecture"] = "wasm";
        props["TargetOS"] = "browser";
        props["TargetsBrowser"] = "true";
        props["TargetsWasm"] = "true";
        props["TargetPlatformIdentifier"] = "browser";
        props["Is64Bit"] = "false";
        props["FeaturePerfTracing"] = "false";
        props["FeatureWasmManagedThreads"] = "false";
        // cs2wasm has threads (section 4.5): workers on the shared memory, not the browser runtime's JS ones
        props["FeatureMultithreading"] = "true";
        props["RuntimeFlavor"] = "Mono";
        props["DefineConstants"] = "";
        foreach (var k in props.Keys) pinned.Add(k);
        pinned.Remove("DefineConstants");
        Project(Libraries.Project(root, lib));
        // files cs2wasm replaces with its own (under <Library>/Boundary), listed as `replaced`
        foreach (var (drop, why) in Replaced)
            if (files.Remove(Path.GetFullPath(Path.Combine(root, drop)))) replaced.Add((drop, why));
        // what the libraries' build adds to a project with resources (CoreLib has its own SR.cs)
        if (lib != "System.Private.CoreLib" && File.Exists(Libraries.Resx(root, lib)))
            files.Add(Path.Combine(root, "src", "libraries", "Common", "src", "System", "SR.cs"));
    }

    public void Write(string output)
    {
        // with what the SDK defines for a net11.0 project
        var sdk = "NET;NETCOREAPP;NET11_0;RELEASE;" + string.Join(";", Enumerable.Range(5, 7).Select(v => $"NET{v}_0_OR_GREATER"))
            + ";NETCOREAPP1_0_OR_GREATER;NETCOREAPP1_1_OR_GREATER;NETCOREAPP2_0_OR_GREATER;NETCOREAPP2_1_OR_GREATER;NETCOREAPP2_2_OR_GREATER;NETCOREAPP3_0_OR_GREATER;NETCOREAPP3_1_OR_GREATER;";
        var defines = (sdk + props["DefineConstants"]).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(d => d is not ("DEBUG" or "_LOGGING")).Distinct().ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"# {lib} for cs2wasm, evaluated from dotnet/runtime's projects by tools/CoreLibManifest.");
        sb.AppendLine("# define <symbol> | reference <library> | file <path> (compiled) | runtime <path> (Mono's runtime half: cs2wasm supplies its own)");
        sb.AppendLine("# | replaced <path> (an upstream file cs2wasm replaces with its own)");
        sb.AppendLine("# | substitute<TAB>method|field<TAB><type><TAB><signature or name><TAB><value> (the trimmer's, for cs2wasm's features)");
        foreach (var d in defines) sb.AppendLine("define " + d);
        foreach (var r in references.Distinct()) sb.AppendLine("reference " + r);
        foreach (var (path, why) in replaced) sb.AppendLine($"replaced {path}  # {why}");
        // feature switches without a trimmer substitution that cs2wasm fixes: no code is generated at run time
        if (lib == "System.Private.CoreLib")
        {
            sb.AppendLine("substitute	method	System.Runtime.CompilerServices.RuntimeFeature	System.Boolean get_IsDynamicCodeSupported()	false");
            // cs2wasm's stack traces are text captured at the throw (Boundary/Runtime.cs): no frames to format
            sb.AppendLine("substitute	method	System.Exception	System.String GetStackTrace()	=>Cs2Wasm.Runtime.Rt.StackTraceOf");
            sb.AppendLine("substitute	method	System.Diagnostics.StackTrace	System.String ToString(System.Diagnostics.StackTrace/TraceFormat)	=>Cs2Wasm.Runtime.Rt.EmptyTrace");
            // the compiler's enum tables are sorted by unsigned value already (Enum.Mono.cs): no sorting code
            sb.AppendLine("substitute	method	System.Enum	System.Boolean AreSorted(TStorage[])	true");
            // the end of a work item is a safe point for deferred destruction (Boundary/Runtime.cs, Limbo)
            sb.AppendLine("substitute\tmethod\tSystem.Threading.ThreadPool\tSystem.Boolean NotifyWorkItemComplete(System.Threading.ThreadInt64PersistentCounter/ThreadLocalNode,System.Int32)\t=>Cs2Wasm.Runtime.Limbo.NotifyWorkItemComplete");
        }
        // the trimmer's substitutions that apply with cs2wasm's features: method bodies the compiler replaces
        // (`stub`: an empty body, or the value), fields with a fixed value
        foreach (var x in substitutionFiles.Distinct().Where(File.Exists))
            foreach (var t in XDocument.Load(x).Descendants("type"))
            {
                bool Applies(XElement e)
                {
                    var f = (string?)e.Attribute("feature");
                    if (f == null) return true;
                    return Features.TryGetValue(f, out var v) && string.Equals(v, (string?)e.Attribute("featurevalue"), StringComparison.OrdinalIgnoreCase);
                }
                if (!Applies(t)) continue;
                var tn = (string?)t.Attribute("fullname");
                foreach (var m in t.Elements("method").Where(Applies))
                    sb.AppendLine($"substitute	method	{tn}	{(string?)m.Attribute("signature")}	{(string?)m.Attribute("value") ?? ""}");
                foreach (var f in t.Elements("field").Where(Applies))
                    sb.AppendLine($"substitute	field	{tn}	{(string?)f.Attribute("name")}	{(string?)f.Attribute("value") ?? ""}");
            }
        int missing = 0, runtime = 0, n = 0;
        foreach (var f in files.Distinct())
        {
            if (!f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(f)) { missing++; Console.Error.WriteLine("missing " + f); continue; }
            var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            if (rel.StartsWith("src/mono/")) { runtime++; sb.AppendLine("runtime " + rel); }
            else { n++; sb.AppendLine("file " + rel); }
        }
        File.WriteAllText(output, sb.ToString());
        Console.WriteLine($"{lib}: {n} files, {runtime} runtime, {missing} missing, references {string.Join(" ", references.Distinct())}");
        foreach (var u in unknown) Console.WriteLine("  condition not understood: " + u);
    }

    string Expand(string s) => Regex.Replace(s, @"\$\(([A-Za-z_][A-Za-z0-9_]*)\)", m => props.TryGetValue(m.Groups[1].Value, out var v) ? v : "");

    bool Cond(XElement e)
    {
        var c = (string?)e.Attribute("Condition");
        return c == null || Eval(c);
    }

    // comparisons of quoted strings, and/or/!, parentheses
    bool Eval(string raw)
    {
        var s = Expand(raw);
        s = Regex.Replace(s, @"!?\s*Exists\('([^']*)'\)", m => (File.Exists(m.Groups[1].Value) || Directory.Exists(m.Groups[1].Value)) != m.Value.StartsWith('!') ? "'true' == 'true'" : "'true' == 'false'");
        var toks = Regex.Matches(s, @"'[^']*'|==|!=|\(|\)|!|[^\s()!=']+").Select(m => m.Value).ToList();
        int pos = 0;
        string Val(string t) => t.StartsWith('\'') ? t.Trim('\'') : t;
        bool Or() { bool v = And(); while (pos < toks.Count && toks[pos].Equals("or", StringComparison.OrdinalIgnoreCase)) { pos++; bool r = And(); v = v || r; } return v; }
        bool And() { bool v = Unary(); while (pos < toks.Count && toks[pos].Equals("and", StringComparison.OrdinalIgnoreCase)) { pos++; bool r = Unary(); v = v && r; } return v; }
        bool Unary()
        {
            if (toks[pos] == "!") { pos++; return !Unary(); }
            if (toks[pos] == "(") { pos++; bool v = Or(); pos++; return v; }
            string a = Val(toks[pos++]);
            if (pos < toks.Count && toks[pos] is "==" or "!=")
            {
                var op = toks[pos++]; var b = Val(toks[pos++]);
                bool eq = string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
                return op == "==" ? eq : !eq;
            }
            unknown.Add(raw);
            return a.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        try { return Or(); } catch { unknown.Add(raw); return false; }
    }

    void Project(string path)
    {
        var doc = XDocument.Load(path);
        var dir = Path.GetDirectoryName(path)! + Path.DirectorySeparatorChar;
        props.TryGetValue("MSBuildThisFileDirectory", out var saved);
        props["MSBuildThisFileDirectory"] = dir;
        string Full(string f) => Path.GetFullPath(Path.Combine(dir, Expand(f.Trim())));
        foreach (var el in doc.Root!.Elements())
        {
            switch (el.Name.LocalName)
            {
                case "Import":
                    {
                        var ip = Expand((string?)el.Attribute("Project") ?? "");
                        if (ip.EndsWith(".projitems", StringComparison.OrdinalIgnoreCase) && Cond(el)) Project(Path.GetFullPath(Path.Combine(dir, ip)));
                        props["MSBuildThisFileDirectory"] = dir;
                        break;
                    }
                case "PropertyGroup":
                    if (!Cond(el)) break;
                    foreach (var p in el.Elements()) if (!pinned.Contains(p.Name.LocalName) && Cond(p)) props[p.Name.LocalName] = Expand(p.Value);
                    break;
                case "ItemGroup":
                    if (!Cond(el)) break;
                    foreach (var it in el.Elements())
                    {
                        if (!Cond(it)) continue;
                        var inc = (string?)it.Attribute("Include");
                        switch (it.Name.LocalName)
                        {
                            case "Compile":
                                if (inc != null)
                                    foreach (var f in inc.Split(';', StringSplitOptions.RemoveEmptyEntries))
                                    {
                                        if (f.Contains('*')) files.AddRange(Glob(Full(f)));
                                        else files.Add(Full(f));
                                    }
                                if ((string?)it.Attribute("Remove") is { } rem)
                                    foreach (var f in rem.Split(';', StringSplitOptions.RemoveEmptyEntries)) files.Remove(Full(f));
                                break;
                            case "ILLinkSubstitutionsXmls" when inc != null:
                                substitutionFiles.Add(Full(inc));
                                break;
                            case "ProjectReference" when inc != null:
                                {
                                    var name = Path.GetFileNameWithoutExtension(Expand(inc));
                                    if (Libraries.All.Contains(name) && name != "System.Private.CoreLib") references.Add(name);
                                    break;
                                }
                        }
                    }
                    break;
            }
        }
        props["MSBuildThisFileDirectory"] = saved ?? "";
    }

    static IEnumerable<string> Glob(string pattern)
    {
        int star = pattern.IndexOf('*');
        var baseDir = Path.GetDirectoryName(pattern[..star].TrimEnd('\\', '/') + Path.DirectorySeparatorChar)!;
        bool recursive = pattern.Contains("**");
        var filePat = Path.GetFileName(pattern);
        if (!Directory.Exists(baseDir)) return [];
        return Directory.GetFiles(baseDir, filePat, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal);
    }
}
