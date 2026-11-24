using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

/// <summary>System.Private.CoreLib from the manifest, the way the compiler's Library builds it from source on every
/// compile (src/Il2Wasm/Compiler/Library.cs: the same files, defines, edits, own files and parse options), emitted
/// once to IL, deterministically, with the manifest's `substitute` lines applied to the source as bodies.</summary>
static class Build
{
    public const string Tag = "v11.0.0";
    public const string Lib = "System.Private.CoreLib";

    // (as Library.s_options, plus what an emitted assembly needs: deterministic, optimized as a release build)
    static CSharpCompilationOptions Options(ImmutableArray<byte> publicKey) => new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            allowUnsafe: true, concurrentBuild: Environment.ProcessorCount > 1, nullableContextOptions: NullableContextOptions.Enable,
            optimizationLevel: OptimizationLevel.Release, deterministic: true, platform: Platform.AnyCpu,
            specificDiagnosticOptions: new Dictionary<string, ReportDiagnostic>
            {
                ["CS0436"] = ReportDiagnostic.Suppress, ["CS0626"] = ReportDiagnostic.Suppress, ["CS8826"] = ReportDiagnostic.Suppress,
            })
        .WithCryptoPublicKey(publicKey).WithPublicSign(true);

    public static string SourcesRoot =>
        Environment.GetEnvironmentVariable("IL2WASM_RUNTIME_SOURCES") is { Length: > 0 } r ? r
        : Path.Combine(Environment.GetEnvironmentVariable("IL2WASM_CACHE") is { Length: > 0 } c ? c
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "il2wasm"), "runtime-" + Tag);

    /// <summary>CoreLib's ILLink substitution files as its project includes them for a 32-bit little-endian target
    /// with no Arm, x86 or WASM hardware intrinsics (System.Private.CoreLib.Shared.projitems). The build embeds them
    /// as one ILLink.Substitutions.xml resource, as the runtime's own build does; the compiler applies every
    /// assembly's. (Copies of v11.0.0's files are in Upstream/System.Private.CoreLib/ILLink.)</summary>
    static readonly string[] IlLinkSubstitutions = [.. new[] { "Shared", "LittleEndian", "32bit", "NoArmIntrinsics", "NoX86Intrinsics", "NoWasmIntrinsics" }
        .Select(n => $"ILLink.Substitutions.{n}.xml")];

    static byte[] MergedSubstitutions()
    {
        var merged = new System.Xml.Linq.XElement("linker");
        foreach (var f in IlLinkSubstitutions)
            merged.Add(System.Xml.Linq.XDocument.Parse(File.ReadAllText(Path.Combine(UpstreamDir, Lib, "ILLink", f))).Root!.Elements());
        return Encoding.UTF8.GetBytes(merged.ToString().Replace("\r\n", "\n"));
    }

    public static string UpstreamDir => Path.Combine(Program.RepoRoot, "src", "Il2Wasm", "Upstream");

    public static int Run(string outDir)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var own = OwnFiles(Lib);
        var manifest = own.First(f => f.Name == "upstream/" + Lib + "/manifest.txt").Text;
        // (not Trim(): an empty substitution value is a trailing tab)
        var lines = manifest.Split('\n').Select(l => l.Trim(' ', '\r')).ToList();
        var defines = lines.Where(l => l.StartsWith("define ")).Select(l => l[7..]).ToArray();
        var files = lines.Where(l => l.StartsWith("file ")).Select(l => l[5..]).ToList();
        var edits = lines.Where(l => l.StartsWith("edit\t")).Select(l => l.Split('\t')).Where(p => p.Length >= 4)
            .GroupBy(p => p[1]).ToDictionary(g => g.Key, g => g.Select(p => (From: p[2], To: p[3])).ToList());
        var substitutions = lines.Where(l => l.StartsWith("substitute\t")).Select(l => l.Split('\t')).Where(p => p.Length >= 5).ToList();
        if (lines.Any(l => l.StartsWith("resource ") || l.StartsWith("root ") || l.StartsWith("reference ")))
            throw new ToolException("the CoreLib manifest has resource/root/reference lines this tool does not handle");

        var root = SourcesRoot;
        Sources.Ensure(root, files);
        Console.Error.WriteLine($"corelib: {files.Count} upstream files from {root}, {own.Count(f => f.Name.EndsWith(".cs"))} own files, {edits.Sum(e => e.Value.Count)} edits, {substitutions.Count} substitutions");

        var po = new CSharpParseOptions(LanguageVersion.Preview, DocumentationMode.None, preprocessorSymbols: defines);
        var trees = files.AsParallel().AsOrdered().Select(f =>
        {
            var path = Path.Combine(root, f);
            var text = File.ReadAllText(path);
            if (edits.TryGetValue(f, out var es))
                foreach (var (from, to) in es)
                {
                    if (!text.Contains(from)) throw new ToolException($"library edit no longer applies: {f}: '{from}'");
                    text = text.Replace(from, to);
                }
            return CSharpSyntaxTree.ParseText(SourceText.From(text, Encoding.UTF8, SourceHashAlgorithm.Sha256), po, path: "dotnet/runtime/" + f);
        }).ToList();
        trees.AddRange(own.Where(f => f.Name.EndsWith(".cs")).AsParallel().AsOrdered()
            .Select(f => CSharpSyntaxTree.ParseText(SourceText.From(f.Text, Encoding.UTF8, SourceHashAlgorithm.Sha256), po, path: "il2wasm/" + f.Name)));
        trees.Add(CSharpSyntaxTree.ParseText(SourceText.From(AssemblyInfo(), Encoding.UTF8, SourceHashAlgorithm.Sha256), po, path: "il2wasm/generated/AssemblyInfo.g.cs"));
        Console.Error.WriteLine($"corelib: {sw.ElapsedMilliseconds,6} ms  parsed {trees.Count} trees");

        var publicKey = StockPublicKey();
        var comp = CSharpCompilation.Create(Lib, trees, [], Options(publicKey));
        comp = Substitute.Apply(comp, substitutions, po);
        Console.Error.WriteLine($"corelib: {sw.ElapsedMilliseconds,6} ms  substitutions applied");

        Directory.CreateDirectory(outDir);
        var dllPath = Path.Combine(outDir, Lib + ".dll");
        var pdbPath = Path.Combine(outDir, Lib + ".pdb");
        using var dll = new MemoryStream();
        using var pdb = new MemoryStream();
        var substitutionsXml = MergedSubstitutions();
        var result = comp.Emit(dll, pdb, manifestResources: [new ResourceDescription("ILLink.Substitutions.xml", () => new MemoryStream(substitutionsXml), isPublic: false)],
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb, pdbFilePath: Lib + ".pdb",
            pdbChecksumAlgorithm: HashAlgorithmName.SHA256));
        var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Console.Error.WriteLine($"corelib: {sw.ElapsedMilliseconds,6} ms  emitted ({errors.Count} errors, {result.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning)} warnings)");
        Console.Error.WriteLine("corelib: warnings by id: " + string.Join(", ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning)
            .GroupBy(d => d.Id).OrderByDescending(g => g.Count()).Select(g => g.Key + " " + g.Count())));
        if (!result.Success)
        {
            foreach (var g in errors.GroupBy(d => d.Id).OrderByDescending(g => g.Count()))
                Console.Error.WriteLine($"  {g.Key} x{g.Count()}: {g.First()}");
            File.WriteAllLines(Path.Combine(outDir, "corelib-errors.txt"), errors.Select(e => e.ToString()));
            throw new ToolException($"CoreLib does not emit: {errors.Count} errors (all in {Path.Combine(outDir, "corelib-errors.txt")})");
        }
        File.WriteAllBytes(dllPath, dll.ToArray());
        File.WriteAllBytes(pdbPath, pdb.ToArray());
        File.WriteAllLines(Path.Combine(outDir, "il2wasm.substitutions.txt"),
            ["# the manifest's substitute lines (already applied to CoreLib's source as bodies; kept for call-site folding)", .. substitutions.Select(p => string.Join('\t', p))]);

        // the runtime's native bindings (docs/CORELIB.md section 7)
        File.Copy(Path.Combine(UpstreamDir, Lib, "natives.txt"), Path.Combine(outDir, "il2wasm.natives.txt"), true);
        Layout(outDir);
        Console.Error.WriteLine($"corelib: {sw.ElapsedMilliseconds,6} ms  framework laid out in {outDir}");
        return 0;
    }

    /// <summary>il2wasm's own files of a library, named as the compiler's embedded resources are
    /// (upstream/&lt;Library&gt;/&lt;relative path&gt;, in ordinal order: Library.Resources).</summary>
    static List<(string Name, string Text)> OwnFiles(string lib)
    {
        var dir = Path.Combine(UpstreamDir, lib);
        return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs") || Path.GetFileName(f) == "manifest.txt")
            .Select(f => (Name: "upstream/" + lib + "/" + Path.GetRelativePath(dir, f).Replace('\\', '/'), Path: f))
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => (f.Name, File.ReadAllText(f.Path)))
            .ToList();
    }

    /// <summary>The assembly attributes the runtime build's generated AssemblyInfo gives CoreLib (version 11.0.0.0, as
    /// the stock libraries reference it).</summary>
    static string AssemblyInfo()
    {
        using var pe = new PEReader(File.OpenRead(Program.PackCoreLib));
        var md = pe.GetMetadataReader();
        var v = md.GetAssemblyDefinition().Version;
        return $$"""
            [assembly: System.Reflection.AssemblyVersion("{{v}}")]
            [assembly: System.Reflection.AssemblyFileVersion("{{v}}")]
            [assembly: System.Reflection.AssemblyInformationalVersion("11.0.0+deae2e32bf0e018e09d64597b7c59b62b73bb3ce; il2wasm")]
            [assembly: System.Reflection.AssemblyCompany("Microsoft Corporation")]
            [assembly: System.Reflection.AssemblyProduct("Microsoft® .NET")]
            [assembly: System.Reflection.AssemblyCopyright("© Microsoft Corporation. All rights reserved.")]
            [assembly: System.Reflection.AssemblyTitle("System.Private.CoreLib")]
            [assembly: System.Reflection.AssemblyDescription("System.Private.CoreLib for il2wasm")]
            [assembly: System.Reflection.AssemblyMetadata("IsTrimmable", "True")]
            [assembly: System.Runtime.CompilerServices.CompilationRelaxations(8)]
            [assembly: System.Runtime.CompilerServices.RuntimeCompatibility(WrapNonExceptionThrows = true)]
            """;
    }

    /// <summary>The stock CoreLib's public key: the stock libraries reference System.Private.CoreLib by it, so il2wasm's
    /// carries the same identity (public-signed; nothing verifies a strong-name signature on .NET Core).</summary>
    static ImmutableArray<byte> StockPublicKey()
    {
        if (!File.Exists(Program.PackCoreLib))
            throw new ToolException($"the browser-wasm runtime pack is not in the NuGet cache ({Program.PackCoreLib}): restore tools/BuildCoreLib");
        using var pe = new PEReader(File.OpenRead(Program.PackCoreLib));
        var md = pe.GetMetadataReader();
        return md.GetBlobContent(md.GetAssemblyDefinition().PublicKey);
    }

    /// <summary>The framework directory: CoreLib (built above) and every IL assembly of the pack, unmodified; then
    /// framework.txt, each file with its size and SHA-256.</summary>
    static void Layout(string outDir)
    {
        int n = 0;
        foreach (var f in Directory.EnumerateFiles(Program.PackLib, "*.dll").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(f);
            if (name == Lib + ".dll") throw new ToolException("the pack's lib directory has a CoreLib of its own: " + f);
            var dst = Path.Combine(outDir, name);
            if (!File.Exists(dst) || new FileInfo(dst).Length != new FileInfo(f).Length || !File.ReadAllBytes(dst).AsSpan().SequenceEqual(File.ReadAllBytes(f)))
                File.Copy(f, dst, true);
            n++;
        }
        var list = Directory.EnumerateFiles(outDir).Where(f => f.EndsWith(".dll") || f.EndsWith(".pdb") || f.EndsWith(".txt") && !f.EndsWith("framework.txt") && !f.EndsWith("corelib-errors.txt"))
            .Order(StringComparer.Ordinal)
            .Select(f => $"{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f)))}  {new FileInfo(f).Length,9}  {Path.GetFileName(f)}");
        File.WriteAllLines(Path.Combine(outDir, "framework.txt"),
            [$"# il2wasm framework: System.Private.CoreLib built by tools/BuildCoreLib from dotnet/runtime {Tag}; the rest is {Program.Meta("BrowserPackId")} {Program.Meta("BrowserPackVersion")} lib/net11.0, unmodified",
             .. list]);
        Console.Error.WriteLine($"corelib: {n} stock assemblies from {Program.PackLib}");
    }
}

/// <summary>Where the dotnet/runtime sources are: a sparse, blobless clone at the tag, as the compiler's
/// UpstreamSources.Ensure makes it (the same cache).</summary>
static class Sources
{
    public static void Ensure(string root, List<string> files)
    {
        var missing = files.Where(f => !File.Exists(Path.Combine(root, f))).ToList();
        if (missing.Count == 0) return;
        if (Environment.GetEnvironmentVariable("IL2WASM_RUNTIME_SOURCES") is { Length: > 0 })
            throw new ToolException($"{missing.Count} files missing from IL2WASM_RUNTIME_SOURCES, e.g. {missing[0]}");
        var patterns = string.Join("\n", missing.Select(f => "/" + f.Replace('\\', '/'))) + "\n";
        if (!Directory.Exists(Path.Combine(root, ".git")))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(root)!);
            Git(null, null, "clone", "--filter=blob:none", "--no-checkout", "--depth", "1", "--branch", Build.Tag, "https://github.com/dotnet/runtime.git", root);
            Git(root, null, "config", "core.longpaths", "true");
            Git(root, patterns, "sparse-checkout", "set", "--no-cone", "--stdin");
            Git(root, null, "checkout", Build.Tag);
        }
        else Git(root, patterns, "sparse-checkout", "add", "--stdin");
    }

    static void Git(string? dir, string? input, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git") { RedirectStandardInput = input != null, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        if (dir != null) { psi.ArgumentList.Add("-C"); psi.ArgumentList.Add(dir); }
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        if (input != null) { p.StandardInput.Write(input); p.StandardInput.Close(); }
        var err = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new ToolException($"git {string.Join(" ", args)}: {err.Result.Trim()}");
    }
}
