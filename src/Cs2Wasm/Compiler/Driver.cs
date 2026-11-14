using System.Text;
using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
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
    /// <summary>Set for lambdas and local functions: compiles the body with its closure information.</summary>
    public Action? Compile;
}

sealed class StaticSlot(Driver d, int offset) : IAddress
{
    public int Offset = offset;
    public int Address => d.StaticBase + Offset;
}

sealed class DataAddr(Driver d, int offset) : IAddress
{
    public int Address => d.DataBase + offset;
}

sealed class LateAddr(Func<int> f) : IAddress { public int Address => f(); }

sealed class VirtualStub
{
    public required IMethodSymbol Slot;
    public required WasmFunction Fn;
    public bool IsInterface;
    public Dictionary<string, (ITypeSymbol type, MethodInst? impl, bool boxed)> Cases = [];
}

internal sealed partial class Driver
{
    public readonly CSharpCompilation Main;
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
        return off;
    }
    /// <summary>True once the program can start a thread: counts become atomic and the runtime lock is real.</summary>
    public bool UsesThreads => AllMethods.Any(m => m.Sym.Name == "SpawnCore" && m.Sym.ContainingType?.Name == "Threads" && m.Compiled);
    public readonly WasmTag ExceptionTag;
    readonly List<SourcePos> _positions = [];
    readonly Dictionary<SourcePos, int> _posIndex = [];
    public readonly List<string> Files = [];
    readonly Dictionary<string, int> _fileIndex = [];
    public readonly Dictionary<string, (string module, string name)> BoundaryByDocId = [];
    public readonly List<string> NonDeterminismNotes = [];

    public Driver(CSharpCompilation main, CompileOptions options)
    {
        Main = main;
        Options = options;
        _inputs = [main, .. options.AdditionalCompilations];
        Lib = new Library(main.References);
        Types = new Types(this);
        StackPointer = new WasmGlobal("__stack_pointer", ValType.I32, true, 0) { Export = "__stack_pointer" };
        Module.Globals.Add(StackPointer);
        ThreadId = new WasmGlobal("__thread_id", ValType.I32, true, 1);
        Module.Globals.Add(ThreadId);
        TlsBase = new WasmGlobal("__tls_base", ValType.I32, true, 0) { Export = "__tls_base" };
        Module.Globals.Add(TlsBase);
        StackLo = new WasmGlobal("__stack_lo", ValType.I32, true, 0); Module.Globals.Add(StackLo);
        StackHi = new WasmGlobal("__stack_hi", ValType.I32, true, 0); Module.Globals.Add(StackHi);
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

    public int MarkConstructed(ITypeSymbol t)
    {
        var id = TypeId(t);
        Constructed.Add(id);
        return id;
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
    public IAddress StringLiteral(string s)
    {
        if (_literals.TryGetValue(s, out var a)) return a;
        // header: rc = -1 (immortal), typeId 1 (string), hash 0; then length, chars, terminator
        Align(8);
        int off = Data.Bytes.Count;
        AddI32(-1); AddI32(1); AddI32(0); AddI32(s.Length);
        foreach (var c in s) { Data.Bytes.Add((byte)c); Data.Bytes.Add((byte)(c >> 8)); }
        Data.Bytes.Add(0); Data.Bytes.Add(0);
        a = new DataAddr(this, off);
        _literals[s] = a;
        return a;
    }

    public IAddress DataBlob(byte[] bytes, int align)
    {
        Align(align);
        int off = Data.Bytes.Count;
        Data.Bytes.AddRange(bytes);
        return new DataAddr(this, off);
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
    public MethodInst Method(IMethodSymbol m)
    {
        m = CanonicalMethod(m);
        if (m.IsGenericMethod && m.TypeArguments.Any(a => a is not INamedTypeSymbol and not IArrayTypeSymbol and not IPointerTypeSymbol))
        {
            // leave as-is; callers substitute first
        }
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
        mi = new MethodInst { Sym = m, Fn = fn, Key = key, Impl = impl };
        fn.FrameName = IsHiddenFrame(m) ? null : FrameName(m);
        _methods[key] = mi;
        if (impl is MethodImpl.Body or MethodImpl.Synthesized or MethodImpl.Missing) _work.Enqueue(mi);
        return mi;
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

    public bool IsLibrary(ISymbol s) => s.Locations.Any(l => l.IsInSource && l.SourceTree != null && LibCompilation.ContainsSyntaxTree(l.SourceTree));

    static bool HasAttr(ISymbol s, string fullName) =>
        s.GetAttributes().Any(a => a.AttributeClass != null && Types.FullName(a.AttributeClass) == fullName);

    public bool IsIntrinsic(IMethodSymbol m) => IsLibrary(m) && HasAttr(m.OriginalDefinition, "Cs2Wasm.Runtime.IntrinsicAttribute");

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
        if (HasBody(def)) return MethodImpl.Body;
        if (IsSynthesizable(m)) return MethodImpl.Synthesized;
        return MethodImpl.Missing;
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

    public static bool IsFieldLikeEvent(IEventSymbol e) =>
        e.OriginalDefinition.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax);

    public static IFieldSymbol? EventField(IEventSymbol e) =>
        e.ContainingType.GetMembers(e.Name).OfType<IFieldSymbol>().FirstOrDefault()
        ?? e.ContainingType.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, e.OriginalDefinition));

    public SemanticModel ModelFor(SyntaxTree tree)
    {
        lock (_models)
        {
            if (_models.TryGetValue(tree, out var m)) return m;
            Compilation? comp = Inputs.FirstOrDefault(c => c.ContainsSyntaxTree(tree));
            if (comp == null && LibCompilation.ContainsSyntaxTree(tree)) comp = LibCompilation;
            if (comp == null) throw new CompileException("no compilation contains " + tree.FilePath);
            m = comp.GetSemanticModel(tree);
            _models[tree] = m;
            return m;
        }
    }
    readonly Dictionary<SyntaxTree, SemanticModel> _models = [];

    // ---------------- lib helpers ----------------
    public INamedTypeSymbol LibType(string metadataName) =>
        LibCompilation.Assembly.GetTypeByMetadataName(metadataName) ?? throw new CompileException("library type missing: " + metadataName);

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
        s = new VirtualStub { Slot = slot, Fn = fn, IsInterface = slot.ContainingType.TypeKind == TypeKind.Interface };
        Stubs[key] = s;
        return fn;
    }

    /// <summary>The method that runs when <paramref name="slot"/> is called virtually on an object of exact type <paramref name="c"/>.</summary>
    public IMethodSymbol? Implementation(ITypeSymbol c, IMethodSymbol slot)
    {
        c = Types.Canonical(c);
        var slotKey = Types.MethodKey(CanonicalMethod(slot));
        if (c is IArrayTypeSymbol arrT && slot.ContainingType.TypeKind == TypeKind.Interface)
        {
            // arrays implement the collection interfaces through SZArrayHelper (as in the CLR)
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
                    if (Types.Key(Types.Canonical(i)) != ifaceKey) continue;
                    var member = i.GetMembers(slot.Name).OfType<IMethodSymbol>().FirstOrDefault(x => Types.DocId(x.OriginalDefinition) == defId);
                    if (member == null) continue;
                    if (b.FindImplementationForInterfaceMember(member) is IMethodSymbol impl)
                    {
                        if (impl.IsGenericMethod || impl.Arity > 0) impl = impl.OriginalDefinition.Construct([.. slot.TypeArguments]);
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
                var mm = m.IsGenericMethod ? m.Construct([.. slot.TypeArguments]) : m;
                if (Types.MethodKey(CanonicalMethod(mm)) == slotKey) return mm.IsAbstract ? null : CanonicalMethod(mm);
                for (var o = mm.OverriddenMethod; o != null; o = o.OverriddenMethod)
                {
                    var oo = o.IsGenericMethod && o.TypeArguments.Any(a => a is ITypeParameterSymbol) ? o.OriginalDefinition.Construct([.. slot.TypeArguments]) : o;
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
            e.I32Addr(flag); e.Call(Rt.CctorEnter);
            e.If();
            {
                var done = e.Block();
                var caught = e.Block(ValType.I32);
                e.TryTable(null, (0, ExceptionTag, caught));
                e.Call(body);
                e.End();
                e.I32Addr(flag); e.Call(Rt.CctorExit);
                e.Br(done);
                e.End();
                // inner exception on the stack (owned): wrap it
                int inner = e.AddLocal(ValType.I32);
                e.Set(inner);
                e.I32Addr(saved); e.I32Addr(StringLiteral(t.ContainingType != null ? t.MetadataName : ClrName(t))); e.Get(inner);
                e.Call(LibMethod("Cs2Wasm.Runtime.ThrowHelpers", "TypeInitFailed", 2).Fn);
                e.Store(Op.I32Store);
                e.Get(inner); e.Call(Rt.Release);
                e.I32Addr(flag); e.Call(Rt.CctorFail);
                e.I32Addr(saved); e.Load(Op.I32Load); e.Call(Rt.Retain); e.Emit(Op.Throw, 0, 0, ExceptionTag);
                e.End();
            }
            e.Else();
            e.I32Addr(flag); e.Load(Op.I32Load); e.I32(-2); e.Op(Op.I32Eq);
            e.If(); e.I32Addr(saved); e.Load(Op.I32Load); e.Call(Rt.Retain); e.Emit(Op.Throw, 0, 0, ExceptionTag); e.End();
            e.End();
        }
        _cctors[key] = (flag, fn);
        _pendingCctors.Enqueue((t, body, flag, cctor));
        return _cctors[key];
    }
    readonly Queue<(INamedTypeSymbol, WasmFunction, IAddress, IMethodSymbol?)> _pendingCctors = new();

    public static EqualsValueClauseSyntax? FieldInitializer(IFieldSymbol f) =>
        f.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<VariableDeclaratorSyntax>().FirstOrDefault()?.Initializer;

    public static EqualsValueClauseSyntax? PropertyInitializer(IPropertySymbol p) =>
        p.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<PropertyDeclarationSyntax>().FirstOrDefault()?.Initializer;

    public bool BeforeFieldInit(INamedTypeSymbol t) => !t.StaticConstructors.Any(c => !c.IsImplicitlyDeclared);

    // ---------------- driving ----------------
    public void Run()
    {
        foreach (var e in Main.GetDiagnostics())
            if (e.Severity == DiagnosticSeverity.Error) { Diagnostics.Add(e); }
        if (Diagnostics.Count > 0) return;
        foreach (var e in Lib.Errors) Report(Diags.LibraryError, e.Location, e.GetMessage());
        if (Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)) return;

        Arenas = new Arenas(this);
        var rt = new RuntimeGen(this);
        rt.Prepare();
        FindRoots(rt);
        Drain(rt);
        RunPlugins(rt);
        rt.FinishExports();
        Drain(rt);
        // cycles (section 4.3): the type graph is complete only now; arena runtime is compiled only if some site needs it
        Arenas.Analyze();
        if (Arenas.Enabled) { Arenas.RequestRuntime(); Drain(rt); }
        Arenas.Resolve();
        ReportEngineThreadBlocking(rt.UserExports);
        rt.Finish();
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
                    try { if (mi.Compile != null) mi.Compile(); else FunctionCompiler.Compile(this, mi); }
                    catch (CompileException ex)
                    {
                        Report(Diags.Unsupported, ex.Location ?? mi.Sym.Locations.FirstOrDefault(), ex.Message);
                        mi.Fn.Body.Clear(); mi.Fn.Locals.Clear();
                        mi.Fn.Body.Add(new Instr { Op = Op.Unreachable, Pos = -1 });
                    }
                }
                else
                {
                    var (t, fn, flag, cctor) = _pendingCctors.Dequeue();
                    try { FunctionCompiler.CompileStaticInit(this, t, fn, flag, cctor); }
                    catch (CompileException ex) { Report(Diags.Unsupported, ex.Location ?? t.Locations.FirstOrDefault(), ex.Message); }
                }
            }
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
        while (Data.Bytes.Count % 16 != 0) Data.Bytes.Add(0);
        StaticBase = DataBase + Data.Bytes.Count;
        int staticEnd = StaticBase + (_staticSize + 15) / 16 * 16;
        StackTop = staticEnd + Options.StackSize;
        HeapBase = StackTop + 16;
    }

    public int Version => 1;
}
