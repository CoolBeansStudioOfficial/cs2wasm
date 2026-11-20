using System.Text;
using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cs2Wasm.Compiler;

internal enum MethodImpl { Body, Import, Intrinsic, Synthesized, Missing }

internal sealed class MethodInst
{
    public required IMethodSymbol Sym;
    public required WasmFunction Fn;
    public required string Key;
    public MethodImpl Impl;
    public bool Compiled;
    /// <summary>Set for clones with suppressed imports (section 7.1).</summary>
    public CloneSpec? Clone;
    public List<MethodInst> Callees = [];
    public Location? FirstUse;
    /// <summary>The method whose compilation first asked for this one (for CS2WASM_WHY's reachability chains).</summary>
    public MethodInst? ReachedFrom;
    /// <summary>A copy specialized for constant arguments (Specialization.cs): parameter ordinal to its constant.</summary>
    public IReadOnlyDictionary<int, ConstArg>? Consts;
    /// <summary>Set for lambdas and local functions: compiles the body with its closure information.</summary>
    public Action? Compile;
    /// <summary>A method that returns `this` (section 4.1, specialization by ownership): its body, compiled to return
    /// `this` borrowed, for direct calls; this one (Fn) is then the owned-return wrapper indirect calls use.</summary>
    public MethodInst? BorrowedReturn;
    /// <summary>Set on that body: `return this` does not count.</summary>
    public bool ReturnsBorrowed;
    /// <summary>A borrowed return's kind for its callers: Stable (the receiver: `return this`) or Unstable (a field's
    /// value, as the caller's own load of that field would be).</summary>
    public Own BorrowedKind = Own.Stable;
    /// <summary>A field's value returned borrowed: the fields it came through (FieldBorrows keys, a line each), when
    /// they are all instance fields of classes from `this` on; a caller that keeps its receiver holds the value across
    /// calls without a count while none of them is stored (FieldBorrows.cs). Null: not known.</summary>
    public string? BorrowKeys;
    /// <summary>A copy that takes these arguments (a bit per parameter ordinal) owned (OwnedParams.cs).</summary>
    public int OwnedParams;
    /// <summary>A copy of a number type's method that takes the number itself, not its address (OwnedParams.cs: ThisValueVariant).</summary>
    public bool ThisByValue;
}

sealed class StaticSlot(Driver d, int offset) : IAddress
{
    public int Offset = offset;
    public int Address => d.StaticBase + Offset;
}

/// <summary>A piece of the data section (a string literal, a blob): moved or dropped by the final layout
/// (Driver.LayoutMemory) unless its address was taken as a number before then.</summary>
sealed class DataChunk
{
    public int Offset, Length, Align;
    public string? Owner;
    public int Final = -1;
    public List<(int at, IAddress target)>? Relocs;
}

sealed class DataAddr(Driver d, DataChunk chunk) : IAddress
{
    public DataChunk Chunk => chunk;
    public int Address => d.ChunkAddress(chunk);
    /// <summary>A string literal: an immortal object (its count is -1; retain and release leave it as it is).</summary>
    public bool Immortal { get; init; }
}

sealed class LateAddr(Func<int> f) : IAddress { public int Address => f(); }

sealed class VirtualStub
{
    public required IMethodSymbol Slot;
    public required WasmFunction Fn;
    public bool IsInterface;
    public Dictionary<string, (ITypeSymbol type, MethodInst? impl, bool boxed)> Cases = [];
    /// <summary>Constant arguments every call through this stub passes (each implementation's specialized copy is called).</summary>
    public IReadOnlyDictionary<int, ConstArg>? Consts;
    /// <summary>The method whose compilation first called through this stub (CS2WASM_WHY_STUBS).</summary>
    public MethodInst? MadeBy;
}

internal sealed partial class Driver
{
    public readonly CSharpCompilation Main;
    public readonly DynamicLowering Dynamic;
    readonly CompileException? _dynamicError;
    readonly List<Compilation> _inputs;
    public IReadOnlyList<Compilation> Inputs => _inputs;
    public bool IsInput(SyntaxTree t) => _inputs.Any(c => c.ContainsSyntaxTree(t));
    /// <summary>Every type the program constructs (allocated objects' types).</summary>
    public IEnumerable<ITypeSymbol> ConstructedTypes() => Constructed.Where(id => id > 0 && id < TypeById.Count && !DisplayById.ContainsKey(id) && !SyntheticById.ContainsKey(id)).Select(id => TypeById[id]);
    public readonly Library Lib;
    public readonly CompileOptions Options;
    public readonly Types Types;
    public readonly WasmModule Module = new();
    public Arenas Arenas = null!;
    public readonly List<Diagnostic> Diagnostics = [];
    public CSharpCompilation LibCompilation => Lib.Compilation;
    public ITypeSymbol ObjectType => Types.Canonical(Main.GetSpecialType(SpecialType.System_Object));
    public ITypeSymbol StringType => Types.Canonical(Main.GetSpecialType(SpecialType.System_String));

    readonly Dictionary<string, MethodInst> _methods = [];
    readonly Queue<MethodInst> _work = new();
    readonly Dictionary<string, int> _typeIds = [];
    public readonly List<ITypeSymbol> TypeById = [null!];
    public readonly SortedSet<int> Constructed = [];
    readonly Dictionary<string, StaticSlot> _statics = [];
    int _staticSize;
    readonly Dictionary<string, IAddress> _literals = [];
    public readonly DataSegment Data = new();
    public readonly Dictionary<string, VirtualStub> Stubs = [];
    public int DataBase = 16, StaticBase, StackTop, HeapBase;

    public readonly WasmGlobal StackPointer;
    /// <summary>Per instance (thread): its managed thread id (1 = the engine thread) and thread-local block.</summary>
    public readonly WasmGlobal ThreadId, TlsBase;
    int _tlsSize;
    /// <summary>The thread statics' offsets and types (released when a thread ends).</summary>
    public readonly List<(int off, ITypeSymbol type)> _tlsTypes = [];
    public int TlsSize => (_tlsSize + 15) / 16 * 16;
    readonly Dictionary<string, int> _tls = [];
    /// <summary>Offset of a [ThreadStatic] field in the thread-local block.</summary>
    public int ThreadStaticOffset(INamedTypeSymbol owner, ISymbol storage, ITypeSymbol type)
    {
        var key = Types.Key(Types.Canonical(owner)) + "::" + storage.MetadataName;
        if (_tls.TryGetValue(key, out var off)) return off;
        var (size, align) = Types.StorageOf(type);
        _tlsSize = (_tlsSize + align - 1) / align * align;
        off = _tlsSize; _tlsSize += size;
        _tls[key] = off;
        _tlsTypes.Add((off, Types.Canonical(type)));
        return off;
    }
    /// <summary>True once the program can start a thread: counts become atomic and the runtime lock is real.</summary>
    /// <summary>CS2WASM_NO_THREADS=1: the module is built for an engine without threads, as .NET's own WebAssembly
    /// build is: starting a thread throws PlatformNotSupportedException (Threads.SpawnCore), so no other thread can
    /// ever run, and the program is compiled as one that starts none (plain counts, no locks, every analysis that
    /// needs a single thread).</summary>
    internal static readonly bool s_noThreads = Environment.GetEnvironmentVariable("CS2WASM_NO_THREADS") == "1";
    public bool UsesThreads => !s_noThreads && AllMethods.Any(m => m.Sym.Name == "SpawnCore" && m.Sym.ContainingType?.Name == "Threads" && m.Compiled);
    /// <summary>Section 4.5, decided when compiling: the program is compiled as one that starts threads. Then a
    /// reference read from memory another thread may write is read and counted in one step (rt.load_retain), counts
    /// are atomic, and nothing borrows from such memory; an object is destroyed when its count reaches zero, as
    /// without threads. Whether a program starts threads is known when it has been compiled once
    /// (Cs2WasmCompiler.Compile compiles it again in this mode if it does).</summary>
    public bool ThreadMode { get; init; }
    Sharing? _sharing;
    /// <summary>The types another thread can reach, as the last compilation of this program found them (Api: a
    /// program with threads is compiled again knowing them). Null: not known; nothing is taken as the thread's own.</summary>
    public IReadOnlyDictionary<string, bool>? SharingPrior { get; init; }
    /// <summary>The borrow placeholders made for a place no other thread can store (FieldBorrows: no other thread's
    /// store to look for).</summary>
    internal readonly HashSet<WasmFunction> ThreadLocalBorrows = [];
    /// <summary>Which objects another thread can reach (Sharing.cs): recorded in a program with threads only.</summary>
    public Sharing? Sharing => ThreadMode ? _sharing ??= new Sharing(this) : null;

    /// <summary>Which types another thread can reach, once every function exists; then the counts by type.</summary>
    internal void ResolveSharing()
    {
        if (Sharing is { } sharing && !sharing.Done)
        {
            var keyOf = new Dictionary<WasmFunction, string>();
            foreach (var mi in AllMethods) keyOf.TryAdd(mi.Fn, Types.MethodKey(mi.Sym));
            sharing.Analyze(Arenas.AllSites, f => keyOf.TryGetValue(f, out var k) ? k : null);
        }
        Rt.ResolveCounts(Sharing);
    }
    /// <summary>Thrown out of Run when a compilation that is not in thread mode meets the start of a thread.</summary>
    public sealed class ThreadsSeen : Exception { }
    /// <summary>CS2WASM_EXP_NO_THREADMODE=1 (a measurement, unsafe: the program must then run on one thread): a program
    /// that starts threads is compiled as one that does not, to see what compiling for threads costs.</summary>
    internal static readonly bool s_expNoThreadMode = Environment.GetEnvironmentVariable("CS2WASM_EXP_NO_THREADMODE") == "1";
    public readonly WasmTag ExceptionTag;
    readonly List<SourcePos> _positions = [];
    readonly Dictionary<SourcePos, int> _posIndex = [];
    public readonly List<string> Files = [];
    readonly Dictionary<string, int> _fileIndex = [];
    public readonly Dictionary<string, (string module, string name)> BoundaryByDocId = [];
    public readonly List<string> NonDeterminismNotes = [];

    /// <summary>A program that references GodotSharp (a Godot game) gets cs2wasm's GodotPlugins: the exports through
    /// which the engine starts it and calls its scripts (Godot/GodotPlugins.cs).</summary>
    static CSharpCompilation AddGodotPlugins(CSharpCompilation main)
    {
        if (!main.ReferencedAssemblyNames.Any(a => a.Name == "GodotSharp") || main.GetTypeByMetadataName("Cs2Wasm.Plugins.GodotPlugins") != null) return main;
        using var s = typeof(Driver).Assembly.GetManifestResourceStream("godot/GodotPlugins.cs")!;
        var text = new StreamReader(s).ReadToEnd();
        var po = (CSharpParseOptions)main.SyntaxTrees.First().Options;
        return main.AddSyntaxTrees(CSharpSyntaxTree.ParseText(text, po, path: "cs2wasm/godot/GodotPlugins.cs"));
    }

    public Driver(CSharpCompilation main, CompileOptions options)
    {
        // dynamic operations: call sites in a generated class of the program (DynamicLowering.cs)
        main = AddGodotPlugins(main);
        try { main = DynamicLowering.Rewrite(main, out Dynamic); }
        catch (CompileException ex) { Dynamic = new DynamicLowering(); _dynamicError = ex; }
        Main = main;
        Options = options;
        _inputs = [main, .. options.AdditionalCompilations];
        Lib = new Library(main);
        Types = new Types(this);
        StackPointer = new WasmGlobal("__stack_pointer", ValType.I32, true, 0) { Export = "__stack_pointer" };
        Module.Globals.Add(StackPointer);
        ThreadId = new WasmGlobal("__thread_id", ValType.I32, true, 1);
        Module.Globals.Add(ThreadId);
        TlsBase = new WasmGlobal("__tls_base", ValType.I32, true, 0) { Export = "__tls_base" };
        Module.Globals.Add(TlsBase);
        StackLo = new WasmGlobal("__stack_lo", ValType.I32, true, 0); Module.Globals.Add(StackLo);
        StackHi = new WasmGlobal("__stack_hi", ValType.I32, true, 0); Module.Globals.Add(StackHi);
        DestroyDepth = new WasmGlobal("__destroy_depth", ValType.I32, true, 0); Module.Globals.Add(DestroyDepth);
        DestroyPending = new WasmGlobal("__destroy_pending", ValType.I32, true, 0); Module.Globals.Add(DestroyPending);
        ExceptionTag = new WasmTag("cs_exception", new FuncType([ValType.I32], []));
        Module.Tags.Add(ExceptionTag);
        Module.MemoryMinPages = options.MemoryInitialPages;
        Module.MemoryMaxPages = options.MemoryMaximumPages;
        Module.MemoryShared = options.SharedMemory;
        foreach (var b in options.Boundary.Imports) BoundaryByDocId[Types.StripReturn(b.Method, b.Method.Contains("op_Implicit") || b.Method.Contains("op_Explicit"))] = (b.Module, b.Name);
        _typeIds["System.String"] = 1; TypeById.Add(StringType); Constructed.Add(1);
    }

    // ---------------- diagnostics ----------------
    public void Report(DiagnosticDescriptor d, Location? loc, params object?[] args) =>
        Diagnostics.Add(Diagnostic.Create(d, loc ?? Location.None, args));

    // ---------------- source positions ----------------
    public int Position(SyntaxNode? node)
    {
        if (node == null) return -1;
        var span = node.GetLocation().GetLineSpan();
        if (!span.IsValid) return -1;
        var file = span.Path ?? "";
        if (!_fileIndex.TryGetValue(file, out var fi)) { fi = Files.Count; Files.Add(file); _fileIndex[file] = fi; }
        var p = new SourcePos(fi, span.StartLinePosition.Line + 1);
        if (!_posIndex.TryGetValue(p, out var idx)) { idx = _positions.Count; _positions.Add(p); _posIndex[p] = idx; }
        return idx;
    }
    public SourcePos PositionAt(int idx) => _positions[idx];

    // ---------------- types ----------------
    public int TypeId(ITypeSymbol t)
    {
        t = Types.Canonical(t);
        var k = Types.Key(t);
        if (!_typeIds.TryGetValue(k, out var id)) { id = TypeById.Count; TypeById.Add(t); _typeIds[k] = id; }
        return id;
    }

    /// <summary>A type's id if it has one already (tables must not add types).</summary>
    public int? ExistingTypeId(ITypeSymbol t) => _typeIds.TryGetValue(Types.Key(Types.Canonical(t)), out var id) ? id : null;

    int[] _extId = [];
    List<int> _intId = [];
    /// <summary>How many types the module numbers (RuntimeGen.AssignTypeIds).</summary>
    public int TypeCount;
    /// <summary>The types numbered below this hold no reference: destroying one frees it (RuntimeGen.AssignTypeIds).</summary>
    public int LeafTypeCount;
    public void SetTypeNumbers(List<int> internals)
    {
        _intId = internals;
        _extId = new int[TypeById.Count];
        Array.Fill(_extId, -1);
        for (int k = 0; k < internals.Count; k++) _extId[internals[k]] = k;
        TypeCount = internals.Count;
        Module.TypeIdMap = i => ExtId(i) is int x && x >= 0 ? x : throw new CompileException("internal: type " + (i < TypeById.Count ? TypeById[i] : (object)i) + " is used at run time but has no number");
    }
    /// <summary>The module's number for an internal type id, -1 when the module never meets the type.</summary>
    public int ExtId(int id) => id >= 0 && id < _extId.Length ? _extId[id] : -1;
    public int IntId(int ext) => _intId[ext];

    /// <summary>The function being compiled (types it constructs are live only if it is), or null.</summary>
    public WasmFunction? CurrentFn;
    List<ITypeSymbol>? _programTypes;
    /// <summary>The program's own types in the order the C# compiler writes its type definitions: each namespace's
    /// types in declaration order, namespaces taken from a stack (the global one first, then its namespaces last to
    /// first), then nested types after the types that contain them.</summary>
    public IReadOnlyList<ITypeSymbol> ProgramTypes()
    {
        if (_programTypes != null) return _programTypes;
        var r = new List<INamedTypeSymbol>();
        foreach (var c in Inputs)
        {
            var stack = new Stack<INamespaceSymbol>();
            stack.Push(c.Assembly.GlobalNamespace);
            while (stack.Count > 0)
                foreach (var m in stack.Pop().GetMembers())
                {
                    if (m is INamespaceSymbol ns) stack.Push(ns);
                    else if (m is INamedTypeSymbol t && t.DeclaringSyntaxReferences.Any(sr => IsInput(sr.SyntaxTree))) r.Add(t);
                }
        }
        for (int i = 0; i < r.Count; i++)
            foreach (var n in r[i].GetTypeMembers())
                if (n.DeclaringSyntaxReferences.Any(sr => IsInput(sr.SyntaxTree))) r.Add(n);
        return _programTypes = r.Select(t => Types.Canonical(t.OriginalDefinition)).ToList();
    }

    /// <summary>The types the program's code names with typeof, and the function that names each.</summary>
    public readonly List<(ITypeSymbol type, WasmFunction? fn)> TypeOfTargets = [];
    int _interpreterTypes;
    bool _interpreterLinked;

    /// <summary>With the IL interpreter linked in (a DynamicMethod runs), the types the program names can be boxed
    /// and made arrays of at run time (box, newarr, a local's default value), as the IL the program emits asks.</summary>
    public bool ExpandInterpreterTypes()
    {
        if (!_interpreterLinked)
        {
            if (!AllMethods.Any(m => m.Sym.ContainingType is { Name: "IlInterpreter" } ct && ct.ContainingNamespace?.ToDisplayString() == "System.Reflection.Emit")) return false;
            _interpreterLinked = true;
        }
        bool any = false;
        var saved = CurrentFn;
        for (; _interpreterTypes < TypeOfTargets.Count; _interpreterTypes++)
        {
            var (t, fn) = TypeOfTargets[_interpreterTypes];
            if (t is ITypeParameterSymbol || t.IsStatic || t.IsRefLikeType || t.SpecialType == SpecialType.System_Void || t is IPointerTypeSymbol or IFunctionPointerTypeSymbol
                || t is INamedTypeSymbol { IsUnboundGenericType: true } || HasTypeParameter(t)) continue;
            CurrentFn = fn;
            if (t.IsValueType) MarkConstructed(Types.Canonical(t));
            MarkConstructed(Types.Canonical(Main.CreateArrayTypeSymbol(t)));
            any = true;
        }
        CurrentFn = saved;
        return any;
    }

    static bool HasTypeParameter(ITypeSymbol t) => t switch
    {
        ITypeParameterSymbol => true,
        IArrayTypeSymbol a => HasTypeParameter(a.ElementType),
        INamedTypeSymbol n => n.TypeArguments.Any(HasTypeParameter) || n.ContainingType is { } o && HasTypeParameter(o),
        _ => false,
    };
    /// <summary>For each constructed type: the functions that construct it; null in the set: made outside any.</summary>
    public readonly Dictionary<int, HashSet<WasmFunction?>> ConstructedBy = [];
    /// <summary>Field-wise ValueType.Equals per struct (ValueEquality.cs).</summary>
    public readonly Dictionary<string, MethodInst> ValueEqualsFns = [];

    /// <summary>The program reads array elements as objects (Array.GetValue, a foreach over an Array): a value
    /// element is boxed then, so the element type of every array type the program makes is one it makes boxes of.</summary>
    public bool BoxesElements;
    public void MarkBoxedElements()
    {
        if (BoxesElements) return;
        BoxesElements = true;
        foreach (var id in Constructed.ToList())
            if (!DisplayById.ContainsKey(id) && TypeById[id] is IArrayTypeSymbol { ElementType: { IsValueType: true, IsRefLikeType: false } et } && et is not ITypeParameterSymbol and not IPointerTypeSymbol)
                MarkConstructed(Types.Canonical(et));
    }

    public int MarkConstructed(ITypeSymbol t)
    {
        if (BoxesElements && t is IArrayTypeSymbol { ElementType: { IsValueType: true, IsRefLikeType: false } bet } && bet is not ITypeParameterSymbol and not IPointerTypeSymbol)
            MarkConstructed(Types.Canonical(bet));
        var id = TypeId(t);
        if (!ConstructedBy.TryGetValue(id, out var by)) ConstructedBy[id] = by = [];
        by.Add(CurrentFn);
        if (Constructed.Add(id) && s_whyConstructed is { } why && (why == "enum" ? t.TypeKind == TypeKind.Enum : Types.Key(t).Contains(why)))
            Console.Error.WriteLine($"constructed {Types.Key(t)} in {(Compiling != null ? Display(Compiling.Sym) : "?")}{WhyReachedAlways(Compiling)}");
        return id;
    }

    /// <summary>CS2WASM_WHY_CONSTRUCTED=Name (or "enum"): where the program first makes objects (or boxes) of a type.</summary>
    static readonly string? s_whyConstructed = Environment.GetEnvironmentVariable("CS2WASM_WHY_CONSTRUCTED") is { Length: > 0 } w ? w : null;

    string WhyReachedAlways(MethodInst? mi)
    {
        var chain = new List<string>();
        for (var x = mi?.ReachedFrom; x != null && chain.Count < 12; x = x.ReachedFrom) chain.Add(Display(x.Sym));
        return chain.Count == 0 ? "" : " <- " + string.Join(" <- ", chain);
    }

    // ---------------- statics ----------------
    public IAddress StaticAddress(string key, int size, int align)
    {
        if (!_statics.TryGetValue(key, out var s))
        {
            _staticSize = (_staticSize + align - 1) / align * align;
            s = new StaticSlot(this, _staticSize);
            _staticSize += size;
            _statics[key] = s;
        }
        return s;
    }

    /// <summary>Static storage of a field of a concrete type (each generic instantiation has its own).</summary>
    public IAddress StaticField(INamedTypeSymbol owner, ISymbol storage, ITypeSymbol type)
    {
        var (size, align) = Types.StorageOf(type);
        return StaticAddress("F:" + Types.Key(Types.Canonical(owner)) + "::" + storage.MetadataName, size, align);
    }

    // ---------------- literals ----------------
    int _literalBytes;
    readonly Dictionary<string, int>? _blobs = Environment.GetEnvironmentVariable("CS2WASM_STATS") != null ? [] : null;

    public IAddress StringLiteral(string s)
    {
        if (_literals.TryGetValue(s, out var a)) return a;
        // header: rc = -1 (immortal), typeId 1 (string), hash 0; then length, chars, terminator
        Align(8);
        int off = Data.Bytes.Count;
        AddI32(-1); AddI32(1); AddI32(0); AddI32(s.Length);
        foreach (var c in s) { Data.Bytes.Add((byte)c); Data.Bytes.Add((byte)(c >> 8)); }
        Data.Bytes.Add(0); Data.Bytes.Add(0);
        a = new DataAddr(this, NewChunk(off, 8)) { Immortal = true };
        _literals[s] = a;
        _literalBytes += Data.Bytes.Count - off;
        return a;
    }

    public IAddress DataBlob(byte[] bytes, int align)
    {
        Align(align);
        int off = Data.Bytes.Count;
        Data.Bytes.AddRange(bytes);
        if (_blobs != null) { var k = Compiling != null ? Display(Compiling.Sym) : "(tables)"; _blobs.TryGetValue(k, out int n); _blobs[k] = n + bytes.Length; }
        var addr = new DataAddr(this, NewChunk(off, align));
        if (_blobs != null) addr.Chunk.Owner = Compiling != null ? Display(Compiling.Sym) : "(tables)";
        return addr;
    }

    /// <summary>Writes a value into a blob made earlier (its bytes are still in the data being built).</summary>
    public void PatchData(DataAddr blob, int byteOffset, int value)
    {
        var bytes = BitConverter.GetBytes(value);
        for (int i = 0; i < 4; i++) Data.Bytes[blob.Chunk.Offset + byteOffset + i] = bytes[i];
    }

    /// <summary>A blob with addresses in it: each (byte offset, target) is written when the layout is final.</summary>
    public IAddress DataBlob(byte[] bytes, int align, List<(int at, IAddress target)> relocs)
    {
        var a = (DataAddr)DataBlob(bytes, align);
        a.Chunk.Relocs = relocs;
        return a;
    }

    readonly List<DataChunk> _chunks = [];
    bool _dataFinal, _dataPinned;

    DataChunk NewChunk(int off, int align)
    {
        var c = new DataChunk { Offset = off, Length = Data.Bytes.Count - off, Align = Math.Max(1, align) };
        _chunks.Add(c);
        return c;
    }

    /// <summary>A chunk's address; taken before the final layout, it pins the data where it is.</summary>
    public int ChunkAddress(DataChunk c)
    {
        if (!_dataFinal && !_dataPinned)
        {
            _dataPinned = true;
            if (Environment.GetEnvironmentVariable("CS2WASM_DEBUG_PIN") != null) Console.Error.WriteLine("data pinned at " + Environment.StackTrace);
        }
        return DataBase + (c.Final >= 0 ? c.Final : c.Offset);
    }

    /// <summary>Keeps the chunks live code refers to (directly, or through the relocations of live chunks), packed in
    /// their order, and writes the relocations. Nothing moves when some address was taken as a number earlier.</summary>
    void CompactData()
    {
        var liveFns = Module.LiveFunctions();
        var live = new HashSet<DataChunk>();
        var work = new Stack<DataChunk>();
        void Mark(DataChunk c) { if (live.Add(c)) work.Push(c); }
        foreach (var f in liveFns)
            foreach (var ins in f.Body)
                if (ins.Ref is DataAddr da) Mark(da.Chunk);
        while (work.Count > 0)
        {
            var c = work.Pop();
            if (c.Relocs != null) foreach (var (_, t) in c.Relocs) if (t is DataAddr ta) Mark(ta.Chunk);
        }
        if (Environment.GetEnvironmentVariable("CS2WASM_STATS") != null)
            Console.Error.WriteLine($"  data chunks: {live.Count}/{_chunks.Count} live, {live.Sum(c => c.Length)} of {_chunks.Sum(c => c.Length)} bytes; functions live {liveFns.Count}/{Module.Functions.Count}");
        if (Environment.GetEnvironmentVariable("CS2WASM_STATS") != null)
        {
            Console.Error.WriteLine($"  live string literals {live.Where(c => c.Owner == null).Sum(c => c.Length)} bytes ({live.Count(c => c.Owner == null)}); live blobs:");
            foreach (var g in live.Where(c => c.Owner != null).GroupBy(c => c.Owner).OrderByDescending(g => g.Sum(c => c.Length)).Take(15))
                Console.Error.WriteLine($"    {g.Sum(c => c.Length),7} {g.Key}");
        }
        var old = Data.Bytes.ToArray();
        if (!_dataPinned)
        {
            var bytes = new List<byte>();
            foreach (var c in _chunks)
            {
                if (!live.Contains(c)) continue;
                while (bytes.Count % c.Align != 0) bytes.Add(0);
                c.Final = bytes.Count;
                for (int i = 0; i < c.Length; i++) bytes.Add(old[c.Offset + i]);
            }
            Data.Bytes.Clear();
            Data.Bytes.AddRange(bytes);
        }
        _dataFinal = true;
        foreach (var c in _chunks)
        {
            if (c.Relocs == null || !_dataPinned && !live.Contains(c)) continue;
            int at0 = c.Final >= 0 ? c.Final : c.Offset;
            foreach (var (at, t) in c.Relocs)
            {
                var v = BitConverter.GetBytes(t.Address);
                for (int i = 0; i < 4; i++) Data.Bytes[at0 + at + i] = v[i];
            }
        }
    }

    void Align(int n) { while (Data.Bytes.Count % n != 0) Data.Bytes.Add(0); }
    void AddI32(int v) { Data.Bytes.AddRange(BitConverter.GetBytes(v)); }

    // ---------------- methods ----------------
    public static FuncType Signature(IMethodSymbol m, Types types, bool forceInstanceThis = false)
    {
        var ps = new List<ValType>();
        var ret = m.ReturnsByRef || m.ReturnsByRefReadonly ? ValType.I32 : Types.WasmType(types.Canonical(m.ReturnType));
        bool structRet = !m.ReturnsByRef && !m.ReturnsByRefReadonly && Types.IsStruct(types.Canonical(m.ReturnType));
        if (structRet) ps.Add(ValType.I32);
        if (!m.IsStatic || forceInstanceThis) ps.Add(ValType.I32);
        foreach (var p in m.Parameters)
            ps.Add(p.RefKind != RefKind.None ? ValType.I32 : Types.WasmType(types.Canonical(p.Type)) ?? ValType.I32);
        return new FuncType([.. ps], structRet || ret == null ? [] : [ret.Value]);
    }

    public IMethodSymbol CanonicalMethod(IMethodSymbol m)
    {
        if (m.MethodKind is MethodKind.LocalFunction or MethodKind.AnonymousFunction) return m;
        if (m.ReducedFrom != null) m = m.ReducedFrom.Construct([.. m.TypeArguments]) ?? m;
        var ct = (INamedTypeSymbol)Types.Canonical(m.ContainingType);
        if (SymbolEqualityComparer.Default.Equals(ct, m.ContainingType)) return m;
        var defId = Types.DocId(m.OriginalDefinition);
        IMethodSymbol? found = null;
        foreach (var cand in ct.GetMembers(m.Name).OfType<IMethodSymbol>())
            if (Types.DocId(cand.OriginalDefinition) == defId) { found = cand; break; }
        if (found == null) return m; // not in the library: reported where it is called
        if (found.IsGenericMethod) found = found.Construct(m.TypeArguments.Select(Types.Canonical).ToArray());
        return found;
    }

    /// <summary>Returns the function for a concrete method instance, scheduling its compilation.</summary>
    /// <summary>The method being compiled.</summary>
    public MethodInst? Compiling;

    /// <summary>CS2WASM_WHY=1: how the program reaches a method (the chain of first callers).</summary>
    public string WhyReached(MethodInst? mi)
    {
        if (Environment.GetEnvironmentVariable("CS2WASM_WHY") != "1" || mi == null) return "";
        var chain = new List<string>();
        for (var x = mi; x != null && chain.Count < 30; x = x.ReachedFrom) chain.Add(Display(x.Sym));
        return " [reached: " + string.Join(" <- ", chain) + "]";
    }

    public readonly HashSet<string> ReportedCapabilities = [];

    /// <summary>The symbol a call to m compiles (canonical; a partial method's implementing part).</summary>
    internal IMethodSymbol MethodTarget(IMethodSymbol m)
    {
        m = CanonicalMethod(m);
        // a partial method is compiled from its implementing part (the boundary's bodies of LibraryImport partials)
        if (m.PartialImplementationPart is { } pi && !m.IsGenericMethod && m.ContainingType is { IsGenericType: false }) m = pi;
        return m;
    }

    public MethodInst Method(IMethodSymbol m)
    {
        m = MethodTarget(m);
        var key = Types.MethodKey(m);
        if (_methods.TryGetValue(key, out var mi)) return mi;
        var impl = Classify(m);
        if (impl == MethodImpl.Missing && Environment.GetEnvironmentVariable("CS2WASM_DEBUG") != null)
            Console.Error.WriteLine("register missing " + key + " " + Environment.StackTrace.Replace('\n', '|'));
        WasmFunction fn;
        if (impl == MethodImpl.Import)
        {
            var (mod, name) = ImportName(m);
            fn = Module.AddImport(mod, name, Display(m), Signature(m, Types));
        }
        else fn = Module.AddFunction(Display(m), Signature(m, Types));
        mi = new MethodInst { Sym = m, Fn = fn, Key = key, Impl = impl, ReachedFrom = Compiling };
        fn.FrameName = IsHiddenFrame(m) ? null : FrameName(m);
        fn.ProgramCode = !IsLibrary(m);
        fn.InlineHint = impl == MethodImpl.Body && IsInlineHint(m.OriginalDefinition);
        fn.NoInline = impl == MethodImpl.Body && IsNoInline(m.OriginalDefinition);
        _methods[key] = mi;
        bool returnsThis = impl == MethodImpl.Body && ReturnsThis(m);
        if (returnsThis || impl == MethodImpl.Body && !ThreadMode && ReturnsField(m))
        {
            // the body returns `this`, or a field's value, borrowed; the method's own function retains it for indirect
            // callers (section 4.1: a getter costs what the field's load costs)
            var bfn = Module.AddFunction(Display(m) + (returnsThis ? " [returns this]" : " [returns a field]"), Signature(m, Types));
            bfn.FrameName = fn.FrameName; bfn.ProgramCode = fn.ProgramCode;
            var mb = new MethodInst { Sym = m, Fn = bfn, Key = key + (returnsThis ? "|returns-this" : "|returns-field"), Impl = MethodImpl.Body, ReachedFrom = Compiling, ReturnsBorrowed = true,
                BorrowedKind = returnsThis ? Own.Stable : Own.Unstable, BorrowKeys = returnsThis ? null : ReturnPathKeys(m) };
            _methods[mb.Key] = mb;
            _work.Enqueue(mb);
            mi.BorrowedReturn = mb;
            mi.Impl = MethodImpl.Synthesized;
            fn.FrameName = null; // (a forwarder: the body's frame is the one traces show)
            var wfn = fn;
            mi.Compile = () =>
            {
                var e = new Emitter(wfn);
                for (int i = 0; i < wfn.Type.Params.Length; i++) e.Get(i);
                e.Call(bfn);
                e.Call(Rt.Retain);
            };
        }
        if (mi.Impl is MethodImpl.Body or MethodImpl.Synthesized or MethodImpl.Missing) _work.Enqueue(mi);
        return mi;
    }

    readonly Dictionary<string, bool> _returnsThis = [];

    /// <summary>A method whose body is one `return` of a field's value: a field of `this` (of a class), of such a field,
    /// or a static field (`Name => _name;`, `get { return s_default; }`). Its borrowed form returns the value without a
    /// count: what the caller gets is what loading the field itself would give it.</summary>
    static readonly bool s_noReturnField = Environment.GetEnvironmentVariable("CS2WASM_NO_RETURN_FIELD") == "1";

    /// <summary>Whether a call of the method can give its caller a reference without a count (its borrowed form).</summary>
    public bool ReturnsFieldOf(IMethodSymbol m) => ReturnsField(m);

    bool ReturnsField(IMethodSymbol m)
    {
        if (s_noReturnField) return false;
        var def = m.OriginalDefinition;
        if (def.MethodKind is not (MethodKind.Ordinary or MethodKind.PropertyGet or MethodKind.Conversion or MethodKind.UserDefinedOperator) || def.IsAsync || def.IsIterator || def.ReturnsVoid || def.ReturnsByRef || def.ReturnsByRefReadonly) return false;
        if (!def.ReturnType.IsReferenceType && def.ReturnType.TypeKind != TypeKind.TypeParameter) return false;
        // (this instantiation's: a type parameter's field is a reference here only when its argument is)
        if (!m.ReturnType.IsReferenceType) return false;
        // (a struct's `this` is the address of its storage, valid to the end of the caller's statement: its field's value
        // borrowed is what the caller's own load through that address would be)
        if (!def.IsStatic && def.ContainingType is not { TypeKind: TypeKind.Class or TypeKind.Struct }) return false;
        if (def.DeclaringSyntaxReferences.Length != 1) return false;
        var sr = def.DeclaringSyntaxReferences[0];
        if (ModelFor(sr.SyntaxTree).GetOperation(sr.GetSyntax()) is not { } body) return false;
        var block = body switch { IMethodBodyOperation mb => mb.BlockBody ?? mb.ExpressionBody, IBlockOperation b => b, _ => null };
        if (block is not { Operations: [IReturnOperation { ReturnedValue: { } v }] }) return false;
        while (v is IConversionOperation { OperatorMethod: null, IsImplicit: true } c && (c.Conversion.IsIdentity || c.Conversion.IsReference)) v = c.Operand;
        // a lazy getter (`return _f ??= Make();`): the result is the field's value after the store
        if (v is ICoalesceAssignmentOperation { Target: var lazy }) v = lazy;
        // the roots: `this`, a static field, a struct parameter (by value: the caller's temporary or its own variable,
        // either alive to the end of the caller's statement); through fields, and elements of arrays they hold
        bool Root(IOperation? o) => o is IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance }
            || o is IParameterReferenceOperation { Parameter: { RefKind: RefKind.None or RefKind.In, Type.TypeKind: TypeKind.Struct } };
        bool Simple(IOperation o) => o.ConstantValue.HasValue || o is IParameterReferenceOperation or ILocalReferenceOperation
            || o is IFieldReferenceOperation { Field.IsVolatile: false } fi && (fi.Field.IsStatic || Root(fi.Instance))
            || o is IConversionOperation { OperatorMethod: null } cv && Simple(cv.Operand);
        bool Holder(IOperation o) => o is IFieldReferenceOperation { Field: { IsVolatile: false, IsFixedSizeBuffer: false } f } fr
            && (f.IsStatic || Root(fr.Instance) || fr.Instance is { } inner && inner.Type?.TypeKind is TypeKind.Class or TypeKind.Struct && Holder(inner))
            && (f.IsStatic || f.ContainingType.TypeKind is TypeKind.Class or TypeKind.Struct);
        bool Path(IOperation o) => o switch
        {
            IFieldReferenceOperation fr => (fr.Field.Type.IsReferenceType || fr.Field.Type.TypeKind == TypeKind.TypeParameter) && Holder(fr),
            IArrayElementReferenceOperation { Indices: [var ix] } ae => (ae.Type?.IsReferenceType == true || ae.Type?.TypeKind == TypeKind.TypeParameter) && Simple(ix)
                && ae.ArrayReference is IFieldReferenceOperation arr && arr.Type is IArrayTypeSymbol { IsSZArray: true } && Holder(arr),
            _ => false,
        };
        return Path(v);
    }

    /// <summary>The fields the value a ReturnsField method returns came through, as FieldBorrows keys (a line each; the
    /// elements' key for an array's element): only when the path starts at `this` of a class and every field on it is an
    /// instance field of a class. Null otherwise (a static field, a struct on the way: the caller counts as before).</summary>
    string? ReturnPathKeys(IMethodSymbol m)
    {
        var def = m.OriginalDefinition;
        if (def.IsStatic || def.ContainingType.TypeKind != TypeKind.Class) return null;
        var sr = def.DeclaringSyntaxReferences[0];
        if (ModelFor(sr.SyntaxTree).GetOperation(sr.GetSyntax()) is not { } body) return null;
        var block = body switch { IMethodBodyOperation mb => mb.BlockBody ?? mb.ExpressionBody, IBlockOperation b => b, _ => null };
        if (block is not { Operations: [IReturnOperation { ReturnedValue: { } v }] }) return null;
        while (v is IConversionOperation { OperatorMethod: null } c) v = c.Operand;
        // (a lazy getter stores the field itself, before the caller's borrow starts)
        if (v is ICoalesceAssignmentOperation { Target: var lazy }) v = lazy;
        var map = TypeMap.For(m);
        var keys = new List<string>();
        while (true)
        {
            while (v is IConversionOperation { OperatorMethod: null } c2) v = c2.Operand;
            if (v is IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance }) break;
            if (v is IArrayElementReferenceOperation ae) { keys.Add(ElementsKey); v = ae.ArrayReference; continue; }
            if (v is not IFieldReferenceOperation { Field: { IsStatic: false } f, Instance: { } inst } || f.ContainingType.TypeKind != TypeKind.Class) return null;
            var storage = f.CorrespondingTupleField ?? f;
            var owner = Types.Resolve(f.ContainingType, map);
            var k = FieldKey(owner, storage);
            NoteBorrowField(k, storage, Types.Resolve(f.Type, map));
            keys.Add(k);
            v = inst;
        }
        return keys.Count == 0 ? null : string.Join("\n", keys);
    }

    /// <summary>An instance method of a class whose every return is `this` (StringBuilder.Append and friends).</summary>
    bool ReturnsThis(IMethodSymbol m)
    {
        var def = m.OriginalDefinition;
        if (def.IsStatic || def.MethodKind != MethodKind.Ordinary || def.IsAsync || def.ReturnsVoid || def.ReturnsByRef || def.ReturnsByRefReadonly) return false;
        if (def.ContainingType is not { TypeKind: TypeKind.Class } || !def.ReturnType.IsReferenceType) return false;
        var key = Types.MethodKey(def);
        if (_returnsThis.TryGetValue(key, out var r)) return r;
        _returnsThis[key] = false; // (while deciding: a recursive call does not count)
        r = false;
        foreach (var sr in def.DeclaringSyntaxReferences)
        {
            if (ModelFor(sr.SyntaxTree).GetOperation(sr.GetSyntax()) is not { } body) { r = false; break; }
            var nested = body.Descendants().Where(o => o is IAnonymousFunctionOperation or ILocalFunctionOperation).SelectMany(o => o.DescendantsAndSelf()).ToHashSet();
            var returns = body.DescendantsAndSelf().Where(o => !nested.Contains(o)).ToList();
            if (returns.Any(o => o.Kind is OperationKind.YieldReturn or OperationKind.YieldBreak)) { r = false; break; }
            var rets = returns.OfType<IReturnOperation>().ToList();
            r = rets.Count > 0 && rets.All(x =>
            {
                var v = x.ReturnedValue;
                while (v is IConversionOperation { OperatorMethod: null } c) v = c.Operand;
                if (v is IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance }) return true;
                // `return Append(x)`: a call on this of a method that returns this (not virtual: that one is the one run)
                return v is IInvocationOperation { Instance: IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance }, IsVirtual: false } inv
                    && ReturnsThis(inv.TargetMethod);
            });
        }
        _returnsThis[key] = r;
        return r;
    }

    public bool TryGetMethod(string key, out MethodInst mi) => _methods.TryGetValue(key, out mi!);

    /// <summary>Schedules a compiler-made function (state machine MoveNext) with its own compile action.</summary>
    internal void EnqueueExtra(MethodInst mi)
    {
        _methods[mi.Key] = mi;
        _work.Enqueue(mi);
    }

    public static string Display(IMethodSymbol m)
    {
        var s = m.ToDisplayString(new SymbolDisplayFormat(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            memberOptions: SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters,
            parameterOptions: SymbolDisplayParameterOptions.IncludeType,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes));
        return s;
    }

    public bool IsLibrary(ISymbol s) => s.Locations.Any(l => l.IsInSource && l.SourceTree != null && Lib.Contains(l.SourceTree));

    static bool HasAttr(ISymbol s, string fullName) =>
        s.GetAttributes().Any(a => a.AttributeClass != null && Types.FullName(a.AttributeClass) == fullName);

    public bool IsIntrinsic(IMethodSymbol m) => IsLibrary(m) && (HasAttr(m.OriginalDefinition, "Cs2Wasm.Runtime.IntrinsicAttribute") || IsUpstreamIntrinsic(m)) || IsEffectsIntrinsic(m);

    /// <summary>The upstream library's [Intrinsic] members whose C# body is a placeholder (the JIT supplies the code):
    /// the compiler emits them (tools/CoreLibManifest intrinsics lists them all).</summary>
    public static bool IsUpstreamIntrinsic(IMethodSymbol m)
    {
        var owner = Types.FullName(m.ContainingType);
        return owner switch
        {
            "System.Runtime.CompilerServices.Unsafe" => m.Name is "AsPointer" or "As" or "AreSame" or "Copy" or "CopyBlock" or "CopyBlockUnaligned"
                or "IsAddressGreaterThan" or "IsAddressLessThan" or "InitBlock" or "InitBlockUnaligned" or "AddByteOffset" or "AsRef"
                or "ByteOffset" or "SkipInit" or "SubtractByteOffset" or "Unbox",
            "System.Threading.Interlocked" => m.Name == "MemoryBarrier"
                || (m.Name is "Exchange" or "CompareExchange" && m.Parameters.Length > 0 && Types.PrimSize(Types.PrimOf(m.Parameters[0].Type)) < 4 && Types.PrimOf(m.Parameters[0].Type) != Prim.None),
            // (Memmove and ClearWithoutReferences are what memory.copy and memory.fill do)
            "System.SpanHelpers" => m.Name is "memmove" or "memset" || m.Name is "Memmove" or "ClearWithoutReferences" && !m.IsGenericMethod,
            "System.Double" or "System.Single" => m.Name == "ConvertToIntegerNative" && m.TypeArguments.Length == 1 && Types.PrimOf(m.TypeArguments[0]) is not (Prim.None or Prim.F32 or Prim.F64 or Prim.Bool),
            "System.Runtime.CompilerServices.RuntimeHelpers" => m.Name is "GetRawData" or "IsBitwiseEquatable" or "IsReferenceOrContainsReferences" or "ObjectHasComponentSize",
            "System.Threading.Volatile" => m.Name is "ReadBarrier" or "WriteBarrier",
            // (the machine's own instructions: upstream asks for a processor's, and falls back to loops and tables)
            "System.Numerics.BitOperations" => m.Name is "LeadingZeroCount" or "TrailingZeroCount" or "PopCount" or "RotateLeft" or "RotateRight" or "Log2"
                && m.Parameters.Length > 0 && Types.PrimOf(m.Parameters[0].Type) is not (Prim.None or Prim.F32 or Prim.F64 or Prim.Bool),
            "System.Object" => m.Name == "GetType",
            // new T() for a T known at compile time (LazyInitializer and friends call it directly)
            "System.Activator" => m.Name == "CreateInstance" && m.IsGenericMethod && m.Parameters.Length == 0,
            _ => false,
        };
    }

    (string, string) ImportName(IMethodSymbol m)
    {
        var a = m.OriginalDefinition.GetAttributes().FirstOrDefault(x => x.AttributeClass != null && Types.FullName(x.AttributeClass) == "Cs2Wasm.Runtime.ImportAttribute");
        if (a != null) return ((string)a.ConstructorArguments[0].Value!, (string)a.ConstructorArguments[1].Value!);
        return BoundaryByDocId[Types.CommentId(m)];
    }

    MethodImpl Classify(IMethodSymbol m)
    {
        var def = m.OriginalDefinition;
        if (IsIntrinsic(m)) return MethodImpl.Intrinsic;
        if (IsLibrary(m) && HasAttr(def, "Cs2Wasm.Runtime.ImportAttribute")) return MethodImpl.Import;
        if (!IsLibrary(m) && (BoundaryByDocId.ContainsKey(Types.CommentId(m)) ||
            (Options.Boundary.MarkerAttribute is { } ma && HasAttr(def, ma))))
            return BoundaryByDocId.ContainsKey(Types.CommentId(m)) ? MethodImpl.Import : MethodImpl.Missing;
        if (MissingCapability(def) != null) return MethodImpl.Missing;
        if (HasBody(def)) return MethodImpl.Body;
        if (IsSynthesizable(m)) return MethodImpl.Synthesized;
        return MethodImpl.Missing;
    }

    /// <summary>The capability a library member marked [MissingCapability] needs (SPEC section 8): reaching it is a build error.</summary>
    public static string? MissingCapability(IMethodSymbol def)
    {
        foreach (var m in new[] { def, def.PartialImplementationPart, def.PartialDefinitionPart })
            if (m?.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "MissingCapabilityAttribute") is { ConstructorArguments: [{ Value: string cap }] }) return cap;
        return null;
    }

    public static bool HasBody(IMethodSymbol def)
    {
        if (def.IsAbstract || def.IsExtern) return false;
        foreach (var r in def.DeclaringSyntaxReferences)
        {
            var n = r.GetSyntax();
            switch (n)
            {
                case BaseMethodDeclarationSyntax b when b.Body != null || b.ExpressionBody != null: return true;
                case AccessorDeclarationSyntax a when a.Body != null || a.ExpressionBody != null: return true;
                case ArrowExpressionClauseSyntax: return true;
                case LocalFunctionStatementSyntax lf when lf.Body != null || lf.ExpressionBody != null: return true;
                case AnonymousFunctionExpressionSyntax: return true;
            }
        }
        return false;
    }

    bool IsSynthesizable(IMethodSymbol m)
    {
        var def = m.OriginalDefinition;
        if (def.MethodKind == MethodKind.Constructor && def.IsImplicitlyDeclared) return true;
        if (IsPrimaryCtor(def)) return true;
        if (def.MethodKind == MethodKind.StaticConstructor) return true;
        if (def.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet && def.AssociatedSymbol is IPropertySymbol p && AutoPropertyField(p) != null) return true;
        if (def.MethodKind is MethodKind.EventAdd or MethodKind.EventRemove && def.AssociatedSymbol is IEventSymbol e && IsFieldLikeEvent(e)) return true;
        if (def.ContainingType.TypeKind == TypeKind.Delegate && def.Name == "Invoke") return true;
        if (def.MethodKind == MethodKind.Constructor && def.ContainingType.TypeKind == TypeKind.Struct && def.Parameters.Length == 0) return true;
        if (def.ContainingType.IsRecord) return true;
        if (def.ContainingType.IsAnonymousType) return true;
        if (def.IsExtern && def.GetAttributes().Any(a => a.AttributeClass?.Name == "UnsafeAccessorAttribute")) return true;
        return false;
    }

    public static IFieldSymbol? AutoPropertyField(IPropertySymbol p) =>
        p.ContainingType.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, p.OriginalDefinition)
                                                                                  || SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, p));

    /// <summary>`event Action E;` — an event whose storage the compiler provides (Roslyn exposes no backing field).</summary>
    /// <summary>A C# 12 primary constructor of a class or struct (not a record): its parameters are captured state.</summary>
    public static bool IsPrimaryCtor(IMethodSymbol m) =>
        m.MethodKind == MethodKind.Constructor && !m.ContainingType.IsRecord &&
        m.OriginalDefinition.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax { ParameterList: not null });

    // (weak: a long-lived compiler process (--watch) must not keep earlier builds' compilations alive)
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IParameterSymbol, object> s_captured = new();

    /// <summary>Whether a primary-constructor parameter is used beyond the initializers and the base list (by name,
    /// conservatively: a lambda in an initializer counts), so the object keeps it. One only initializing fields has
    /// no storage, as C# gives it none (Utf16Char(char ch) is two bytes).</summary>
    public static bool IsCapturedPrimaryParam(IParameterSymbol p) => (bool)s_captured.GetValue((IParameterSymbol)p.OriginalDefinition, static p => CapturedPrimaryParam(p));

    static bool CapturedPrimaryParam(IParameterSymbol p)
    {
        foreach (var r in p.ContainingType.DeclaringSyntaxReferences)
        {
            if (r.GetSyntax() is not Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax decl) continue;
            foreach (var id in decl.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax>())
            {
                if (id.Identifier.ValueText != p.Name) continue;
                bool initOnly = false;
                for (var n = id.Parent; n != null && n != decl; n = n.Parent)
                {
                    if (n is Microsoft.CodeAnalysis.CSharp.Syntax.AnonymousFunctionExpressionSyntax or Microsoft.CodeAnalysis.CSharp.Syntax.LocalFunctionStatementSyntax) break;
                    if (n is Microsoft.CodeAnalysis.CSharp.Syntax.PrimaryConstructorBaseTypeSyntax or Microsoft.CodeAnalysis.CSharp.Syntax.ParameterListSyntax
                        || n is Microsoft.CodeAnalysis.CSharp.Syntax.EqualsValueClauseSyntax { Parent: Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax or Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax })
                    { initOnly = true; break; }
                }
                if (!initOnly) return true;
            }
        }
        return false;
    }

    public static bool IsFieldLikeEvent(IEventSymbol e) =>
        e.OriginalDefinition.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax);

    public static IFieldSymbol? EventField(IEventSymbol e) =>
        e.ContainingType.GetMembers(e.Name).OfType<IFieldSymbol>().FirstOrDefault()
        ?? e.ContainingType.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, e.OriginalDefinition));

    static readonly System.Collections.Concurrent.ConcurrentDictionary<SyntaxTree, SemanticModel> s_libModels = new();

    public SemanticModel ModelFor(SyntaxTree tree)
    {
        lock (_models)
        {
            if (_models.TryGetValue(tree, out var m)) return m;
            Compilation? comp = Inputs.FirstOrDefault(c => c.ContainsSyntaxTree(tree));
            if (comp == null && Lib.CompilationOf(tree) is { } lc)
            {
                // the library's compilations outlive a build: so do their models, and the bodies they have bound
                // (section 7.6: caching the analysis of library compilations is allowed)
                m = s_libModels.GetOrAdd(tree, t => lc.GetSemanticModel(t));
                _models[tree] = m;
                return m;
            }
            if (comp == null) throw new CompileException("no compilation contains " + tree.FilePath);
            m = comp.GetSemanticModel(tree);
            _models[tree] = m;
            return m;
        }
    }
    // (kept for the whole build: a local function's symbol, and its type parameters, are its semantic model's, so a
    // model dropped and made again gives other symbols than the ones the compiler holds)
    readonly Dictionary<SyntaxTree, SemanticModel> _models = [];

    // ---------------- lib helpers ----------------
    public INamedTypeSymbol LibType(string metadataName) =>
        Lib.TypeByMetadataName(metadataName) ?? throw new CompileException("library type missing: " + metadataName);

    /// <summary>string.Equals(string, string): what `==` on strings is.</summary>
    public MethodInst StringEquals => Method(LibType("System.String").GetMembers("Equals").OfType<IMethodSymbol>()
        .First(x => x.IsStatic && x.Parameters.Length == 2 && x.Parameters.All(p => Types.IsString(p.Type))));

    public MethodInst LibMethod(string type, string name, int arity = -1)
    {
        var t = LibType(type);
        var m = t.GetMembers(name).OfType<IMethodSymbol>().First(x => arity < 0 || x.Parameters.Length == arity);
        return Method(m);
    }

    // ---------------- virtual dispatch ----------------
    public WasmFunction Stub(IMethodSymbol slot)
    {
        slot = CanonicalMethod(slot);
        var key = Types.MethodKey(slot);
        if (Stubs.TryGetValue(key, out var s)) return s.Fn;
        var fn = Module.AddFunction("virtual " + Display(slot), Signature(slot, Types));
        s = new VirtualStub { Slot = slot, Fn = fn, IsInterface = slot.ContainingType.TypeKind == TypeKind.Interface, MadeBy = Compiling };
        Stubs[key] = s;
        return fn;
    }

    /// <summary>The method that runs when <paramref name="slot"/> is called virtually on an object of exact type <paramref name="c"/>.</summary>
    public IMethodSymbol? Implementation(ITypeSymbol c, IMethodSymbol slot)
    {
        c = Types.Canonical(c);
        var slotKey = Types.MethodKey(CanonicalMethod(slot));
        // a non-generic interface of an array (IList, ICollection, IEnumerable, ICloneable...) is System.Array's
        // implementation (IList.Add throws NotSupportedException, as in the CLR)
        if (c is IArrayTypeSymbol && slot.ContainingType is { TypeKind: TypeKind.Interface, IsGenericType: false }) c = LibType("System.Array");
        // an array's class virtuals (ToString, Equals, GetHashCode) are System.Array's (object's)
        if (c is IArrayTypeSymbol && slot.ContainingType.TypeKind == TypeKind.Class) c = LibType("System.Array");
        if (c is IArrayTypeSymbol arrT && slot.ContainingType.TypeKind == TypeKind.Interface)
        {
            // arrays implement the generic collection interfaces through SZArrayHelper (as in the CLR)
            var helper = LibType("System.SZArrayHelper");
            var hm = helper.GetMembers(slot.Name).OfType<IMethodSymbol>().FirstOrDefault(x => x.Parameters.Length == slot.Parameters.Length + 1);
            return hm?.Construct(Types.Canonical(arrT.ElementType));
        }
        if (slot.ContainingType.TypeKind == TypeKind.Interface)
        {
            var ifaceKey = Types.Key(Types.Canonical(slot.ContainingType));
            var defId = Types.DocId(slot.OriginalDefinition);
            for (var b = c as INamedTypeSymbol; b != null; b = b.BaseType is { } bt ? (INamedTypeSymbol)Types.Canonical(bt) : null)
            {
                foreach (var i in b.AllInterfaces)
                {
                    if (Types.Key(Types.Canonical(i)) != ifaceKey && !(slot.ContainingType is INamedTypeSymbol sit && Types.VariantConvertible((INamedTypeSymbol)Types.Canonical(i), (INamedTypeSymbol)Types.Canonical(sit)))) continue;
                    var member = i.GetMembers(slot.Name).OfType<IMethodSymbol>().FirstOrDefault(x => Types.DocId(x.OriginalDefinition) == defId);
                    if (member == null) continue;
                    if (b.FindImplementationForInterfaceMember(member) is IMethodSymbol impl)
                    {
                        // (on the implementing type's own instantiation: ConstructedFrom keeps its type arguments)
                        if (impl.IsGenericMethod || impl.Arity > 0) impl = impl.ConstructedFrom.Construct([.. slot.TypeArguments]);
                        // a virtual implementation may be overridden further down
                        if ((impl.IsVirtual || impl.IsAbstract || impl.IsOverride) && impl.ContainingType.TypeKind != TypeKind.Interface)
                            return Implementation(c, impl);
                        if (!impl.IsAbstract) return CanonicalMethod(impl);
                    }
                    // implemented by an interface's default method
                    var canonIface = (INamedTypeSymbol)Types.Canonical(i);
                    var cm = canonIface.GetMembers(slot.Name).OfType<IMethodSymbol>().FirstOrDefault(x => Types.DocId(x.OriginalDefinition) == defId);
                    if (cm != null && !cm.IsAbstract) return cm.IsGenericMethod ? cm.Construct([.. slot.TypeArguments]) : cm;
                }
            }
            // default implementations in other interfaces (overriding defaults)
            foreach (var i in c.AllInterfaces)
            {
                var ci = (INamedTypeSymbol)Types.Canonical(i);
                foreach (var m in ci.GetMembers().OfType<IMethodSymbol>())
                    if (!m.IsAbstract && m.ExplicitInterfaceImplementations.Any(e => Types.MethodKey(CanonicalMethod(e)) == slotKey))
                        return m;
            }
            return null;
        }
        // class virtual: most-derived override
        for (var b = c as INamedTypeSymbol; b != null; b = b.BaseType is { } bt ? (INamedTypeSymbol)Types.Canonical(bt) : null)
        {
            foreach (var m in b.GetMembers(slot.Name).OfType<IMethodSymbol>())
            {
                if (m.Arity != slot.Arity) continue;
                var mm = m.IsGenericMethod ? m.Construct([.. slot.TypeArguments]) : m;
                if (Types.MethodKey(CanonicalMethod(mm)) == slotKey) return mm.IsAbstract ? null : CanonicalMethod(mm);
                // (a constructed generic method has no overridden method: walk the definition's, constructed alike)
                for (var o = m.IsGenericMethod ? m.OverriddenMethod : mm.OverriddenMethod; o != null; o = o.OverriddenMethod)
                {
                    // (on the base type's own instantiation: ConstructedFrom keeps its type arguments, B<int, int>.W<TK>)
                    var oo = o.IsGenericMethod ? o.ConstructedFrom.Construct([.. slot.TypeArguments]) : o;
                    if (Types.MethodKey(CanonicalMethod(oo)) == slotKey) return mm.IsAbstract ? null : CanonicalMethod(mm);
                }
            }
            if (b.BaseType == null) break;
        }
        return null;
    }

    // ---------------- static initialization ----------------
    readonly Dictionary<string, (IAddress flag, WasmFunction fn)?> _cctors = [];

    /// <summary>For a type with static initialization, the run-once flag and the initializer function.</summary>
    public (IAddress flag, WasmFunction fn)? StaticInit(INamedTypeSymbol t)
    {
        t = (INamedTypeSymbol)Types.Canonical(t);
        var key = Types.Key(t);
        if (_cctors.TryGetValue(key, out var r)) return r;
        var cctor = t.StaticConstructors.FirstOrDefault(c => !c.IsImplicitlyDeclared);
        bool hasInit = t.GetMembers().Any(m => m.IsStatic && (m is IFieldSymbol { IsConst: false } f && FieldInitializer(f) != null
                                                               || m is IPropertySymbol p && PropertyInitializer(p) != null));
        if (cctor == null && !hasInit) { _cctors[key] = null; return null; }
        // state word: 0 not started, -1 done (or started on a single thread), else the id of the thread running it
        var flag = StaticAddress("init:" + key, 4, 4);
        var sym = cctor ?? t.StaticConstructors.FirstOrDefault();
        var fn = Module.AddFunction("static init " + key, new FuncType([], []));
        var body = Module.AddFunction("static init body " + key, new FuncType([], []));
        {
            // an exception from the initializer: TypeInitializationException now and at every later access (-2)
            var saved = StaticAddress("tie:" + key, 4, 4);
            var e = new Emitter(fn);
            // (what every type's initializer does the same way is two functions of the module, not a copy a type:
            // a large program has thousands of types with initializers)
            if (_cctorFailed == null)
            {
                // (flag, saved, name, inner exception: owned): wraps it, keeps the wrapper for later accesses, throws it
                _cctorFailed = Module.AddFunction("rt.cctor_failed", new FuncType([ValType.I32, ValType.I32, ValType.I32, ValType.I32], []));
                var f = new Emitter(_cctorFailed);
                f.Call(Rt.FinUnhold);
                f.Get(1); f.Get(2); f.Get(3);
                f.Call(LibMethod("Cs2Wasm.Runtime.ThrowHelpers", "TypeInitFailed", 2).Fn);
                f.Store(Op.I32Store);
                f.Get(3); f.Call(Rt.Release);
                f.Get(0); f.Call(Rt.CctorFail);
                f.Get(1); f.Load(Op.I32Load); f.Call(Rt.Retain); f.Emit(Op.Throw, 0, 0, ExceptionTag);
                // (flag, saved): the type's initializer failed before: its exception again
                _cctorCheck = Module.AddFunction("rt.cctor_check", new FuncType([ValType.I32, ValType.I32], []));
                var c = new Emitter(_cctorCheck);
                c.Get(0); c.Load(Op.I32Load); c.I32(-2); c.Op(Op.I32Eq);
                c.If(); c.Get(1); c.Load(Op.I32Load); c.Call(Rt.Retain); c.Emit(Op.Throw, 0, 0, ExceptionTag); c.End();
            }
            e.I32Addr(flag); e.Call(Rt.CctorEnter);
            e.If();
            {
                var caught = e.Block(ValType.I32);
                e.TryTable(null, (0, ExceptionTag, caught));
                // (threads: the initializer passes no safe point, so code that touches a type for the first time
                // keeps what it borrowed: FieldBorrows.cs)
                e.Call(Rt.FinHold);
                e.Call(body);
                e.Call(Rt.FinUnhold);
                e.End();
                e.I32Addr(flag); e.Call(Rt.CctorExit);
                e.Op(Op.Return);
                e.End();
                // inner exception on the stack (owned): wrapped and thrown
                int inner = e.AddLocal(ValType.I32);
                e.Set(inner);
                e.I32Addr(flag); e.I32Addr(saved); e.I32Addr(StringLiteral(t.ContainingType != null ? t.MetadataName : ClrName(t))); e.Get(inner);
                e.Call(_cctorFailed);
                e.Op(Op.Unreachable);
            }
            e.Else();
            e.I32Addr(flag); e.I32Addr(saved); e.Call(_cctorCheck!);
            e.End();
        }
        _cctors[key] = (flag, fn);
        _pendingCctors.Enqueue((t, body, flag, cctor));
        return _cctors[key];
    }
    WasmFunction? _cctorFailed, _cctorCheck;
    readonly Queue<(INamedTypeSymbol, WasmFunction, IAddress, IMethodSymbol?)> _pendingCctors = new();
    readonly List<(INamedTypeSymbol, WasmFunction, IAddress, IMethodSymbol?)> _compiledCctors = [];

    /// <summary>Static fields some compiled code reads (or takes the address of), by owner and name, with the functions
    /// that read them (a static initializer compiled again forgets its reads first).</summary>
    public readonly StaticReadSet StaticReads = new();

    internal sealed class StaticReadSet
    {
        readonly Dictionary<string, HashSet<WasmFunction?>> _by = [];
        public Driver? Owner;
        public void Add(string key)
        {
            if (!_by.TryGetValue(key, out var s)) _by[key] = s = [];
            s.Add(Owner?.CurrentFn);
        }
        public bool Contains(string key) => _by.TryGetValue(key, out var s) && s.Count > 0;
        public void Forget(WasmFunction fn) { foreach (var s in _by.Values) s.Remove(fn); }
    }
    public static string StaticKey(ITypeSymbol owner, ISymbol storage) => Types.Key(owner) + "::" + storage.Name;
    /// <summary>The second compilation of the static initializers: initializers of unread fields that have no effect are left out.</summary>
    public bool SkipUnreadInits;
    /// <summary>For each type with static initializers: the static keys of the fields each pure initializer sets
    /// (FunctionCompiler.UnreadPureInits).</summary>
    public readonly Dictionary<INamedTypeSymbol, string[][]> PureInitKeys = new(SymbolEqualityComparer.Default);

    public static EqualsValueClauseSyntax? FieldInitializer(IFieldSymbol f) =>
        f.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<VariableDeclaratorSyntax>().FirstOrDefault()?.Initializer;

    public static EqualsValueClauseSyntax? PropertyInitializer(IPropertySymbol p) =>
        p.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<PropertyDeclarationSyntax>().FirstOrDefault()?.Initializer;

    public bool BeforeFieldInit(INamedTypeSymbol t) => !t.StaticConstructors.Any(c => !c.IsImplicitlyDeclared);

    // ---------------- driving ----------------
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    readonly bool _times = Environment.GetEnvironmentVariable("CS2WASM_TIMES") == "1";
    /// <summary>CS2WASM_TIMES=1: milliseconds since the last mark, per phase.</summary>
    /// <summary>Time spent binding method bodies (Roslyn's GetOperation), for CS2WASM_TIMES.</summary>
    public static long BindTicks;
    public void Mark(string phase)
    {
        if (!_times) return;
        long ins = 0, cap = 0; foreach (var f in Module.Functions) { ins += f.Body.Count; cap += f.Body.Capacity; }
        Console.Error.WriteLine($"  {_clock.ElapsedMilliseconds,6} ms  {GC.GetTotalMemory(false) >> 20,6} MB  {phase}  ({Module.Functions.Count} functions, {ins / 1000000.0:F1}M instructions in room for {cap / 1000000.0:F1}M)");
        _clock.Restart();
    }

    public void Run()
    {
        if (_dynamicError != null) throw _dynamicError;
        Mark("start (library ready)");
        foreach (var e in Main.GetDiagnostics())
            if (e.Severity == DiagnosticSeverity.Error) { Diagnostics.Add(e); }
        if (Diagnostics.Count > 0) return;
        foreach (var e in Lib.Errors) Report(Diags.LibraryError, e.Location, e.GetMessage());
        if (Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)) return;

        Arenas = new Arenas(this);
        var rt = new RuntimeGen(this);
        rt.Prepare();
        FindRoots(rt);
        Mark("program diagnostics, roots");
        RunPluginsBeforeCompile(rt);
        Drain(rt);
        Mark("compile (first drain)");
        RunPlugins(rt);
        rt.FinishExports();
        Drain(rt);
        Mark("exports, reflection (second drain)");
        // static fields nothing reads: the whole program is known now, so their initializers that have no effect go
        // (and with them what only they reached, which the encoder no longer finds live)
        SkipUnreadInits = true;
        // in rounds: an initializer left out reads nothing any more, so what only it read may go next (a method bind
        // read only by its method, the name only that bind's initializer read)
        StaticReads.Owner = this;
        var dropped = new Dictionary<WasmFunction, int>();
        long tScan = 0, tBy = 0, tComp = 0, tDrain = 0; int rounds = 0, redone = 0;
        var swp = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            swp.Restart();
            var redo = _compiledCctors.Where(c => FunctionCompiler.UnreadPureInits(this, c.Item1) > dropped.GetValueOrDefault(c.Item2)).ToList();
            tScan += swp.ElapsedMilliseconds; swp.Restart();
            if (redo.Count == 0) break;
            rounds++; redone += redo.Count;
            var recompiled = redo.Select(c => c.Item2).ToHashSet();
            foreach (var by in ConstructedBy.Values) by.RemoveWhere(f => f != null && recompiled.Contains(f));
            tBy += swp.ElapsedMilliseconds; swp.Restart();
            foreach (var (t, fn, flag, cctor) in redo)
            {
                // (only the ones that lose an initializer: the others stay as they were compiled)
                dropped[fn] = FunctionCompiler.UnreadPureInits(this, t);
                StaticReads.Forget(fn);
                fn.Body.Clear(); fn.Locals.Clear();
                CurrentFn = fn;
                try { FunctionCompiler.CompileStaticInit(this, t, fn, flag, cctor); }
                catch (CompileException ex) { Report(Diags.Unsupported, ex.Location ?? t.Locations.FirstOrDefault(), ex.Message); }
            }
            CurrentFn = null;
            tComp += swp.ElapsedMilliseconds; swp.Restart();
            Drain(rt);
            tDrain += swp.ElapsedMilliseconds;
        }
        if (_times) Console.Error.WriteLine($"    static initializers again: {rounds} rounds, {redone} of {_compiledCctors.Count} compiled again; finding them {tScan} ms, constructed-by {tBy} ms, compiling {tComp} ms, drain {tDrain} ms");
        Mark("  static initializers again");
        // cycles (section 4.3): the type graph is complete only now; arena runtime is compiled only if some site needs it
        Arenas.Analyze();
        Mark("  arenas: analyze");
        if (Arenas.Enabled) { Arenas.RequestRuntime(); Drain(rt); }
        Mark("  arenas: runtime drained");
        Arenas.Resolve();
        Mark("static initializers again, arenas");
        ReportEngineThreadBlocking(rt.UserExports);
        rt.Finish();
        Mark("finish (tables, dispatch)");
        if (Environment.GetEnvironmentVariable("CS2WASM_STATS") is "1" or "2") PrintStats();
    }

    /// <summary>CS2WASM_STATS=1: what the program reaches, by namespace and by type (instructions), largest first.</summary>
    void PrintStats()
    {
        // (what the encoder keeps: reachable from the exports through calls and table loads)
        var liveFns = Module.LiveFunctions();
        var compiled = AllMethods.Where(m => m.Compiled && liveFns.Contains(m.Fn)).ToList();
        string Ns(MethodInst m) => m.Sym.ContainingType?.ContainingNamespace?.ToDisplayString() ?? "?";
        string Ty(MethodInst m) => m.Sym.ContainingType is { } t ? Types.FullName(t.OriginalDefinition) : "?";
        Console.Error.WriteLine($"{compiled.Count} methods, {compiled.Sum(m => m.Fn.Body.Count)} instructions");
        Console.Error.WriteLine($"  data {Data.Bytes.Count} bytes: string literals {_literalBytes} ({_literals.Count}), blobs {_blobs!.Values.Sum()}");
        foreach (var (k, n) in _blobs.OrderByDescending(x => x.Value).Take(12)) Console.Error.WriteLine($"    {n,7} {k}");
        var spec = compiled.Where(m => m.Consts != null).ToList();
        Console.Error.WriteLine($"  {spec.Count} constant-argument copies, {spec.Sum(m => m.Fn.Body.Count)} instructions");
        if (Environment.GetEnvironmentVariable("CS2WASM_STATS") == "2")
            foreach (var m in spec.OrderByDescending(m => m.Fn.Body.Count).Take(40)) Console.Error.WriteLine($"    {m.Fn.Body.Count,6} {m.Fn.Name}");
        foreach (var g in compiled.GroupBy(Ns).OrderByDescending(g => g.Sum(m => m.Fn.Body.Count)).Take(25))
            Console.Error.WriteLine($"  {g.Sum(m => m.Fn.Body.Count),8} {g.Count(),5}  {g.Key}");
        // CS2WASM_WHY_TYPE=Name: how the program reaches that type's first method
        if (Environment.GetEnvironmentVariable("CS2WASM_WHY_TYPE") is { Length: > 0 } why)
            foreach (var name in why.Split(','))
                if (compiled.FirstOrDefault(m => Ty(m).EndsWith(name)) is { } first)
                {
                    var chain = new List<string>();
                    for (var x = first; x != null && chain.Count < 40; x = x.ReachedFrom) chain.Add(Display(x.Sym));
                    Console.Error.WriteLine($"why {name}: " + string.Join(" <- ", chain));
                }
        // CS2WASM_WHY_METHODS=Text: how the program reaches each compiled method whose name contains it (the 15 largest)
        if (Environment.GetEnvironmentVariable("CS2WASM_WHY_METHODS") is { Length: > 0 } whyM)
            foreach (var m in compiled.Where(m => Display(m.Sym).Contains(whyM)).OrderByDescending(m => m.Fn.Body.Count).Take(15))
                Console.Error.WriteLine($"why {m.Fn.Body.Count} {Display(m.Sym)}{WhyReachedAlways(m)}");
        // CS2WASM_WHY_STUBS=Text: which method first calls through each matching dispatch stub, and its targets
        if (Environment.GetEnvironmentVariable("CS2WASM_WHY_STUBS") is { Length: > 0 } whyS)
            foreach (var (k, st) in Stubs.Where(x => x.Key.Contains(whyS)))
                Console.Error.WriteLine($"stub {k} ({st.Cases.Count} targets) <- {(st.MadeBy != null ? Display(st.MadeBy.Sym) + WhyReachedAlways(st.MadeBy) : "?")}");
        // CS2WASM_STATS_TREE=depth: the first-reach tree (each method under the method whose compilation first asked for
        // it), with each subtree's instructions, the large subtrees only
        if (int.TryParse(Environment.GetEnvironmentVariable("CS2WASM_STATS_TREE"), out int depth))
        {
            var kids = new Dictionary<MethodInst, List<MethodInst>>();
            var roots = new List<MethodInst>();
            foreach (var m in compiled)
                if (m.ReachedFrom is { } p && p.Compiled) { if (!kids.TryGetValue(p, out var l)) kids[p] = l = []; l.Add(m); } else roots.Add(m);
            var size = new Dictionary<MethodInst, long>();
            long Size(MethodInst m)
            {
                if (size.TryGetValue(m, out var v)) return v;
                size[m] = 0;
                v = m.Fn.Body.Count + (kids.TryGetValue(m, out var l) ? l.Sum(Size) : 0);
                size[m] = v;
                return v;
            }
            long total = compiled.Sum(m => (long)m.Fn.Body.Count);
            void Print(MethodInst m, int d)
            {
                Console.Error.WriteLine($"{new string(' ', d * 2)}{Size(m),7} {Display(m.Sym)}");
                if (d >= depth || !kids.TryGetValue(m, out var l)) return;
                foreach (var c in l.OrderByDescending(Size).Where(c => Size(c) * 100 >= total).Take(12)) Print(c, d + 1);
            }
            foreach (var r in roots.OrderByDescending(Size).Where(r => Size(r) * 200 >= total)) Print(r, 0);
        }
        // CS2WASM_WHY_LIVE=Text: the shortest chain of calls (or table loads) from an export to each live function whose
        // name contains it (what keeps it in the module)
        if (Environment.GetEnvironmentVariable("CS2WASM_WHY_LIVE") is { Length: > 0 } whyL)
        {
            var parent = new Dictionary<WasmFunction, WasmFunction?>();
            var q = new Queue<WasmFunction>();
            foreach (var f in Module.Functions) if (f.Exports.Count > 0 || f.TableRoot) { parent[f] = null; q.Enqueue(f); }
            while (q.Count > 0)
            {
                var f = q.Dequeue();
                var next = f.Body.Where(i => i.Ref is WasmFunction && (i.Op == Op.Call || i.Op == Op.I32Const)).Select(i => (WasmFunction)i.Ref!);
                if (f.ForwardTo != null) next = next.Append(f.ForwardTo);
                foreach (var g in next) if (!parent.ContainsKey(g)) { parent[g] = f; q.Enqueue(g); }
            }
            foreach (var f in parent.Keys.Where(f => f.Name.Contains(whyL)).Take(8))
            {
                var chain = new List<string>();
                for (WasmFunction? x = f; x != null && chain.Count < 30; x = parent[x]) chain.Add(x.Name.Length > 90 ? x.Name[..90] : x.Name);
                Console.Error.WriteLine("live " + string.Join(" <- ", chain));
            }
        }
        Console.Error.WriteLine("types:");
        foreach (var g in compiled.GroupBy(Ty).OrderByDescending(g => g.Sum(m => m.Fn.Body.Count)).Take(40))
            Console.Error.WriteLine($"  {g.Sum(m => m.Fn.Body.Count),8} {g.Count(),5}  {g.Key}");
    }

    void Drain(RuntimeGen rt)
    {
        bool progress = true;
        while (progress)
        {
            progress = false;
            while (_work.Count > 0 || _pendingCctors.Count > 0)
            {
                progress = true;
                if (_work.Count > 0)
                {
                    var mi = _work.Dequeue();
                    if (mi.Compiled) continue;
                    mi.Compiled = true;
                    // (the program starts threads: it is compiled as one that does, from the start; nothing more of
                    // this compilation is of use)
                    if (!ThreadMode && !s_expNoThreadMode && !s_noThreads && mi.Sym.Name == "SpawnCore" && mi.Sym.ContainingType?.Name == "Threads") throw new ThreadsSeen();
                    Compiling = mi;
                    CurrentFn = mi.Fn;
                    try { if (mi.Compile != null) mi.Compile(); else FunctionCompiler.Compile(this, mi); mi.Fn.Body.TrimExcess(); mi.Fn.Locals.TrimExcess(); }
                    catch (CompileException ex)
                    {
                        if (Environment.GetEnvironmentVariable("CS2WASM_DEBUG") != null) Console.Error.WriteLine(ex.StackTrace);
                        Report(Diags.Unsupported, ex.Location ?? mi.Sym.Locations.FirstOrDefault(), ex.Message);
                        mi.Fn.Body.Clear(); mi.Fn.Locals.Clear();
                        mi.Fn.Body.Add(new Instr { Op = Op.Unreachable, Pos = -1 });
                    }
                    catch (Exception ex)
                    {
                        // an internal error: reported against the method, and the rest of the program still compiles
                        if (Environment.GetEnvironmentVariable("CS2WASM_DEBUG") != null) Console.Error.WriteLine(ex.ToString());
                        var frame = new System.Diagnostics.StackTrace(ex, true).GetFrames().FirstOrDefault(f => f.GetFileName() != null);
                        Report(Diags.Unsupported, mi.Sym.Locations.FirstOrDefault(), $"internal error compiling {Display(mi.Sym)}: {ex.GetType().Name}: {ex.Message} at {Path.GetFileName(frame?.GetFileName())}:{frame?.GetFileLineNumber()}");
                        mi.Fn.Body.Clear(); mi.Fn.Locals.Clear();
                        mi.Fn.Body.Add(new Instr { Op = Op.Unreachable, Pos = -1 });
                    }
                }
                else
                {
                    var (t, fn, flag, cctor) = _pendingCctors.Dequeue();
                    _compiledCctors.Add((t, fn, flag, cctor));
                    CurrentFn = fn;
                    try { FunctionCompiler.CompileStaticInit(this, t, fn, flag, cctor); }
                    catch (CompileException ex) { Report(Diags.Unsupported, ex.Location ?? t.Locations.FirstOrDefault(), ex.Message); }
                }
                CurrentFn = null;
            }
            Compiling = null; // what virtual dispatch adds is reached by no one method
            CurrentFn = null;
            if (rt.Expand()) progress = true;
        }
    }

    readonly HashSet<string> _exportNames = [];
    void FindRoots(RuntimeGen rt)
    {
        foreach (var comp in Inputs) FindRootsIn(comp, rt);
    }

    void FindRootsIn(Compilation comp, RuntimeGen rt)
    {
        var seen = _exportNames;
        {
            foreach (var t in AllTypes(comp.Assembly.GlobalNamespace))
                foreach (var m in t.GetMembers().OfType<IMethodSymbol>())
                {
                    var uco = m.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "UnmanagedCallersOnlyAttribute");
                    if (uco == null) continue;
                    var entry = uco.NamedArguments.FirstOrDefault(a => a.Key == "EntryPoint").Value.Value as string;
                    if (entry == null) { Report(Diags.ExportNeedsName, m.Locations.FirstOrDefault(), m.Name); continue; }
                    if (!m.IsStatic || m.IsGenericMethod) { Report(Diags.ExportShape, m.Locations.FirstOrDefault(), m.Name); continue; }
                    if (!seen.Add(entry)) { Report(Diags.DuplicateExport, m.Locations.FirstOrDefault(), entry); continue; }
                    rt.Export(entry, Method(m));
                }
        }
    }

    public static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol ns)
    {
        foreach (var m in ns.GetMembers())
        {
            if (m is INamespaceSymbol n) { foreach (var t in AllTypes(n)) yield return t; }
            else if (m is INamedTypeSymbol t && t.Locations.Any(l => l.IsInSource))
            {
                yield return t;
                foreach (var nt in Nested(t)) yield return nt;
            }
        }
    }
    static IEnumerable<INamedTypeSymbol> Nested(INamedTypeSymbol t)
    {
        foreach (var n in t.GetTypeMembers()) { yield return n; foreach (var x in Nested(n)) yield return x; }
    }

    public IEnumerable<MethodInst> AllMethods => _methods.Values;

    // ---------------- final memory layout ----------------
    public void LayoutMemory()
    {
        CompactData();
        while (Data.Bytes.Count % 16 != 0) Data.Bytes.Add(0);
        StaticBase = DataBase + Data.Bytes.Count;
        int staticEnd = StaticBase + (_staticSize + 15) / 16 * 16;
        // (the first thread's stack lies above the statics; a frame is taken from it by rt.frame, which traps when
        // the stack has not that much left: Wasm/Frames.cs)
        StackTop = staticEnd + Options.StackSize;
        HeapBase = StackTop + 16;
        // The module says how much memory it needs to start at all: its data, statics and stack, and a first
        // megabyte of heap (a program with large resources needs more than the default; the host reads the number).
        long need = ((long)HeapBase + (1 << 20) + 65535) / 65536;
        if (need > Module.MemoryMinPages) Module.MemoryMinPages = (int)Math.Min(need, Module.MemoryMaxPages);
    }

    public int Version => 1;
}
