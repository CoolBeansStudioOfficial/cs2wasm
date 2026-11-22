using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

/// <summary>Specialization by ownership (section 4.1: "a function is specialized for the ownership its callers
/// need"). A parameter whose every use is a store (into a field or an element, or passed on to another such
/// parameter), the uses on exclusive paths and outside loops, is consumed: a copy of the method takes that argument
/// owned and moves it into its place (no count taken; the parameter is cleared, and released at the end on the paths
/// that did not store it). A direct call that passes a fresh value there (a new object, a call's result, a
/// concatenation) hands its count over instead of releasing it after the call: `new Node(name + "!")`,
/// `list.Add(new Item())`, `obj.Name = Make()` cost no count operation for the argument.</summary>
internal sealed partial class Driver
{
    internal sealed record Consuming(int Mask, HashSet<SyntaxNode> Moves);
    static readonly Consuming s_noConsuming = new(0, []);
    readonly Dictionary<string, Consuming> _consuming = [];

    /// <summary>The parameters (a bit per ordinal) m consumes, and the uses that move them.</summary>
    static readonly bool s_noOwned = Environment.GetEnvironmentVariable("CS2WASM_NO_OWNED") == "1";
    static readonly bool s_noOwnedAdd = Environment.GetEnvironmentVariable("CS2WASM_NO_OWNED_ADD") == "1";
    static readonly int s_ownedLimit =int.TryParse(Environment.GetEnvironmentVariable("CS2WASM_OWNED_LIMIT"), out var ol) ? ol : 10;

    internal Consuming ConsumingOf(IMethodSymbol m)
    {
        if (s_noOwned) return s_noConsuming;
        var def = MethodTarget(m).OriginalDefinition;
        var key = Types.MethodKey(def);
        if (_consuming.TryGetValue(key, out var c)) return c;
        _consuming[key] = s_noConsuming; // (a recursive use, while deciding: not consumed)
        c = ComputeConsuming(def);
        _consuming[key] = c;
        return c;
    }

    /// <summary>Whether a direct call of m can go to an owned-argument copy (its body is compiled from source).</summary>
    internal bool CanSpecializeOwnership(IMethodSymbol m)
    {
        var t = MethodTarget(m);
        if (_methods.TryGetValue(Types.MethodKey(t), out var mi) && (mi.Impl != MethodImpl.Body || mi.Compile != null || mi.BorrowedReturn != null)) return false;
        return Classify(t) == MethodImpl.Body;
    }

    Consuming ComputeConsuming(IMethodSymbol def)
    {
        if (def.IsAbstract || def.IsExtern || def.IsAsync || def.IsIterator || def.Parameters.Length == 0 || def.Parameters.Length > 30) return s_noConsuming;
        if (def.MethodKind is not (MethodKind.Ordinary or MethodKind.Constructor or MethodKind.PropertySet or MethodKind.ExplicitInterfaceImplementation)) return s_noConsuming;
        if (Library.Substitution(def) != null || ReturnsThis(def)) return s_noConsuming;
        var body = BodyOperation(this, def);
        if (body == null) return s_noConsuming;
        // large functions keep one shared, borrow-first version (section 4.1); 10 operations is where the copies stop
        // paying for themselves in module size (measured); a struct parameter saves a count per reference it holds,
        // twice, so a larger body still pays (a constructor that stores its struct arguments)
        int size = body.Descendants().Count();
        // (List<T>.Add and the AddWithResize it calls are past the limit, 44 and 39 operations, and are where most
        // new objects of a program go: `list.Add(new Item())`, `children.Add(Build(..))`. Borrowed, the item is counted
        // by the store and let go by the caller; for a member of a group that second count is an outside reference
        // that comes and goes around the store, with the holds it makes up the owners: tools/frames.mjs, tree)
        bool listAdd = IsListAdd(def);
        bool small = size <= s_ownedLimit || listAdd && size <= 50;
        if (size > s_ownedLimit * 4 && !small) return s_noConsuming;
        if (body.DescendantsAndSelf().Any(o => o is ILabeledOperation || o is IBranchOperation { BranchKind: BranchKind.GoTo } || o.Kind is OperationKind.YieldReturn or OperationKind.YieldBreak))
            return s_noConsuming;
        var nested = body.Descendants().Where(o => o is IAnonymousFunctionOperation or ILocalFunctionOperation).SelectMany(o => o.DescendantsAndSelf()).ToHashSet();
        var refs = body.Descendants().OfType<IParameterReferenceOperation>().Where(r => !InNameOf(r)).ToLookup(r => r.Parameter.OriginalDefinition.Ordinal);
        int mask = 0;
        var moves = new HashSet<SyntaxNode>();
        for (int i = 0; i < def.Parameters.Length; i++)
        {
            var p = def.Parameters[i];
            if (p.RefKind == RefKind.None && p.Type.TypeKind == TypeKind.Struct && !p.Type.IsRefLikeType && !p.IsParams)
            {
                // a struct stored once, unconditionally, into a field: StructMoves (the caller's temporary moves in)
                var us = refs[i].Where(r => SymbolEqualityComparer.Default.Equals(r.Parameter.ContainingSymbol.OriginalDefinition, def)).ToList();
                if (s_noStructParams || us.Count != 1 || nested.Contains(us[0]) || !UnconditionalStructStore(us[0], body)) continue;
                mask |= 1 << i;
                moves.Add(us[0].Syntax);
                continue;
            }
            if (!small) continue;
            if (p.RefKind != RefKind.None || p.Type.IsValueType || p.Type.IsRefLikeType || p.IsParams) continue;
            var rs = refs[i].Where(r => SymbolEqualityComparer.Default.Equals(r.Parameter.ContainingSymbol.OriginalDefinition, def)).ToList();
            if (rs.Count == 0 || rs.Count > 4) continue;
            if (rs.Any(r => nested.Contains(r) || InLoop(r) || !IsMovePoint(r))) continue;
            bool exclusive = true;
            for (int a = 0; a < rs.Count && exclusive; a++)
                for (int b = a + 1; b < rs.Count && exclusive; b++)
                    exclusive = Exclusive(rs[a], rs[b]);
            if (!exclusive) continue;
            mask |= 1 << i;
            foreach (var r in rs) moves.Add(r.Syntax);
        }
        return mask == 0 ? s_noConsuming : new Consuming(mask, moves);
    }

    /// <summary>List&lt;T&gt;.Add(T) or its AddWithResize(T) (CS2WASM_NO_OWNED_ADD=1: as any method of their size).</summary>
    internal static bool IsListAdd(IMethodSymbol def) =>
        !s_noOwnedAdd && def.Name is "Add" or "AddWithResize" && def.Parameters.Length == 1
        && def.ContainingType is { Name: "List", Arity: 1, ContainingNamespace: { Name: "Generic", ContainingNamespace: { Name: "Collections", ContainingNamespace.Name: "System" } } };

    static readonly bool s_noStructParams =Environment.GetEnvironmentVariable("CS2WASM_NO_STRUCT_PARAMS") == "1";

    /// <summary>`this.f = p;` as a statement of the body's own block, every path through the body reaching it (no
    /// return before it, no try, no jump); f's type is p's. An exception before it leaves the value where the caller
    /// put it: the caller's temporary, released by the caller's unwinding.</summary>
    static bool UnconditionalStructStore(IParameterReferenceOperation r, IOperation body)
    {
        if (r.Parent is not ISimpleAssignmentOperation { IsRef: false } sa || !ReferenceEquals(sa.Value, r)) return false;
        if (sa.Target is not IFieldReferenceOperation { Field: { IsStatic: false, IsVolatile: false } f, Instance: IInstanceReferenceOperation }) return false;
        if (!SymbolEqualityComparer.Default.Equals(f.Type, r.Parameter.Type)) return false;
        if (sa.Parent is not IExpressionStatementOperation { Parent: IBlockOperation blk } st) return false;
        if (!(blk.Parent is IMethodBodyOperation or IConstructorBodyOperation || ReferenceEquals(blk, body))) return false;
        foreach (var o in body.Descendants())
        {
            if (o is ITryOperation || o is IBranchOperation || o is ILabeledOperation || o is IAnonymousFunctionOperation || o is ILocalFunctionOperation) return false;
            if (o is IReturnOperation && o.Syntax.SpanStart < st.Syntax.SpanStart) return false;
        }
        return true;
    }

    static bool InLoop(IOperation op)
    {
        for (var p = op.Parent; p != null; p = p.Parent) if (p is ILoopOperation) return true;
        return false;
    }

    /// <summary>A use that can take the parameter's count: the value of a store into a field or an element (as a
    /// statement), or an argument for a parameter the callee consumes.</summary>
    bool IsMovePoint(IParameterReferenceOperation r)
    {
        IOperation v = r;
        while (v.Parent is IConversionOperation c && c.OperatorMethod == null && (c.Conversion.IsIdentity || c.Conversion.IsImplicit && c.Conversion.IsReference)) v = c;
        switch (v.Parent)
        {
            case ISimpleAssignmentOperation { IsRef: false } sa when sa.Value == v && sa.Parent is IExpressionStatementOperation:
                return sa.Target is IFieldReferenceOperation { Field.IsConst: false } f && !f.Field.Type.IsRefLikeType && !f.Field.Type.IsValueType
                    || sa.Target is IArrayElementReferenceOperation;
            case IArgumentOperation { ArgumentKind: ArgumentKind.Explicit, Parameter: { RefKind: RefKind.None } ap } arg when arg.Value == v:
                {
                    IMethodSymbol? callee = arg.Parent switch
                    {
                        IInvocationOperation { IsVirtual: false } i => i.TargetMethod,
                        IInvocationOperation i when i.TargetMethod is { IsVirtual: false, IsAbstract: false, IsOverride: false } => i.TargetMethod,
                        IObjectCreationOperation oc => oc.Constructor,
                        _ => null,
                    };
                    return callee != null && !callee.IsGenericMethod && (ConsumingOf(callee).Mask & (1 << ap.Ordinal)) != 0;
                }
        }
        return false;
    }

    /// <summary>Whether two operations are on paths that exclude each other (the branches of one `if` or `?:`, or
    /// different sections of one switch).</summary>
    static bool Exclusive(IOperation a, IOperation b)
    {
        var up = new List<IOperation>();
        for (var x = a; x != null; x = x.Parent) up.Add(x);
        IOperation? prevB = b;
        for (var y = b.Parent; y != null; prevB = y, y = y.Parent)
        {
            int k = up.IndexOf(y);
            if (k < 1) continue;
            var prevA = up[k - 1];
            return y switch
            {
                IConditionalOperation c => prevA != prevB && (prevA == c.WhenTrue || prevA == c.WhenFalse) && (prevB == c.WhenTrue || prevB == c.WhenFalse),
                ISwitchOperation => prevA != prevB && prevA is ISwitchCaseOperation && prevB is ISwitchCaseOperation,
                _ => false,
            };
        }
        return false;
    }

    /// <summary>Whether a direct call of an instance method of a number type (int.ToString(), x.CompareTo(y)) can
    /// pass the number itself: the method's body is compiled from source, by the usual rules.</summary>
    internal bool CanPassThisByValue(IMethodSymbol m)
    {
        var t = MethodTarget(m);
        if (t.IsStatic || t.IsAbstract || t.IsExtern || t.IsAsync || t.IsIterator || t.MethodKind == MethodKind.Constructor) return false;
        if (t.ContainingType is not { TypeKind: TypeKind.Struct } ct || Types.PrimOf(ct) == Prim.None || Types.IsStruct(ct)) return false;
        if (t.ReturnsByRef || t.ReturnsByRefReadonly || Types.IsStruct(Types.Canonical(t.ReturnType))) return false;
        if (IsIntrinsic(t) || Library.Substitution(t.OriginalDefinition) != null) return false;
        if (Classify(t) != MethodImpl.Body) return false;
        var mi = Method(t);
        return mi.Impl == MethodImpl.Body && mi.Compile == null && mi.BorrowedReturn == null && mi.OwnedParams == 0;
    }

    /// <summary>The copy of a number type's instance method that takes the number itself as its first argument. The
    /// method as it is takes the number's address, so each caller keeps the number in its frame and passes where
    /// (`i.ToString()` in a loop: a frame, a store and an address per call); the copy keeps it in its own frame,
    /// which the final pass turns into a local when the body only reads it (Wasm/Locals.cs: PromoteFrame).
    /// What calls through the address still calls the method as it is (a boxed number's dispatch, a delegate).</summary>
    internal MethodInst ThisValueVariant(MethodInst baseMi)
    {
        var key = baseMi.Key + "|thisval";
        if (_methods.TryGetValue(key, out var vi)) return vi;
        var bt = baseMi.Fn.Type;
        var ps = bt.Params.ToArray();
        ps[0] = Types.WasmType(Types.Canonical(baseMi.Sym.ContainingType))!.Value;
        var fn = Module.AddFunction(Display(baseMi.Sym) + " [this by value]", new FuncType(ps, bt.Results));
        fn.FrameName = baseMi.Fn.FrameName;
        fn.ProgramCode = baseMi.Fn.ProgramCode;
        fn.InlineHint = baseMi.Fn.InlineHint;
        vi = new MethodInst { Sym = baseMi.Sym, Fn = fn, Key = key, Impl = MethodImpl.Body, ReachedFrom = Compiling, ThisByValue = true };
        _methods[key] = vi;
        _work.Enqueue(vi);
        return vi;
    }

    /// <summary>The copy of a method that takes the arguments in `mask` owned.</summary>
    internal MethodInst OwnedVariant(MethodInst baseMi, int mask)
    {
        var key = baseMi.Key + "|own" + mask;
        if (_methods.TryGetValue(key, out var vi)) return vi;
        if (Environment.GetEnvironmentVariable("CS2WASM_OWNED") == "1") Console.Error.WriteLine("owned variant: " + Display(baseMi.Sym) + " " + mask);
        var fn = Module.AddFunction(Display(baseMi.Sym) + " [own " + mask + "]", baseMi.Fn.Type);
        fn.FrameName = baseMi.Fn.FrameName;
        vi = new MethodInst { Sym = baseMi.Sym, Fn = fn, Key = key, Impl = MethodImpl.Body, ReachedFrom = Compiling, OwnedParams = mask };
        _methods[key] = vi;
        _work.Enqueue(vi);
        return vi;
    }
}

sealed partial class FunctionCompiler
{
    /// <summary>The call whose arguments are being pushed, the parameters its callee can consume, and the fresh
    /// arguments handed over (their temps, cleared before the call).</summary>
    sealed class CallConsume(IMethodSymbol m, int mask)
    {
        public readonly IMethodSymbol M = m;
        public readonly int Mask = mask;
        public int Moved;
        public readonly List<int> Temps = [];
    }

    CallConsume? _consume;
    HashSet<SyntaxNode>? _moves;

    /// <summary>An owned-argument copy: the consumed parameters own their values (released at the end where they
    /// were not moved).</summary>
    void SetupOwnedParams()
    {
        int mask = Inst?.OwnedParams ?? 0;
        if (mask == 0) return;
        _moves = D.ConsumingOf(M).Moves;
        for (int i = 0; i < M.Parameters.Length; i++)
        {
            if ((mask & (1 << i)) == 0) continue;
            var p = M.Parameters[i];
            var v = _vars[p];
            if (!Types.IsRef(v.Type) || v.InMemory) continue;
            var slot = new Slot { Local = v.Local, Type = v.Type };
            _paramSlots.Add(slot);
            _allSlots.Add(slot);
        }
    }

    /// <summary>A consumed struct parameter's store (OwnedParams: UnconditionalStructStore): it moves from the
    /// caller's temporary.</summary>
    bool IsMovedStructParamUse(IParameterReferenceOperation p) =>
        _moves != null && _moves.Contains(p.Syntax) && Inst is { OwnedParams: var om } && (om & (1 << p.Parameter.Ordinal)) != 0
        && _vars.TryGetValue(p.Parameter, out var v) && Types.IsStruct(v.Type);

    bool IsMovedParamUse(IParameterReferenceOperation p) =>
        _moves != null && _moves.Contains(p.Syntax) && Inst is { OwnedParams: var om } && (om & (1 << p.Parameter.Ordinal)) != 0
        && _vars.TryGetValue(p.Parameter, out var v) && !v.InMemory && Types.IsRef(v.Type);
}
