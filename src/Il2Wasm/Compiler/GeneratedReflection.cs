using Il2Wasm.Wasm;
using Microsoft.CodeAnalysis;

namespace Il2Wasm.Compiler;

/// <summary>
/// Reflection on what the C# compiler makes (section 6.3): the methods of lambdas and local functions (their
/// RoslynNames.cs names, on `&lt;&gt;c`, a display class or the outer method's type), state machines' MoveNext (on
/// `&lt;M&gt;d__N`), and those classes as types (their names, declaring type, [CompilerGenerated]); the kickoff
/// methods' [IteratorStateMachine] / [AsyncStateMachine(typeof(...))] and [DebuggerStepThrough]. A record is made
/// for each such method the program compiled, only when code asks frames for their methods (StackFrame.GetMethod,
/// TargetSite, GetCurrentMethod) or a delegate for its method: a program that does neither has none of it.
/// </summary>
internal sealed partial class Driver
{
    /// <summary>A class the C# compiler makes, as reflection sees it (a type id without a symbol).</summary>
    internal sealed class GenType
    {
        public required string Short, Full;
        /// <summary>The internal id of the type it is nested in.</summary>
        public int DeclaringId = -1;
        public required string Namespace;
        public IAssemblySymbol? Assembly;
        public bool IsValueType, Serializable, BeforeFieldInit;
        public List<ITypeSymbol> Interfaces = [];
    }

    internal readonly Dictionary<int, GenType> GenTypes = [];
    readonly Dictionary<string, int> _genTypeByFull = [];
    /// <summary>Whether code asks a delegate for its method (Delegate.Method, GetMethodInfo).</summary>
    bool _wantsDelegateMethod;
    bool WantsGenerated => FramesAsked || _wantsDelegateMethod;
    /// <summary>Whether the program's code asks a delegate for its target (Delegate.Target): a lambda that captures
    /// nothing answers with the singleton of its `&lt;&gt;c` class, as under .NET (the delegate itself holds what it
    /// always held, and is invoked as before: only the property looks, EmitDelegateSingleton).</summary>
    bool _wantsDelegateTarget;
    bool WantsGenTypes => WantsGenerated || _wantsDelegateTarget;
    /// <summary>The functions of the lambdas on a singleton class, and the class's id.</summary>
    readonly Dictionary<WasmFunction, int> _singletonOfFn = [];
    /// <summary>What a lambda's or local function's code is called with (the delegate's target field): the
    /// environment where it was made, and the key of `this` in it.</summary>
    internal readonly Dictionary<WasmFunction, (Display? env, ISymbol? thisKey)> NestedEnvs = [];
    /// <summary>The functions whose delegate's Target is not what the delegate holds, and what it is: no object
    /// (a local function that captures nothing: a static method), or an object found from the one held (so many
    /// environments up, then, for a lambda that captures `this` alone, the field `this` is kept in).</summary>
    readonly Dictionary<WasmFunction, (bool none, int up, int thisOffset)> _targetOfFn = [];

    /// <summary>Under .NET the target is the environment the function captures nearest (its class) or `this`; the
    /// delegate here holds the innermost environment of the place it was made in.</summary>
    void NoteTargetRule(WasmFunction fn, ClosureNames names, CFn f)
    {
        if (f.Kind == ClosureKind.Static) { _targetOfFn[fn] = (true, 0, -1); return; }
        if (!NestedEnvs.TryGetValue(fn, out var ne)) return;
        int up = 0;
        for (var x = ne.env; x != null; x = x.Parent, up++)
        {
            // (a state machine's environments are held and linked their own way: left as they are)
            if (x.Owner is not FunctionCompiler || x.RoslynFull != null) return;
            if (f.Kind == ClosureKind.ThisOnly)
            {
                if (ne.thisKey == null || !x.Slots.TryGetValue(ne.thisKey, out var slot)) continue;
                _targetOfFn[fn] = (false, up, slot.offset);
                return;
            }
            if (RoslynDisplayName(x, false) == f.Container!.Name)
            {
                if (up > 0) _targetOfFn[fn] = (false, up, -1);
                return;
            }
        }
    }

    /// <summary>The functions compiled for lambdas and local functions: the outer method (definition) and syntax.</summary>
    internal readonly Dictionary<WasmFunction, (IMethodSymbol outer, SyntaxNode syntax)> NestedFns = [];

    internal sealed record MachineRec(WasmFunction MoveNext, IMethodSymbol Kickoff, Display Machine, bool Iterator, bool Async, WasmFunction? NestedOwner);
    internal readonly List<MachineRec> Machines = [];
    readonly Dictionary<IMethodSymbol, int> _machineIds = new(SymbolEqualityComparer.Default);

    int GenTypeId(string full, string shortName, int declaringId, INamedTypeSymbol outerType, bool valueType = false, bool serializable = false, bool beforeFieldInit = false)
    {
        if (_genTypeByFull.TryGetValue(full, out var id)) return id;
        id = TypeById.Count;
        TypeById.Add(ObjectType);
        GenTypes[id] = NewGenType(full, shortName, declaringId, outerType, valueType, serializable, beforeFieldInit);
        _genTypeByFull[full] = id;
        return id;
    }

    GenType NewGenType(string full, string shortName, int declaringId, INamedTypeSymbol outerType, bool valueType, bool serializable, bool beforeFieldInit)
    {
        var top = outerType;
        while (top.ContainingType != null) top = top.ContainingType;
        return new GenType
        {
            Full = full, Short = shortName, DeclaringId = declaringId, IsValueType = valueType, Serializable = serializable, BeforeFieldInit = beforeFieldInit,
            Namespace = top.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : "",
            Assembly = outerType.OriginalDefinition.ContainingAssembly,
        };
    }

    bool IsRelease(IMethodSymbol m) => m.DeclaringSyntaxReferences.FirstOrDefault() is { } r && ModelFor(r.SyntaxTree).Compilation.Options.OptimizationLevel == OptimizationLevel.Release;

    /// <summary>A state machine's type id: the one of its class when the kickoff method is compiled (so
    /// [AsyncStateMachine]'s type is the class of the object), else one of its own.</summary>
    internal int MachineTypeId(Display machine, IMethodSymbol? def)
    {
        if (def != null && _machineIds.TryGetValue(def, out var pre) && !DisplayById.ContainsKey(pre))
        {
            machine.TypeId = pre;
            DisplayById[pre] = machine;
            Constructed.Add(pre);
            return pre;
        }
        int id = DisplayTypeId(machine);
        if (def != null) _machineIds.TryAdd(def, id);
        return id;
    }

    int MachineIdFor(IMethodSymbol def)
    {
        def = def.OriginalDefinition;
        if (_machineIds.TryGetValue(def, out var id)) return id;
        var (sn, full) = StateMachineName(null!, def)!.Value;
        var ct = (INamedTypeSymbol)Types.Canonical(def.ContainingType.OriginalDefinition);
        id = TypeById.Count;
        TypeById.Add(ObjectType);
        GenTypes[id] = NewGenType(full, sn, TypeId(ct), ct, def.IsAsync && !def.IsIterator && IsRelease(def), false, false);
        _machineIds[def] = id;
        return id;
    }

    // ---------------- the methods ----------------
    sealed class GenMethod
    {
        public required string Name;
        public required Func<int> DeclaringId;
        public int Flags;
        public ITypeSymbol? Return;
        public List<(Func<int> typeId, bool byRef, string name)> Params = [];
        public bool CompilerGenerated;
        public List<WasmFunction> Fns = [];
        public string? FrameKey;
        public int Entry;
        /// <summary>A delegate can name it (a lambda, a local function made a delegate).</summary>
        public bool Converted;
    }

    List<GenMethod>? _genMethods;
    readonly Dictionary<WasmFunction, int> _genEntryOfFn = [];

    /// <summary>The generated methods and the types they name, once compiling is over (RuntimeGen.AssignTypeIds).</summary>
    internal IEnumerable<int> GeneratedTypeIds()
    {
        PrepareGenerated();
        foreach (var id in GenTypes.Keys.ToList()) yield return id;
    }

    internal IEnumerable<ITypeSymbol> GeneratedRelatedTypes()
    {
        PrepareGenerated();
        foreach (var g in GenTypes.Values)
            foreach (var i in g.Interfaces) yield return i;
        foreach (var a in _genAttrClasses) yield return LibType(a);
        foreach (var m in _genMethods!)
            if (m.Return != null) yield return m.Return;
    }

    internal void PrepareGenerated()
    {
        if (_genMethods != null) return;
        _genMethods = [];
        // the classes the program's closures and state machines are, by their C# compiler's names
        foreach (var (id, d) in DisplayById.ToList())
        {
            if (GenTypes.ContainsKey(id)) continue;
            if (d.RoslynFull != null)
            {
                var mr = Machines.FirstOrDefault(m => m.Machine == d);
                if (mr == null) continue;
                var ct = (INamedTypeSymbol)Types.Canonical(mr.Kickoff.ContainingType?.OriginalDefinition ?? ObjectType);
                GenTypes[id] = NewGenType(d.RoslynFull, d.RoslynShort!, mr.NestedOwner == null ? TypeId(ct) : -1, ct, mr.Async && !mr.Iterator && IsRelease(mr.Kickoff.OriginalDefinition), false, false);
                _genTypeByFull.TryAdd(d.RoslynFull, id);
            }
            else if (d.Owner is FunctionCompiler fc && RoslynDisplayName(d, true) is { } full)
            {
                var ct = (INamedTypeSymbol)Types.Canonical(fc.OuterMethod.ContainingType.OriginalDefinition);
                GenTypes[id] = NewGenType(full, RoslynDisplayName(d, false)!, TypeId(ct), ct, false, false, true);
                _genTypeByFull.TryAdd(full, id);
            }
        }
        foreach (var mr in Machines)
        {
            if (GenTypes.TryGetValue(mr.Machine.TypeId, out var g))
            {
                g.Interfaces = mr.Iterator && !mr.Async
                    ? [.. SyntheticById.TryGetValue(mr.Machine.TypeId, out var syn) ? syn.Interfaces : []]
                    : [LibType("System.Runtime.CompilerServices.IAsyncStateMachine")];
            }
        }
        if (!WantsGenTypes) return;
        var byKey = new Dictionary<string, GenMethod>();
        // lambdas and local functions
        foreach (var (fn, (outer, syntax)) in NestedFns)
        {
            if (fn.Body.Count == 0 || NamesOf(outer) is not { } names || !names.Functions.TryGetValue(syntax, out var f) || f.InExpressionTree) continue;
            var outerType = (INamedTypeSymbol)Types.Canonical(outer.ContainingType.OriginalDefinition);
            var path = ClrPath(outer.ContainingType.OriginalDefinition);
            if (_wantsDelegateTarget && f.Kind == ClosureKind.Singleton)
            {
                // (an object of the class exists: the dispatch of Object's virtual methods is told of its id,
                // RuntimeGen.Finish; AnalyzeReflection had Object's own compiled)
                _singletonOfFn[fn] = GenTypeId(path + "+" + names.SingletonName, names.SingletonName, TypeId(outerType), outerType, serializable: true, beforeFieldInit: true);
            }
            else if (_wantsDelegateTarget && f.Converted) NoteTargetRule(fn, names, f);
            if (!WantsGenerated) continue;
            var key = Types.MethodKey(outer) + "@" + syntax.SyntaxTree.FilePath + ":" + syntax.SpanStart;
            if (!byKey.TryGetValue(key, out var gm))
            {
                Func<int> declaring = f.Kind switch
                {
                    ClosureKind.Singleton => () => GenTypeId(path + "+" + names.SingletonName, names.SingletonName, TypeId(outerType), outerType, serializable: true, beforeFieldInit: true),
                    ClosureKind.General => () => GenTypeId(path + "+" + f.Container!.Name, f.Container!.Name, TypeId(outerType), outerType, beforeFieldInit: true),
                    _ => () => TypeId(outerType),
                };
                var sym = f.Sym;
                gm = new GenMethod
                {
                    Name = f.Name, DeclaringId = declaring,
                    Flags = (f.Kind == ClosureKind.Static ? FStatic : 0) | (f.Kind == ClosureKind.ThisOnly ? FPrivate : FAssembly),
                    Return = sym.ReturnsVoid ? null : sym.ReturnType,
                    CompilerGenerated = f.Kind is ClosureKind.ThisOnly or ClosureKind.Static,
                    FrameKey = fn.FrameName,
                    Converted = f.Converted,
                };
                foreach (var p in sym.Parameters)
                {
                    var pt = p.Type;
                    gm.Params.Add((() => TypeId(pt), p.RefKind != RefKind.None, p.Name));
                }
                foreach (var se in f.StructEnvs)
                {
                    var n = se.Name;
                    gm.Params.Add((() => GenTypeId(path + "+" + n, n, TypeId(outerType), outerType, valueType: true), true, ""));
                }
                byKey[key] = gm;
                _genMethods.Add(gm);
            }
            gm.Fns.Add(fn);
        }
        // the state machines' MoveNext
        foreach (var mr in Machines)
        {
            if (mr.MoveNext.Body.Count == 0 || !WantsGenerated) continue;
            var machineId = mr.Machine.TypeId;
            if (mr.NestedOwner != null && GenTypes.TryGetValue(machineId, out var mg) && mg.DeclaringId < 0)
            {
                // (a lambda's or local function's machine: in the class its method is a member of)
                var owner = _genMethods.FirstOrDefault(x => x.Fns.Contains(mr.NestedOwner));
                if (owner != null) { var od = owner.DeclaringId; mg.DeclaringId = od(); }
            }
            var gm = new GenMethod
            {
                Name = "MoveNext", DeclaringId = () => machineId,
                Flags = FPrivate | FVirtual | FFinal | FNewSlot,
                Return = mr.Iterator && !mr.Async ? Main.GetSpecialType(SpecialType.System_Boolean) : null,
                FrameKey = mr.MoveNext.FrameName,
            };
            gm.Fns.Add(mr.MoveNext);
            _genMethods.Add(gm);
        }
        foreach (var gm in _genMethods)
        {
            gm.DeclaringId();
            foreach (var p in gm.Params) p.typeId();
        }
    }

    /// <summary>The generated methods' entries (EmitReflectionTables, after the types' records): their frames and
    /// their functions as delegates name them.</summary>
    void EmitGeneratedMethods()
    {
        if (_genMethods == null) return;
        foreach (var gm in _genMethods)
        {
            var (na, aa) = gm.CompilerGenerated ? GenAttrs([("System.Runtime.CompilerServices.CompilerGeneratedAttribute", null)]) : (0, 0);
            _tokenRows[0x06000000] = _tokenRows.GetValueOrDefault(0x06000000) + 1;
            var entry = Ints([Str(gm.Name), gm.Return == null || Compiler.Types.IsVoid(gm.Return) ? -1 : ExtTypeId(gm.Return), gm.Flags, 0, gm.Params.Count,
                Ints(gm.Params.Select(p => ExtId(p.typeId()) | (p.byRef ? 0x40000000 : 0))).Address, Ints(gm.Params.Select(p => Str(p.name))).Address,
                na, aa, ExtId(gm.DeclaringId()), 0, 0, 0x06000000 | _tokenRows[0x06000000]]).Address;
            gm.Entry = entry;
            if (FramesAsked && gm.FrameKey != null) _frameEntries[gm.FrameKey] = entry;
            if (gm.Converted) foreach (var fn in gm.Fns) _genEntryOfFn[fn] = entry;
        }
    }

    internal IEnumerable<int> SingletonClassIds() => _singletonOfFn.Values.Distinct();

    WasmFunction? _delegateSingleton;
    public WasmFunction DelegateSingletonFn => _delegateSingleton ??= Module.AddFunction("rt.delegate_target", new FuncType([ValType.I32, ValType.I32], [ValType.I32]));

    /// <summary>Delegate.Target (Delegate.TargetOf, asked by the property alone; parameters: the delegate's function
    /// and what it holds; the result is counted): of a lambda that captures nothing, the one object of its
    /// `&lt;&gt;c` class, which is in the data as a string literal is (count -1: never freed, the same for every
    /// thread), its type the class's; of the functions of NoteTargetRule, what the rule says; of any other, and of
    /// every function in a program whose code never asks for a target, what the delegate holds.</summary>
    public void EmitDelegateSingleton()
    {
        if (_delegateSingleton == null) return;
        var e = new Emitter(_delegateSingleton);
        var live = _singletonOfFn.Count + _targetOfFn.Count > 0 ? Module.LiveFunctions() : null;
        var objects = new Dictionary<int, IAddress>();
        foreach (var (fn, id) in _singletonOfFn)
        {
            if (fn.Body.Count == 0 || !live!.Contains(fn) || ExtId(id) < 0) continue;
            if (!objects.TryGetValue(id, out var o)) objects[id] = o = DataBlob([.. new[] { -1, ExtId(id), 0, 0 }.SelectMany(BitConverter.GetBytes)], 8);
            e.Get(0); e.I32Fn(Module, fn); e.Op(Op.I32Eq);
            e.If(); e.I32Addr(o); e.Op(Op.Return); e.End();
        }
        foreach (var (fn, (none, up, thisOffset)) in _targetOfFn)
        {
            if (fn.Body.Count == 0 || !live!.Contains(fn)) continue;
            e.Get(0); e.I32Fn(Module, fn); e.Op(Op.I32Eq);
            e.If();
            if (none) e.I32(0);
            else
            {
                e.Get(1);
                for (int i = 0; i < up; i++) e.Load(Op.I32Load, 12);
                if (thisOffset >= 0) e.Load(Op.I32Load, thisOffset);
                e.Call(Rt.Retain);
            }
            e.Op(Op.Return); e.End();
        }
        e.Get(1); e.Call(Rt.Retain);
    }

    /// <summary>The records of the generated types (their attributes, base and interfaces), by id.</summary>
    void GeneratedTypeRecords(Dictionary<int, IAddress> records)
    {
        foreach (var (id, g) in GenTypes)
        {
            if (records.ContainsKey(id) || ExtId(id) < 0) continue;
            // ([Serializable] is the type's flag: GetCustomAttributes makes it of that, as for any type; a class
            // nested in one the C# compiler made has no [CompilerGenerated] of its own, as Roslyn emits it)
            var (ta, tb) = GenTypes.ContainsKey(g.DeclaringId) ? (0, Ints([]).Address) : GenAttrs([("System.Runtime.CompilerServices.CompilerGeneratedAttribute", null)]);
            var baseType = LibType(g.IsValueType ? "System.ValueType" : "System.Object");
            var ifaces = g.Interfaces.Where(i => ExistingTypeId(i) is int x && ExtId(x) >= 0).ToList();
            records[id] = Ints([0, Ints([]).Address, 0, Ints([]).Address, 0, Ints([]).Address, 0, Ints([]).Address, ta, tb,
                ExistingTypeId(baseType) is int bi && ExtId(bi) >= 0 ? ExtId(bi) + 1 : 0, ifaces.Count, Ints(ifaces.Select(i => ExtTypeId(i))).Address]);
        }
    }

    // ---------------- attributes the C# compiler adds ----------------
    readonly Dictionary<string, WasmFunction> _genAttrFactories = [];
    readonly HashSet<string> _genAttrClasses = [];

    static string GenAttrKey(string attr, int? typeArg) => "gen " + attr + (typeArg is int t ? "(" + t + ")" : "");

    /// <summary>A factory of the attribute (its constructor without arguments, or with the type of the id).</summary>
    WasmFunction GenAttributeFactory(string attr, Func<int>? typeArg)
    {
        int? ta = typeArg?.Invoke();
        var key = GenAttrKey(attr, ta);
        if (_genAttrFactories.TryGetValue(key, out var f)) return f;
        var at = LibType(attr);
        _genAttrClasses.Add(attr);
        for (var t = (INamedTypeSymbol?)at; t != null && !Compiler.Types.IsObject(t); t = t.BaseType) MarkConstructed(Types.LibCompilationArray(t));
        MarkConstructed(Types.LibCompilationArray(LibType("System.AttributeUsageAttribute")));
        var ctor = at.InstanceConstructors.First(c => c.Parameters.Length == (ta == null ? 0 : 1));
        f = Module.AddFunction("attribute " + key, ThunkType);
        f.TableRoot = true;
        _genAttrFactories[key] = f;
        var mi = new MethodInst { Sym = LibMethod("Il2Wasm.Runtime.Rt", "ReportUnhandled", 1).Sym, Fn = f, Key = "attr " + key, Impl = MethodImpl.Body };
        mi.Compile = () => FunctionCompiler.CompileGenAttributeFactory(this, f, ctor, ta, mi);
        EnqueueExtra(mi);
        return f;
    }

    (int count, int addr) GenAttrs(List<(string attr, Func<int>? typeArg)> list)
    {
        var ints = new List<int>();
        int n = 0;
        foreach (var (attr, typeArg) in list)
        {
            var key = GenAttrKey(attr, typeArg?.Invoke());
            if (!_genAttrFactories.TryGetValue(key, out var f) || f.Body.Count == 0) continue;
            ints.Add(ExtTypeId(LibType(attr)));
            ints.Add(RootSlot(f) + 1);
            n++;
        }
        return (n, Ints(ints).Address);
    }

    /// <summary>The attributes the C# compiler puts on an iterator's or async method: its state machine's type, and
    /// [DebuggerStepThrough] on an async one.</summary>
    List<(string, Func<int>?)> StateMachineAttributes(ISymbol s)
    {
        if (s is not IMethodSymbol m || m.MethodKind is MethodKind.AnonymousFunction or MethodKind.LocalFunction || !(m.IsAsync || m.IsIterator) || m.ContainingType == null) return [];
        var def = m.OriginalDefinition;
        Func<int> sm = () => MachineIdFor(def);
        bool asyncIterator = def.IsAsync && def.IsIterator;
        var l = new List<(string, Func<int>?)>
        {
            (asyncIterator ? "System.Runtime.CompilerServices.AsyncIteratorStateMachineAttribute" : def.IsAsync ? "System.Runtime.CompilerServices.AsyncStateMachineAttribute" : "System.Runtime.CompilerServices.IteratorStateMachineAttribute", sm),
        };
        if (def.IsAsync && !asyncIterator) l.Add(("System.Diagnostics.DebuggerStepThroughAttribute", null));
        return l;
    }

    /// <summary>The factories of the attributes the C# compiler adds (before the last compile round).</summary>
    void RequestGeneratedAttributeFactories()
    {
        if (WantsGenTypes)
        {
            GenAttributeFactory("System.Runtime.CompilerServices.CompilerGeneratedAttribute", null);
        }
        if (_reflect == null) return;
        foreach (var (t, all, names, ctors) in _reflect.Types.Values)
            foreach (var m in KeptMembers(t, all, names, ctors))
                foreach (var (attr, typeArg) in StateMachineAttributes(m)) GenAttributeFactory(attr, typeArg);
    }

    IEnumerable<ITypeSymbol> GeneratedAttributeClasses(ISymbol s)
    {
        foreach (var (attr, _) in StateMachineAttributes(s)) yield return LibType(attr);
    }
}

sealed partial class FunctionCompiler
{
    public static void CompileGenAttributeFactory(Driver d, WasmFunction fn, IMethodSymbol ctor, int? typeArg, MethodInst ctx)
    {
        var fc = new FunctionCompiler(d, fn, ctx.Sym, ctx);
        fc.EmitGenAttributeFactory(ctor, typeArg);
    }

    void EmitGenAttributeFactory(IMethodSymbol ctor0, int? typeArg)
    {
        using (BodyScope())
        {
            _retLocal = E.AddLocal(ValType.I32);
            BeginTemps();
            var ctor = D.CanonicalMethod(ctor0);
            var t = T.Canonical(ctor.ContainingType);
            int tid = D.MarkConstructed(t);
            int obj = E.AddLocal(ValType.I32);
            E.I32(Math.Max(T.LayoutOf(t).Size, Types.ObjectHeader)); E.Tid(tid); E.Call(Rt.AllocObject); E.Set(obj);
            CallWith(ctor, false, () => E.Get(obj), () =>
            {
                if (typeArg is int id)
                {
                    E.Tid(id);
                    var fromId = D.LibType("System.Type").GetMembers("FromId").OfType<IMethodSymbol>().First();
                    E.Call(D.Method(fromId).Fn);
                    int tl = TempRef(D.ObjectType); E.Tee(tl);
                }
            }, null, null);
            E.Get(obj);
            E.Set(_retLocal);
            EndTemps();
            EmitReturnSequence(true);
        }
        Finish();
    }
}
