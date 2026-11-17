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
if (args.Length >= 6 && args[0] == "stubs") return Stubs.Run(args[1], Path.GetFullPath(args[2]), args[3], args[4], args[5], args[6..]);
if (args.Length >= 3 && args[0] == "godot-funcs") return GodotNativeFuncs.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
if (args.Length >= 3 && args[0] == "godot") return GodotLibrary.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
if (args.Length >= 3 && args[0] == "roslyn") return RoslynLibraries.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
if (args.Length >= 3 && args[0] == "check") return Check.Run(args[1], args[2], args.Length > 3 ? args[3] : null);
if (args.Length < 2) { Console.Error.WriteLine("usage: CoreLibManifest <runtime-root> <upstream-dir> | check <runtime-root> <upstream-dir> [library]"); return 2; }
return Libraries.Run(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), args.Length > 2 ? args[2..] : null);

static class Libraries
{
    /// <summary>The libraries cs2wasm builds, each after those it references.</summary>
    public static readonly string[] All =
    [
        "System.Private.CoreLib", "System.Collections", "System.Collections.NonGeneric", "System.ObjectModel",
        "System.Memory", "System.Threading", "System.Collections.Concurrent", "System.Linq", "System.Console",
        "System.Runtime.Numerics", "System.Threading.Tasks.Parallel", "System.Text.RegularExpressions",
        "System.Linq.Expressions", "Microsoft.CSharp", "System.Linq.Queryable", "System.Linq.Parallel", "System.Resources.Writer",
        "System.ComponentModel", "System.ComponentModel.Primitives", "System.Collections.Specialized",
        "System.Diagnostics.TraceSource", "System.Collections.Immutable", "System.IO.MemoryMappedFiles",
        "System.IO.Compression", "System.IO.Compression.Brotli", "System.Reflection.Metadata", "System.Private.Uri",
        "System.Diagnostics.DiagnosticSource", "System.Formats.Asn1", "System.Security.Claims",
        "System.Diagnostics.StackTrace", "System.Threading.Channels", "System.Diagnostics.FileVersionInfo", "System.Diagnostics.Process", "System.IO.FileSystem.Watcher", "System.Net.Primitives",
        "System.Security.Cryptography", "System.Text.Encodings.Web", "System.IO.Pipelines", "System.Text.Json", "System.Net.NameResolution", "System.Net.Sockets", "System.Net.Security",
        "System.Net.Quic", "System.Net.NetworkInformation", "System.Net.Http",
        "System.Runtime.Serialization.Primitives", "System.Private.Xml", "System.Xml.XPath",
        "System.Private.Xml.Linq", "System.Xml.XPath.XDocument", "System.Text.Encoding.CodePages",
        "Microsoft.CodeAnalysis", "Microsoft.CodeAnalysis.CSharp",
    ];

    /// <summary>Libraries cs2wasm writes by hand (members the others name, for capabilities the sandbox lacks): compiled,
    /// never evaluated from dotnet/runtime.</summary>
    public static readonly HashSet<string> HandWritten = ["System.IO.Compression", "Microsoft.CodeAnalysis", "Microsoft.CodeAnalysis.CSharp",
        "System.Runtime.Serialization.Primitives", "System.Text.Encoding.CodePages"];

    public static string Project(string root, string lib) => lib == "System.Private.CoreLib"
        ? Path.Combine(root, "src", "mono", "System.Private.CoreLib", "System.Private.CoreLib.csproj")
        : Path.Combine(root, "src", "libraries", lib, "src", lib + ".csproj");

    public static string Resx(string root, string lib) => lib == "System.Private.CoreLib"
        ? Path.Combine(root, "src", "libraries", "System.Private.CoreLib", "src", "Resources", "Strings.resx")
        : Path.Combine(root, "src", "libraries", lib, "src", "Resources", "Strings.resx");

    /// <summary>The .NET reference pack (the public API of the libraries).</summary>
    public static string RefPack()
    {
        var packs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "packs", "Microsoft.NETCore.App.Ref");
        return Directory.GetDirectories(Directory.GetDirectories(packs).OrderBy(d => Version.TryParse(Path.GetFileName(d), out var v) ? v : new Version()).Last(), "*", SearchOption.AllDirectories).First(d => File.Exists(Path.Combine(d, "System.Runtime.dll")));
    }

    public static int Run(string root, string upstream, string[]? only = null)
    {
        foreach (var lib in All)
        {
            if (only != null && !only.Contains(lib) || HandWritten.Contains(lib)) continue;
            var dir = Path.Combine(upstream, lib);
            Directory.CreateDirectory(dir);
            var manifest = new Manifest(root, lib);
            manifest.Write(Path.Combine(dir, "manifest.txt"));
            // a platform-not-supported assembly on the browser (Sockets, Security, Quic...): its API, throwing
            var pnsePath = Path.Combine(dir, "Generated", "PlatformNotSupported.g.cs");
            if (manifest.Prop("GeneratePlatformNotSupportedAssemblyMessage") is { Length: > 0 } msg)
            {
                var excl = manifest.Prop("ApiExclusionListPath") is { Length: > 0 } ex && File.Exists(Path.Combine(Path.GetDirectoryName(Project(root, lib))!, ex))
                    ? File.ReadAllLines(Path.Combine(Path.GetDirectoryName(Project(root, lib))!, ex)) : [];
                Directory.CreateDirectory(Path.GetDirectoryName(pnsePath)!);
                File.WriteAllText(pnsePath, Stubs.Pnse(RefPack(), lib + ".dll", msg, excl, manifest.Files.Select(f => Path.Combine(root, f))));
            }
            else if (File.Exists(pnsePath)) File.Delete(pnsePath);
            var resx = Resx(root, lib);
            if (File.Exists(resx)) global::Resx.Run(resx, Path.Combine(dir, "Generated", "SR.g.cs"));
        }
        // the externs need each library compiled, with those it references
        var built = new Dictionary<string, Microsoft.CodeAnalysis.CSharp.CSharpCompilation>();
        foreach (var lib in All) built[lib] = (only == null || only.Contains(lib)) && !HandWritten.Contains(lib) ? Externs.Run(root, upstream, lib, built) : Check.Compile(root, upstream, lib, built);
        return 0;
    }
}

sealed class Manifest
{
    readonly Dictionary<string, string> props = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>An evaluated property of the project.</summary>
    public string? Prop(string name) => props.TryGetValue(name, out var v) ? v : null;
    /// <summary>The evaluated source files, relative to the runtime root.</summary>
    public IReadOnlyList<string> Files => files;
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
        ("src/libraries/System.Net.Http/src/System/Net/Http/BrowserHttpHandler/BrowserHttpInterop.cs", "the browser runtime's JavaScript interop: the host's fetch, through imports (Boundary/BrowserHttpInterop.cs)"),
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
        // (the project's browser build when it has one; else its build for no platform, which is what a browser app
        // gets: System.Private.Xml's XmlCollation.Unix.cs)
        var projText = File.Exists(Libraries.Project(root, lib)) ? File.ReadAllText(Libraries.Project(root, lib)) : "";
        props["TargetPlatformIdentifier"] = lib == "System.Private.CoreLib" || projText.Contains("-browser") ? "browser" : "";
        props["Is64Bit"] = "false";
        props["FeaturePerfTracing"] = "false";
        props["FeatureWasmManagedThreads"] = "false";
        // cs2wasm has threads (section 4.5): workers on the shared memory, not the browser runtime's JS ones
        props["FeatureMultithreading"] = "true";
        props["RuntimeFlavor"] = "Mono";
        props["TargetFrameworkIdentifier"] = ".NETCoreApp";
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
        sb.AppendLine("#   (a value =>Type.Method: the body calls that boundary method) | edit<TAB><path><TAB><text><TAB><replacement> (an exact change to an upstream file)");
        foreach (var d in defines) sb.AppendLine("define " + d);
        foreach (var r in references.Distinct()) sb.AppendLine("reference " + r);
        foreach (var (path, why) in replaced) sb.AppendLine($"replaced {path}  # {why}");
        // the console writes to the engine's log import (Boundary/EngineConsole.cs)
        if (lib == "System.Console")
        {
            sb.AppendLine("substitute	method	System.Console	System.IO.TextWriter CreateOutputWriter(System.IO.Stream)	=>System.Cs2WasmConsole.CreateOutputWriter");
            sb.AppendLine("substitute	method	System.ConsolePal	System.IO.Stream OpenStandardOutput()	=>System.Cs2WasmConsole.OpenStandardOutput");
            sb.AppendLine("substitute	method	System.ConsolePal	System.IO.Stream OpenStandardError()	=>System.Cs2WasmConsole.OpenStandardError");
        }
        // expression trees run on the interpreter: no IL is emitted for object[] thunks either (as on NativeAOT)
        if (lib == "System.Linq.Expressions")
        {
            sb.AppendLine("substitute	method	System.Dynamic.Utils.DelegateHelpers	System.Boolean get_CanEmitObjectArrayDelegate()	false");
            // lifting a struct: the Nullable instances the program makes exist (MakeGenericType)
            sb.AppendLine("edit	src/libraries/System.Linq.Expressions/src/System/Dynamic/Utils/TypeUtils.cs	                    throw new NotSupportedException(Strings.LiftingInExpressionRequiresDynamicCode(type));	                    return GetNullableType(type); // cs2wasm: the Nullable instances the program makes exist (MakeGenericType)");
        }
        // a cache of weak references assumes a collector (an object only a weak reference refers to lives until one):
        // with counting it dies at once, so the code that makes a namespace keeps it while it hands it out
        if (lib == "System.Private.Xml.Linq")
        {
            sb.AppendLine("edit\tsrc/libraries/System.Private.Xml.Linq/src/System/Xml/Linq/XNamespace.cs\trefNamespace = s_namespaces.Add(new WeakReference<XNamespace>(new XNamespace(namespaceName.Substring(index, count))));\tvar made = new XNamespace(namespaceName.Substring(index, count)); refNamespace = s_namespaces.Add(new WeakReference<XNamespace>(made)); if (refNamespace != null && refNamespace.TryGetTarget(out XNamespace? own) && ReferenceEquals(own, made)) return own;");
            sb.AppendLine("edit\tsrc/libraries/System.Private.Xml.Linq/src/System/Xml/Linq/XNamespace.cs\tInterlocked.CompareExchange(ref refNmsp, new WeakReference<XNamespace>(new XNamespace(namespaceName)), refOld);\tvar made = new XNamespace(namespaceName); if (Interlocked.CompareExchange(ref refNmsp, new WeakReference<XNamespace>(made), refOld) == refOld) return made;");
        }
        // feature switches without a trimmer substitution that cs2wasm fixes: no code is generated at run time
        if (lib == "System.Private.CoreLib")
        {
            sb.AppendLine("substitute	method	System.Runtime.CompilerServices.RuntimeFeature	System.Boolean get_IsDynamicCodeSupported()	false");
            // DynamicMethod runs on the IL interpreter (Reflection/Emit/DynamicMethod.Interpreter.cs): no dynamic assembly
            sb.AppendLine("substitute	method	System.Reflection.Emit.AssemblyBuilder	System.Void EnsureDynamicCodeSupported()	");
            sb.AppendLine("substitute	method	System.Reflection.Emit.DynamicMethod	System.Reflection.Module GetDynamicMethodsModule()	=>Cs2Wasm.Runtime.Rt.DynamicMethodsModule");
            // cs2wasm's stack traces are text captured at the throw (Boundary/Runtime.cs): no frames to format
            sb.AppendLine("substitute	method	System.Exception	System.String GetStackTrace()	=>Cs2Wasm.Runtime.Rt.StackTraceOf");
            sb.AppendLine("substitute	method	System.Diagnostics.StackTrace	System.String ToString(System.Diagnostics.StackTrace/TraceFormat)	=>Cs2Wasm.Runtime.Rt.EmptyTrace");
            // the compiler's enum tables are sorted by unsigned value already (Enum.Mono.cs): no sorting code
            sb.AppendLine("substitute	method	System.Enum	System.Boolean AreSorted(TStorage[])	true");
            // Type.GetType(name): the program is whole, its types' names are a table (Boundary/TypeInfo.cs)
            sb.AppendLine("substitute	method	System.RuntimeTypeHandle	System.RuntimeType GetTypeByName(System.String,System.Boolean,System.Boolean,System.Threading.StackCrawlMark&)	=>Cs2Wasm.Runtime.TypeInfo.GetTypeByName");
            // C# declares no enums over float, double, char or nint (as for NativeAOT): no code for them
            sb.AppendLine("edit	src/libraries/System.Private.CoreLib/src/System/Enum.cs	#define RARE_ENUMS	#undef RARE_ENUMS");
            // environment variables are the host's (section 8): the block is read once, on the first read, from the host
            // (Boundary/Interop.Env.cs), as getenv would give it
            sb.AppendLine("edit	src/libraries/System.Private.CoreLib/src/System/Environment.Variables.Unix.cs	return Utf8StringMarshaller.ConvertToManaged(Interop.Sys.GetEnv(variable));	EnsureEnvironmentCached();");
            // no garbage collector: a gen-2 collection callback never fires (array pools are not trimmed)
            sb.AppendLine("substitute	method	System.Gen2GcCallback	System.Void Register(System.Func`2,System.Object)	");
            sb.AppendLine("substitute	method	System.Gen2GcCallback	System.Void Register(System.Func`1)	");
            sb.AppendLine("edit	src/libraries/System.Private.CoreLib/src/System/Buffers/SharedArrayPool.cs	_allTlsBuckets.Add(tlsBuckets, null);	");
            sb.AppendLine("edit	src/libraries/System.Private.CoreLib/src/System/Buffers/SharedArrayPool.cs	Gen2GcCallback.Register(s => ((SharedArrayPool<T>)s).Trim(), this);	");
            sb.AppendLine("edit	src/libraries/System.Private.CoreLib/src/System/Buffers/SharedArrayPool.cs	_allTlsBuckets = new ConditionalWeakTable<SharedArrayPoolThreadLocalArray[], object?>();	_allTlsBuckets = null!;");
            // a spin lock spins while its thread holds references it borrowed: its pauses are no safe points (Threads.SpinPause)
            sb.AppendLine("edit\tsrc/libraries/System.Private.CoreLib/src/System/Threading/SpinWait.cs\tThread.Sleep(1);\tCs2Wasm.Runtime.Threads.SpinPause(1);");
            sb.AppendLine("edit\tsrc/libraries/System.Private.CoreLib/src/System/Threading/SpinWait.cs\tThread.Sleep(0);\tCs2Wasm.Runtime.Threads.SpinPause(0);");
            sb.AppendLine("edit\tsrc/libraries/System.Private.CoreLib/src/System/Threading/SpinWait.cs\tThread.Yield();\tCs2Wasm.Runtime.Threads.SpinPause(0);");
            // Span<T>.Clear over references releases them (the upstream loop writes zeros over them)
            sb.AppendLine("edit\tsrc/libraries/System.Private.CoreLib/src/System/Span.cs\tSpanHelpers.ClearWithReferences(ref Unsafe.As<T, IntPtr>(ref _reference), (uint)_length * (nuint)(sizeof(T) / sizeof(nuint)));\tfor (int i = 0; i < _length; i++) Unsafe.Add(ref _reference, i) = default!;");
            // the text scans with 128-bit instructions (Boundary/TextScan.cs)
            sb.AppendLine("edit\tsrc/libraries/System.Private.CoreLib/src/System/SpanHelpers.T.cs\t=> IndexOfValueType(ref Unsafe.As<char, short>(ref searchSpace), (short)value, length);\t=> Cs2Wasm.Runtime.TextScan.IndexOfChar(ref searchSpace, value, length);");
            sb.AppendLine("edit\tsrc/libraries/System.Private.CoreLib/src/System/String.Manipulation.cs\tif (Vector128.IsHardwareAccelerated && source.Length >= Vector128<ushort>.Count * 2)\tif (source.Length >= 16)");
            sb.AppendLine("edit\tsrc/libraries/System.Private.CoreLib/src/System/String.Manipulation.cs\tMakeSeparatorListVectorized(source, ref sepListBuilder, sep0, sep1, sep2);\tCs2Wasm.Runtime.TextScan.Separators(source, ref sepListBuilder, sep0, sep1, sep2);");
            sb.AppendLine("edit\tsrc/libraries/System.Private.CoreLib/src/System/Text/Ascii.CaseConversion.cs\tif (!conversionIsWidthPreserving && !Vector128.IsHardwareAccelerated)\tif (sizeof(TFrom) == 2 && sizeof(TTo) == 2) i = Cs2Wasm.Runtime.TextScan.ChangeCase16((int)pSrc, (int)pDest, (int)elementCount, conversionIsToUpper); if (!conversionIsWidthPreserving && !Vector128.IsHardwareAccelerated)");
            // resource keys are not used for messages (System.Resources.UseSystemResourceKeys is off, as by default)
            sb.AppendLine("substitute	method	System.SR	System.Boolean UsingResourceKeys()	false");
            // a WASM worker has no processor number: the thread's id stands for it (a hint, as in .NET)
            sb.AppendLine("substitute	method	System.Threading.Thread	System.Int32 GetCurrentProcessorId()	=>Cs2Wasm.Runtime.Rt.CurrentProcessorId");
            // enum formatting over the compiler's enum tables (Boundary/EnumFormat.cs), not a copy per storage type
            sb.AppendLine("substitute	method	System.Enum	System.String ToString()	=>Cs2Wasm.Runtime.EnumFormat.ToString");
            sb.AppendLine("substitute	method	System.Enum	System.String ToString(System.String)	=>Cs2Wasm.Runtime.EnumFormat.ToString");
            sb.AppendLine("substitute	method	System.Enum	System.Boolean System.ISpanFormattable.TryFormat(System.Span`1,System.Int32&,System.ReadOnlySpan`1,System.IFormatProvider)	=>Cs2Wasm.Runtime.EnumFormat.TryFormat");
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
        var s = raw;
        // the property functions the projects use: a net11.0 target is .NETCoreApp and compatible with earlier ones
        s = Regex.Replace(s, @"\$\(\[MSBuild\]::GetTargetFrameworkIdentifier\('[^']*'\)\)", ".NETCoreApp");
        s = Regex.Replace(s, @"\$\(\[MSBuild\]::IsTargetFrameworkCompatible\('[^']*',\s*'net(\d+)\.\d+'\)\)", m => int.Parse(m.Groups[1].Value) <= 11 ? "true" : "false");
        s = Expand(s);
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
            if (!a.Equals("true", StringComparison.OrdinalIgnoreCase) && !a.Equals("false", StringComparison.OrdinalIgnoreCase)) unknown.Add(raw);
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
