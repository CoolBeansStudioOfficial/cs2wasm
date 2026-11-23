using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.FlowAnalysis;

namespace Cs2Wasm.Compiler;

sealed partial class FunctionCompiler
{
    /// <summary>Substitutes the current instantiation into a method referenced by the body, and maps it to the library.</summary>
    IMethodSymbol SubstMethod(IMethodSymbol m)
    {
        if (m.MethodKind is MethodKind.LocalFunction or MethodKind.AnonymousFunction) return m;
        if (m.ReducedFrom != null) m = m.ReducedFrom.Construct([.. m.TypeArguments]);
        var ct = (INamedTypeSymbol)T.Resolve(m.ContainingType, Map);
        IMethodSymbol r = m;
        if (!SymbolEqualityComparer.Default.Equals(ct, m.ContainingType))
        {
            var defId = Types.DocId(m.OriginalDefinition);
            r = ct.GetMembers(m.Name).OfType<IMethodSymbol>().FirstOrDefault(x => x.Arity == m.Arity && Types.DocId(x.OriginalDefinition) == defId) ?? m;
            if (ReferenceEquals(r, m) && Environment.GetEnvironmentVariable("CS2WASM_DEBUG") != null)
                Console.Error.WriteLine($"subst fail {defId} in {Types.Key(ct)} asm={ct.ContainingAssembly?.Name} members=[{string.Join(";", ct.GetMembers(m.Name).Select(x => Types.DocId(x.OriginalDefinition)))}]");
        }
        if (m.IsGenericMethod)
        {
            var args = m.TypeArguments.Select(a => T.Resolve(a, Map)).ToArray();
            r = (r.ConstructedFrom ?? r).Construct(args);
        }
        return D.CanonicalMethod(r);
    }

    bool ReturnsStruct(IMethodSymbol m) => !m.ReturnsByRef && !m.ReturnsByRefReadonly && Types.IsStruct(T.Canonical(m.ReturnType));

    // =====================================================================
    // invocation from the operation tree
    // =====================================================================
    Own EmitInvocation(IInvocationOperation inv)
    {
        var target = inv.TargetMethod;
        // [Conditional("X")] with X not defined where the call is: the call, arguments included, is omitted (C# 22.5.3)
        if (IsOmittedConditionalCall(inv)) return Own.None;
        if (target.Name is "New" or "NewOfNullableArg" && Types.FullName(target.ContainingType) == "Cs2Wasm.Runtime.Generics" && D.IsLibrary(target))
            return EmitGenericsNew(inv);
        if (target.MethodKind == MethodKind.LocalFunction) return EmitLocalFunctionCall(inv);
        // EqualityComparer<T>.Default.Equals(a, b) and .GetHashCode(a) for an integer T (a Dictionary<int, V>'s probe):
        // the comparison itself, as .NET's compilers make it; the comparer is not fetched.
        if (inv.Instance is IPropertyReferenceOperation { Property: { Name: "Default", IsStatic: true } } dflt
            && T.Resolve(dflt.Property.ContainingType, Map) is INamedTypeSymbol { Name: "EqualityComparer", Arity: 1 } ect
            && ect.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic"
            && target.Name is "Equals" or "GetHashCode" && target.Parameters.Length == (target.Name == "Equals" ? 2 : 1))
        {
            var kt = T.Canonical(ect.TypeArguments[0]);
            var kp = Types.PrimOf(kt);
            if (kp is Prim.Bool or Prim.Char or Prim.I8 or Prim.U8 or Prim.I16 or Prim.U16 or Prim.I32 or Prim.U32 or Prim.I64 or Prim.U64 or Prim.IntPtr or Prim.UIntPtr
                && (target.Name == "Equals" || kp is Prim.I32 or Prim.U32 or Prim.I64 or Prim.U64 && kt.TypeKind != TypeKind.Enum))
            {
                EmitValueAs(inv.Arguments[0].Value, kt);
                if (target.Name == "GetHashCode")
                {
                    // (an int's hash code is the int, a uint's its bits; a long's the two halves xor-ed: Int64.GetHashCode)
                    if (Types.Is64(kp))
                    {
                        int hv = Scratch(ValType.I64);
                        E.Tee(hv); E.Op(Op.I32WrapI64); E.Get(hv); E.I64(32); E.Op(Op.I64ShrU); E.Op(Op.I32WrapI64); E.Op(Op.I32Xor);
                        FreeScratch(ValType.I64, hv);
                    }
                    return Own.None;
                }
                EmitValueAs(inv.Arguments[1].Value, kt);
                E.Op(Types.Is64(kp) ? Op.I64Eq : Op.I32Eq);
                return Own.None;
            }
        }
        // Array.CreateInstance(typeof(T), n), typeof(T).MakeArrayType(): T[] is a type the program makes
        if (target.ContainingType?.SpecialType == SpecialType.System_Array && target.Name == "CreateInstance" && inv.Arguments.Length == 2
            && StripConversions(inv.Arguments[0].Value) is ITypeOfOperation cto)
            MarkArrayOf(cto.TypeOperand);
        // Enum.GetValues(typeof(E)), typeof(E).GetEnumValues(): the result is an E[]
        if (target.Name is "GetValues" or "GetEnumValues" or "GetValuesAsUnderlyingType" or "GetEnumValuesAsUnderlyingType")
        {
            if (inv.Instance is ITypeOfOperation eto) MarkArrayOf(eto.TypeOperand);
            foreach (var arg in inv.Arguments) if (StripConversions(arg.Value) is ITypeOfOperation ato) MarkArrayOf(ato.TypeOperand);
        }
        if (target.Name == "MakeArrayType" && target.Parameters.Length == 0 && target.ContainingType?.ToDisplayString() == "System.Type" && inv.Instance is ITypeOfOperation mto)
            MarkArrayOf(mto.TypeOperand);
        // typeof(G<>).MakeGenericType(typeof(A), ...): the instance, known now (so its code is made, as NativeAOT's
        // analysis sees such calls)
        if (target.Name == "MakeGenericType" && target.ContainingType?.ToDisplayString() is "System.Type" && FoldMakeGenericType(inv) is { } folded)
        {
            if (folded.TypeKind == TypeKind.Enum) D.MarkConstructed(folded);
            E.Tid(D.TypeId(folded));
            var fromId = D.LibType("System.Type").GetMembers("FromId").OfType<IMethodSymbol>().First();
            E.Call(D.Method(fromId).Fn);
            return Own.Owned;
        }
        // Assembly.GetExecutingAssembly(): the assembly of the calling code
        if (target.Name == "GetExecutingAssembly" && target.Parameters.Length == 0 && target.ContainingType?.ToDisplayString() is "System.Reflection.Assembly")
        {
            E.I32(D.AssemblyIndex(M.ContainingAssembly));
            var of = D.LibType("System.Reflection.RuntimeAssembly").GetMembers("OfIndex").OfType<IMethodSymbol>().First();
            E.Call(D.Method(of).Fn);
            return Own.Owned;
        }
        // static abstract / virtual interface members through a constrained type parameter
        if (target.IsStatic && target.ContainingType.TypeKind == TypeKind.Interface && inv.ConstrainedToType is { } ctt)
        {
            var concrete = T.Resolve(ctt, Map);
            var slot = SubstMethod(target);
            var impl = ImplementationOf(concrete, slot) ?? slot; // primitives: generic math, lowered in EmitCallInstr
            return EmitCallOps(impl, null, inv.Arguments, false, inv.Syntax, alreadyResolved: true);
        }
        return EmitCall(target, inv.Instance, inv.Arguments.Cast<IOperation>().ToList(), inv.IsVirtual, inv.Syntax);
    }

    INamedTypeSymbol? FoldMakeGenericType(IInvocationOperation inv)
    {
        var recv = inv.Instance;
        while (recv is IConversionOperation c) recv = c.Operand;
        if (recv is not ITypeOfOperation { TypeOperand: INamedTypeSymbol { IsGenericType: true } open }) return null;
        if (inv.Arguments is not [{ Value: var av }]) return null;
        while (av is IConversionOperation c2) av = c2.Operand;
        if (av is not IArrayCreationOperation { Initializer: { } init }) return null;
        var args = new List<ITypeSymbol>();
        foreach (var e in init.ElementValues)
        {
            var x = e;
            while (x is IConversionOperation c3) x = c3.Operand;
            if (x is not ITypeOfOperation to) return null;
            var at = T.Resolve(to.TypeOperand, Map);
            if (at is ITypeParameterSymbol || at is INamedTypeSymbol { IsUnboundGenericType: true }) return null;
            args.Add(at);
        }
        var def = open.OriginalDefinition;
        if (def.TypeParameters.Length != args.Count) return null;
        // (the constraints are C#'s to check at run time: only arguments that satisfy them fold)
        for (int i = 0; i < args.Count; i++)
        {
            var tp = def.TypeParameters[i];
            if (tp.HasValueTypeConstraint && !args[i].IsValueType || tp.HasReferenceTypeConstraint && !args[i].IsReferenceType || tp.HasUnmanagedTypeConstraint && !args[i].IsUnmanagedType) return null;
            if (tp.ConstraintTypes.Length > 0) return null;
        }
        return (INamedTypeSymbol)T.Canonical(def.Construct([.. args]));
    }

    Own EmitCall(IMethodSymbol target, IOperation? instance, IReadOnlyList<IOperation> args, bool isVirtual, SyntaxNode syntax)
        => EmitCallOps(SubstMethod(target), instance, args, isVirtual, syntax, alreadyResolved: true);

    Own EmitCallOps(IMethodSymbol m, IOperation? instance, IReadOnlyList<IOperation> args, bool isVirtual, SyntaxNode syntax, bool alreadyResolved)
    {
        if (!alreadyResolved) m = SubstMethod(m);
        if (m.IsStatic && TryLowerStringFormat(m, args) is { } lowered) return lowered;
        int save = E.CurrentPos;
        Action? pushThis = null;
        ITypeSymbol? recvType = null;
        bool virt = isVirtual, thisByValue = false;
        // `((ISpanFormattable)value).TryFormat(...)` on a value of a known value type (generic code over T = int):
        // call the type's own implementation, as the JIT does, instead of boxing and dispatching to every
        // implementation of the interface. Only for types whose methods cannot change `this` (primitives, enums,
        // readonly structs), so calling on the value is calling on the box's copy.
        if (!m.IsStatic && isVirtual && instance is IConversionOperation { OperatorMethod: null } box
            && box.Operand.Type != null && TypeOf(instance) is { TypeKind: TypeKind.Interface or TypeKind.Class } && TypeOf(box.Operand) is var bt
            && bt.IsValueType && bt.TypeKind != TypeKind.TypeParameter && !IsNullable(bt)
            && (Types.PrimOf(bt) != Prim.None || bt is INamedTypeSymbol { IsReadOnly: true })
            && D.Implementation(bt, m) is { } dimpl && Types.Key(T.Canonical(dimpl.ContainingType)) == Types.Key(T.Canonical(bt)))
            instance = box.Operand;
        if (!m.IsStatic && instance != null)
        {
            recvType = TypeOf(instance);
            if (recvType.IsValueType && recvType.TypeKind != TypeKind.TypeParameter)
            {
                var rt = recvType;
                var mct = T.Canonical(m.ContainingType);
                bool own = Types.Key(mct) == Types.Key(T.Canonical(rt));
                var impl = own ? m : D.Implementation(rt, m);
                if (impl != null && Types.Key(T.Canonical(impl.ContainingType)) == Types.Key(T.Canonical(rt)))
                {
                    m = impl; virt = false;
                    var callee = m;
                    // (a number's own method: the copy that takes the number itself, Driver.ThisValueVariant)
                    thisByValue = s_thisByValue && Types.PrimOf(rt) != Prim.None && !Types.IsStruct(rt) && rt.TypeKind == TypeKind.Struct && _clone == null
                        && !IsStaticInterfaceMember(m) && !args.Any(HasHandlerPlaceholders) && ConstArgsFor(m, args) == null
                        && (m.Parameters.Length == 0 || D.ConsumingOf(m).Mask == 0) && D.CanPassThisByValue(m);
                    if (thisByValue) pushThis = () => { EmitValueAs(instance, rt); };
                    else pushThis = () => PushValueTypeReceiver(instance, rt, callee);
                }
                else
                {
                    // box and call the inherited (object/ValueType/Enum) implementation virtually
                    pushThis = () => { var o = EmitValue(instance); EmitBox(rt, o); E.Tee(TempRef(D.ObjectType)); E.Op(Op.Drop); E.Get(_refPoolLast); };
                    pushThis = () =>
                    {
                        var o = EmitValue(instance);
                        EmitBox(rt, o);
                        int tl = TempRef(D.ObjectType);
                        E.Set(tl); E.Get(tl);
                    };
                    virt = true;
                }
            }
            else
            {
                pushThis = () =>
                {
                    var o = EmitValue(instance);
                    ToCallSafe(o, recvType);
                    if (!IsKnownNonNull(instance)) NullCheckKeep();
                };
                // (anonymous types' Equals/GetHashCode/ToString are synthesized: no symbol to find, so they dispatch)
                if (recvType is INamedTypeSymbol { IsSealed: true, IsAnonymousType: false } sealedT && virt && sealedT.TypeKind == TypeKind.Class)
                {
                    var impl = D.Implementation(sealedT, m);
                    if (impl != null) { m = impl; virt = false; }
                }
            }
        }
        else if (!m.IsStatic && instance == null)
        {
            // implicit `this`-less instance call (e.g. base ctor initializer resolved elsewhere)
            pushThis = () => EmitThis(null!);
        }
        if (args.Any(HasHandlerPlaceholders))
        {
            var spilled = EmitSpilledCall(m, virt, pushThis, args, syntax);
            E.CurrentPos = save;
            return spilled;
        }
        // (an `out var` the callee can leave borrowed: its copy that stores the value as it is, OutBorrows.cs)
        int ob = thisByValue || D.ConsumingOf(m).Mask != 0 ? -1 : OutBorrowCall(m, virt, args);
        bool wantRefAddress = _wantRefReturnAddress;
        var r = CallWith(m, virt, pushThis, () => { PushArgs(m, args); if (ob >= 0) _outBorrowNext = ob + 1; }, null, syntax, ob >= 0 ? null : ConstArgsFor(m, args), stableRecv: instance != null && IsStableBase(instance), thisByValue: thisByValue);
        if (D.ExposesStorage(m)) KeepStorage(m, syntax, wantRefAddress, instance, args);
        E.CurrentPos = save;
        return r;
    }

    static readonly bool s_thisByValue = Environment.GetEnvironmentVariable("CS2WASM_NO_THIS_BY_VALUE") != "1";
    /// <summary>CS2WASM_MEMSTATS=1 (a diagnosis): the runtime reports where its heap's memory is (Runtime.cs, MemStatsReport).</summary>
    static readonly bool s_atomicCounts = Environment.GetEnvironmentVariable("CS2WASM_ATOMIC_COUNTS") == "1";
    static readonly bool MemStats =Environment.GetEnvironmentVariable("CS2WASM_MEMSTATS") == "1";
    /// <summary>CS2WASM_NO_RIPE_PAGES=1: a size's page whose block was freed last is the next to be used again (Runtime.cs, PageAlloc).</summary>
    static readonly bool s_noRipePages = Environment.GetEnvironmentVariable("CS2WASM_NO_RIPE_PAGES") == "1";
    static readonly bool s_noHoldPages = Environment.GetEnvironmentVariable("CS2WASM_NO_HOLD_PAGES") == "1";

    int _refPoolLast => -1;

    void PushValueTypeReceiver(IOperation instance, ITypeSymbol rt, IMethodSymbol? callee = null)
    {
        if (Types.IsStruct(rt))
        {
            // A method that may change its struct runs on a copy when the struct is not a variable (C# 12.8.9.2):
            // a property's value, an unboxed value, a readonly field outside its constructor, an `in` parameter.
            bool mutates = callee != null && !callee.IsReadOnly && !callee.ContainingType.IsReadOnly && callee.MethodKind != MethodKind.Constructor;
            var inst = instance;
            while (inst is IParenthesizedOperation pa) inst = pa.Operand;
            bool readOnlyPlace = inst is IFieldReferenceOperation { Field.IsReadOnly: true } fr
                    && !(M.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor && SymbolEqualityComparer.Default.Equals(fr.Field.ContainingType.OriginalDefinition, M.ContainingType.OriginalDefinition))
                || inst is IParameterReferenceOperation { Parameter.RefKind: RefKind.In or RefKind.RefReadOnlyParameter }
                || inst is ILocalReferenceOperation { Local.RefKind: RefKind.RefReadOnly };
            if (IsAddressable(instance))
            {
                EmitAddress(instance);
                if (mutates && readOnlyPlace) CopyToTemp(rt);
                return;
            }
            EmitValue(instance);
            // (what is not a variable and not a temporary of its own: a property's value, an unboxed or converted value)
            if (mutates && inst is IPropertyReferenceOperation or IConversionOperation { Conversion.IsIdentity: false } or IImplicitIndexerReferenceOperation)
                CopyToTemp(rt);
            return;
        }
        // primitive or enum: the method takes the address of the value
        if (IsAddressable(instance)) { EmitAddress(instance); return; }
        int off = TempValue(rt);
        var vt = Types.WasmType(rt)!.Value;
        EmitValue(instance);
        int sv = Scratch(vt); E.Set(sv);
        E.Get(_fb); E.Get(sv); E.Store(Types.StoreOp(rt), off);
        FreeScratch(vt, sv);
        E.Get(_fb); E.I32(off); E.Op(Op.I32Add);
    }

    /// <summary>Pushes arguments in parameter order.</summary>
    void PushArgs(IMethodSymbol m, IReadOnlyList<IOperation> args, Action<int>? afterEach = null)
    {
        var byParam = new IOperation?[m.Parameters.Length];
        var convs = new IMethodSymbol?[m.Parameters.Length];
        int k = 0;
        foreach (var a in args)
        {
            if (a is IArgumentOperation ao && ao.Parameter != null)
            {
                byParam[ao.Parameter.Ordinal] = ao.Value;
                // a user-defined conversion of the argument lives on the argument, not in the tree
                if (ao.InConversion.IsUserDefined && ao.InConversion.MethodSymbol is { } cm) convs[ao.Parameter.Ordinal] = cm;
            }
            else if (k < byParam.Length) byParam[k] = a;
            k++;
        }
        // (one local for two by-reference parameters: each names the other's place)
        for (int i = 0; i < byParam.Length; i++)
            if (m.Parameters[i].RefKind != RefKind.None && byParam[i] is ILocalReferenceOperation or IParameterReferenceOperation && T.Resolve(m.Parameters[i].Type, Map) is { IsReferenceType: true } dt)
                for (int j = 0; j < i; j++)
                    if (m.Parameters[j].RefKind != RefKind.None && byParam[j] is { } o && o.Kind == byParam[i]!.Kind
                        && SymbolEqualityComparer.Default.Equals(o is ILocalReferenceOperation ol ? ol.Local : (o as IParameterReferenceOperation)?.Parameter, byParam[i] is ILocalReferenceOperation il ? il.Local : ((IParameterReferenceOperation)byParam[i]!).Parameter))
                        D.Arenas.RefWild.Add(Types.Key(T.Canonical(dt)));
        // Named arguments written in another order than the parameters' (f(b: x(), a: y())) are evaluated as
        // written: each into a local, then pushed in the parameters' order.
        int[]? held = null;
        int last = -1;
        foreach (var a in args)
            if (a is IArgumentOperation { Parameter: { } ap, ArgumentKind: not ArgumentKind.DefaultValue })
            {
                if (ap.Ordinal < last && afterEach == null) { held = new int[m.Parameters.Length]; break; }
                last = ap.Ordinal;
            }
        if (held != null)
        {
            Array.Fill(held, -1);
            foreach (var a in args)
            {
                if (a is not IArgumentOperation { Parameter: { } ap, ArgumentKind: not ArgumentKind.DefaultValue }) continue;
                int i = ap.Ordinal;
                if (convs[i] is { } cm0 && byParam[i] is { } v0) PushConvertedArg(m.Parameters[i], v0, cm0);
                else PushArg(m.Parameters[i], byParam[i]);
                var pt = T.Canonical(m.Parameters[i].Type);
                held[i] = E.AddLocal(m.Parameters[i].RefKind != RefKind.None ? ValType.I32 : Types.WasmType(pt) ?? ValType.I32);
                E.Set(held[i]);
            }
        }
        for (int i = 0; i < m.Parameters.Length; i++)
        {
            if (held != null && held[i] >= 0) E.Get(held[i]);
            else if (convs[i] is { } cm && byParam[i] is { } v) PushConvertedArg(m.Parameters[i], v, cm);
            else PushArg(m.Parameters[i], byParam[i]);
            afterEach?.Invoke(i);
        }
        // (a converted argument's source is the conversion's result: Sharing.cs)
        if (Sh != null)
        {
            for (int i = 0; i < m.Parameters.Length; i++)
                if (convs[i] is { } cm && byParam[i] != null) { var cs = new List<Sharing.Src>(1); AddRet(cm, false, cs); RecordParam(m, m.Parameters[i], cs); }
            RecordRefWrite(m, byParam);
        }
        ArenaRefWrite(m, byParam);
    }

    /// <summary>Interlocked.Exchange and CompareExchange, Volatile.Write: their own store through the `ref` names no
    /// site (Rt.LinkAt), so the call says what it puts in a slot of the ref's type (RefStores.cs).</summary>
    void ArenaRefWrite(IMethodSymbol m, IOperation?[] byParam)
    {
        if (m.ContainingType is not { } ct || m.MethodKind == MethodKind.FunctionPointerSignature || Types.FullName(ct.OriginalDefinition) is not ("System.Threading.Interlocked" or "System.Threading.Volatile")) return;
        int loc = -1, val = -1;
        for (int i = 0; i < m.Parameters.Length; i++)
        {
            var p = m.Parameters[i];
            if (p.RefKind == RefKind.Ref && Types.IsRef(T.Canonical(p.Type)) && loc < 0) loc = i;
            else if (p.RefKind == RefKind.None && p.Name is "value" && Types.IsRef(T.Canonical(p.Type))) val = i;
        }
        if (loc < 0) return;
        D.Arenas.RefStore(T.Canonical(m.Parameters[loc].Type), val >= 0 && byParam[val] is { } v ? TypeOf(v) : null);
    }

    void PushConvertedArg(IParameterSymbol p, IOperation value, IMethodSymbol conversion)
    {
        var cm = SubstMethod(conversion);
        var argT = T.Canonical(cm.Parameters[0].Type);
        var pt = T.Canonical(p.Type);
        var r = CallWith(cm, false, null, () =>
        {
            var o = EmitValueAs(value, argT);
            if (Types.IsRef(argT)) ToCallSafe(o, argT);
        }, null, value.Syntax);
        ConvertTop(T.Canonical(cm.ReturnType), pt, ref r);
        if (Types.IsRef(pt)) ToCallSafe(r, pt);
    }

    /// <summary>An argument for a by-reference parameter that names a place no other code can write while the callee
    /// runs: a local or a parameter of this method's own, not captured, not itself a reference to elsewhere (a
    /// by-reference parameter passed on is its caller's question); a variable declared at the call; a discard.</summary>
    bool PlainRefArg(IOperation value) => value switch
    {
        ILocalReferenceOperation lr => lr.Local.RefKind == RefKind.None && !IsCaptured(lr.Local) && _closure == null && _machine == null,
        IParameterReferenceOperation pr => !IsCaptured(pr.Parameter) && _closure == null && _machine == null,
        IDeclarationExpressionOperation de => PlainRefArg(de.Expression),
        IDiscardOperation => true,
        _ => false,
    };

    void PushArg(IParameterSymbol p, IOperation? value)
    {
        var pt = T.Canonical(p.Type);
        // (where the callee's parameter's value comes from: Sharing.cs)
        if (Sh != null && p.ContainingSymbol is IMethodSymbol pm && p.RefKind == RefKind.None && Types.IsRef(pt))
            RecordParam(pm, p, value != null ? FlowSrcs(value) : p.HasExplicitDefaultValue && p.ExplicitDefaultValue is string ? [new Sharing.Src(Sharing.K.Exact, Type: D.Main.GetSpecialType(SpecialType.System_String))] : []);
        else if (Sh != null && p.ContainingSymbol is IMethodSymbol rpm && p.RefKind != RefKind.None && Types.IsRef(pt))
            RecordRefIn(rpm, p, value);
        // (a class's reference passed by reference from anywhere but a local of this method's own: ChainStores.cs)
        if (p.RefKind != RefKind.None && value != null && T.Resolve(p.Type, Map) is { IsReferenceType: true } rpt && !PlainRefArg(value) && D.Arenas.RefWild.Add(Types.Key(T.Canonical(rpt))) && Driver.ChainDebug)
            Console.Error.WriteLine($"by reference, not a local: {value.Syntax} ({value.Kind}) for {p.ContainingSymbol.ToDisplayString()} in {M.ToDisplayString()}");
        if (value == null)
        {
            // default argument
            if (p.HasExplicitDefaultValue) EmitConstant(p.ExplicitDefaultValue, pt);
            else EmitDefault(pt);
            return;
        }
        if (p.RefKind is RefKind.Ref or RefKind.Out or RefKind.RefReadOnlyParameter)
        {
            if (Sh != null && p.RefKind is RefKind.Ref or RefKind.Out) RecordOutArg(p, value);
            else if (Sh != null) _preciseAddr = true; // (read only)
            if (Sh != null)
            {
                _addrUse = RefUseOf(p);
                // (a struct's place to an intrinsic that remakes refs, Unsafe.As<S, object>(ref s): its references
                // may be stored through refs of other types, any object of their types)
                if (_addrUse == null && Types.IsStruct(pt) && T.LayoutOf(pt).HasRefs && D.IsIntrinsic(D.MethodTarget((IMethodSymbol)p.ContainingSymbol))) StructLeavesAnything(pt);
            }
            EmitAddress(value);
            return;
        }
        if (p.RefKind == RefKind.In)
        {
            if (IsAddressable(value) && (value.Type == null || Types.Key(TypeOf(value)) == Types.Key(pt))) EmitAddress(value);
            else EmitAddressOfCopy(value, pt);
            return;
        }
        if (Types.IsStruct(pt))
        {
            // by value: the callee gets its own copy unless the argument is already a fresh temporary
            // (a callee that consumes the parameter moves the temporary into its place: OwnedParams.cs)
            bool consume = _consume is { } scc && (scc.Mask & (1 << p.Ordinal)) != 0 && SymbolEqualityComparer.Default.Equals(p.ContainingSymbol, scc.M) && T.NeedsRc(pt);
            if (IsFreshStruct(value))
            {
                EmitValueAs(value, pt);
                if (consume && TakeFresh(pt)) _consume!.Moved |= 1 << p.Ordinal;
                return;
            }
            if (!consume && TryPushBorrowedStruct(p, value, pt)) return;
            int off = TempStruct(pt);
            EmitValueAs(value, pt);
            int src = Scratch(ValType.I32); E.Set(src);
            E.Get(_fb); E.I32(off); E.Op(Op.I32Add);
            E.Get(src); CopyStructInit(pt);
            FreeScratch(ValType.I32, src);
            E.Get(_fb); E.I32(off); E.Op(Op.I32Add);
            if (consume && _temps.Count > 0 && _temps[^1].LastOrDefault(x => x.Local < 0 && x.FrameOffset == off && !x.Moved) is { } ts)
            {
                ts.Moved = true;
                _consume!.Moved |= 1 << p.Ordinal;
            }
            return;
        }
        var o = EmitValueAs(value, pt);
        // a fresh value for a parameter the callee consumes: its count goes to the callee (OwnedParams.cs); the temp
        // holds it until the call, for an exception while the later arguments are computed
        if (Types.IsRef(pt) && o == Own.Owned && _consume is { } cc && (cc.Mask & (1 << p.Ordinal)) != 0 && SymbolEqualityComparer.Default.Equals(p.ContainingSymbol, cc.M))
        {
            int tl = TempRef(pt);
            E.Tee(tl);
            cc.Temps.Add(tl);
            cc.Moved |= 1 << p.Ordinal;
            return;
        }
        if (Types.IsRef(pt)) ToCallSafe(o, pt);
    }

    void EmitAddressOfCopy(IOperation value, ITypeSymbol pt)
    {
        if (Types.IsStruct(pt)) { EmitValueAs(value, pt); return; }
        int off = TempValue(pt);
        var o = EmitValueAs(value, pt);
        if (Types.IsRef(pt)) ToOwned(o, pt);
        int sv = Scratch(Types.WasmType(pt)!.Value); E.Set(sv);
        E.Get(_fb); E.Get(sv); E.Store(Types.StoreOp(pt), off);
        FreeScratch(Types.WasmType(pt)!.Value, sv);
        E.Get(_fb); E.I32(off); E.Op(Op.I32Add);
    }

    static bool IsFreshStruct(IOperation op)
    {
        while (op is IConversionOperation { Conversion.IsIdentity: true } c) op = c.Operand;
        return op is IObjectCreationOperation or IDefaultValueOperation or IBinaryOperation { OperatorMethod: not null } or IUnaryOperation { OperatorMethod: not null }
               || op is IInvocationOperation inv && !inv.TargetMethod.ReturnsByRef && !inv.TargetMethod.ReturnsByRefReadonly
               || op is IPropertyReferenceOperation pr && pr.Property.GetMethod != null && !pr.Property.ReturnsByRef && Driver.AutoPropertyField(pr.Property.OriginalDefinition) == null;
    }

    bool IsDirectStructProducer(IOperation op)
    {
        while (op is IConversionOperation { Conversion.IsIdentity: true } c) op = c.Operand;
        return op is IObjectCreationOperation { Constructor: not null } oc && Types.IsStruct(TypeOf(oc))
            || op is IInvocationOperation inv && ReturnsStruct(SubstMethod(inv.TargetMethod)) && inv.TargetMethod.MethodKind != MethodKind.LocalFunction;
    }

    /// <summary>Constructs or returns a struct value straight into the destination address.</summary>
    void EmitStructInto(IOperation op, Action pushDst)
    {
        while (op is IConversionOperation { Conversion.IsIdentity: true } c) op = c.Operand;
        if (op is IObjectCreationOperation oc)
        {
            var t = TypeOf(oc);
            EmitStructCtor(oc, t, pushDst);
            return;
        }
        var inv = (IInvocationOperation)op;
        var m = SubstMethod(inv.TargetMethod);
        Action? pushThis = null;
        bool virt = inv.IsVirtual;
        if (!m.IsStatic && inv.Instance != null)
        {
            var rt = TypeOf(inv.Instance);
            if (rt.IsValueType)
            {
                // an interface method on a struct (through a type parameter's constraint): the struct's own
                // implementation, as EmitCallOps resolves it
                if (Types.Key(T.Canonical(m.ContainingType)) != Types.Key(T.Canonical(rt)) && D.Implementation(rt, m) is { } impl
                    && Types.Key(T.Canonical(impl.ContainingType)) == Types.Key(T.Canonical(rt)))
                    m = impl;
                var callee = m;
                pushThis = () => PushValueTypeReceiver(inv.Instance, rt, callee); virt = false;
            }
            else pushThis = () => { var o = EmitValue(inv.Instance); ToCallSafe(o, rt); if (!IsKnownNonNull(inv.Instance)) NullCheckKeep(); };
        }
        CallWith(m, virt, pushThis, () => PushArgs(m, inv.Arguments.Cast<IOperation>().ToList()), pushDst, inv.Syntax);
        // (a span over a collection's storage, written where it goes: the storage kept, StorageKeeps.cs)
        if (D.ExposesStorage(m)) { pushDst(); KeepStorage(m, inv.Syntax, false, inv.Instance, inv.Arguments.Cast<IOperation>().ToList()); E.Op(Op.Drop); }
    }

    static IOperation StripConversions(IOperation o) { while (o is IConversionOperation c) o = c.Operand; return o; }

    void MarkArrayOf(ITypeSymbol element)
    {
        var e = T.Resolve(element, Map);
        if (e is ITypeParameterSymbol || e.IsRefLikeType || e.SpecialType == SpecialType.System_Void || e is IPointerTypeSymbol || e is INamedTypeSymbol { IsUnboundGenericType: true } || e.IsStatic) return;
        D.MarkConstructed(T.Canonical(D.Main.CreateArrayTypeSymbol(e)));
    }

    /// <summary>Cs2Wasm.Runtime.Generics.New&lt;T&gt;("Open`1"): `new Open&lt;T&gt;()`, the type closed now.</summary>
    Own EmitGenericsNew(IInvocationOperation inv)
    {
        var name = inv.Arguments[0].Value.ConstantValue.Value as string ?? throw new CompileException("Generics.New needs a constant type name", inv.Syntax.GetLocation());
        var open = D.Lib.TypeByMetadataName(name) ?? throw new CompileException("library type missing: " + name, inv.Syntax.GetLocation());
        var args = inv.TargetMethod.TypeArguments.Select(a => T.Resolve(a, Map)).ToArray();
        if (inv.TargetMethod.Name == "NewOfNullableArg") args = [((INamedTypeSymbol)args[0]).TypeArguments[0]];
        return NewObject((INamedTypeSymbol)T.Canonical(open.Construct(args)), inv.Syntax);
    }

    /// <summary>`new C()` for a class symbol: allocation, then the parameterless constructor. Owned.</summary>
    Own NewObject(INamedTypeSymbol t, SyntaxNode? syntax)
    {
        int tid = D.MarkConstructed(t);
        int obj = E.AddLocal(ValType.I32);
        E.I32(Math.Max(T.LayoutOf(t).Size, Types.ObjectHeader)); E.Tid(tid); E.Call(Rt.AllocObject); E.Set(obj);
        var ctor = t.InstanceConstructors.FirstOrDefault(c => c.Parameters.Length == 0)
            ?? throw new CompileException(t + " has no parameterless constructor", syntax?.GetLocation());
        var slot = new Slot { Local = obj, Type = t };
        RegisterTemp(slot);
        CallWith(D.CanonicalMethod(ctor), false, () => E.Get(obj), () => { }, null, syntax);
        _temps[^1].Remove(slot);
        E.Get(obj);
        return Own.Owned;
    }

    internal static bool IsOmittedConditionalCall(IInvocationOperation inv)
    {
        var m = inv.TargetMethod.OriginalDefinition;
        if (!m.ReturnsVoid) return false;
        // a partial method without an implementation: its calls are omitted, arguments included (C# 15.6.9)
        if (m.IsPartialDefinition && m.PartialImplementationPart == null) return true;
        var symbols = m.GetAttributes().Where(a => a.AttributeClass?.Name == "ConditionalAttribute" && a.ConstructorArguments.Length == 1)
            .Select(a => a.ConstructorArguments[0].Value as string).ToList();
        if (symbols.Count == 0) return false;
        var defined = inv.Syntax.SyntaxTree.Options.PreprocessorSymbolNames;
        return !symbols.Any(s => s != null && defined.Contains(s));
    }

    /// <summary>Calls a resolved method. Pushes the struct-return destination, then receiver, then arguments.</summary>
    Own CallWith(IMethodSymbol m, bool virt, Action? pushThis, Action pushArgs, Action? intoAddr, SyntaxNode? syntax, IReadOnlyDictionary<int, ConstArg>? consts = null, bool stableRecv = false, bool thisByValue = false)
    {
        bool sret = ReturnsStruct(m);
        int sretOff = -1;
        // wanting this call's ref return as an address is not wanting its arguments' (`ref span[i].GetRawData()`)
        bool wantAddress = _wantRefReturnAddress;
        _wantRefReturnAddress = false;
        if (sret)
        {
            if (intoAddr != null) intoAddr();
            else { sretOff = TempStruct(T.Canonical(m.ReturnType)); E.Get(_fb); E.I32(sretOff); E.Op(Op.I32Add); }
        }
        // a direct call of a method that consumes parameters (OwnedParams.cs): fresh arguments are handed over
        bool dispatch = virt && IsVirtualDispatch(m);
        CallConsume? cc = null;
        if (consts == null && _clone == null && !dispatch && m.Parameters.Length > 0 && !D.IsIntrinsic(m) && !IsStaticInterfaceMember(m)
            && D.ConsumingOf(m).Mask is var cmask && cmask != 0 && D.CanSpecializeOwnership(m)
            // (List<T>.Add's item moves into an element, which only a reference does: OwnedParams.cs, IsListAdd)
            && (!Driver.IsListAdd(m.OriginalDefinition) || Types.IsRef(T.Canonical(m.Parameters[0].Type)))
            // (and Dictionary's value, the second parameter of TryInsert, Add, TryAdd and the indexer's setter: IsMapInsert)
            && (!Driver.IsMapInsert(m.OriginalDefinition) || m.Parameters.Length > 1 && Types.IsRef(T.Canonical(m.Parameters[1].Type))))
            cc = new CallConsume(m, cmask);
        var savedConsume = _consume;
        _consume = cc;
        int rec0 = _recorded.Count;
        try
        {
            pushThis?.Invoke();
            pushArgs();
        }
        finally { _consume = savedConsume; }
        // (a call whose arguments' sources were all recorded: Sharing.cs)
        bool certified = Sh != null && AllRecorded(m, rec0);
        bool certifiedRefs = Sh != null && AllRefsRecorded(m, rec0);
        if (_recorded.Count > rec0) _recorded.RemoveRange(rec0, _recorded.Count - rec0);
        // (the callee owns them now)
        if (cc != null) foreach (var tl in cc.Temps) { E.I32(0); E.Set(tl); }
        _wantRefReturnAddress = wantAddress;
        _certifyCall = certified;
        _certifyRefs = certifiedRefs;
        try { return EmitCallInstr(m, dispatch, syntax, sretOff, consts, cc?.Moved ?? 0, stableRecv, thisByValue); }
        finally { _certifyCall = _certifyRefs = false; }
    }

    /// <summary>Arguments are already on the stack (not for struct-returning methods unless the destination was pushed first).</summary>
    Own EmitCallCore(IMethodSymbol m, object? unused, IReadOnlyList<IOperation> none, bool virt, int preArgs, bool forceInstance = false, ITypeSymbol? receiverType = null)
    {
        if (ReturnsStruct(m)) throw new CompileException("internal: struct-returning call without destination: " + m);
        return EmitCallInstr(m, virt && IsVirtualDispatch(m), null, -1);
    }

    Own EmitCallInstr(IMethodSymbol m, bool virt, SyntaxNode? syntax, int sretOff, IReadOnlyDictionary<int, ConstArg>? consts = null, int owned = 0, bool stableRecv = false, bool thisByValue = false)
    {
        int outBorrow = _outBorrowNext; _outBorrowNext = 0;
        if (IsStaticInterfaceMember(m) && TryStaticInterfaceMember(ref m, out var sim)) return sim;
        var rt = T.Canonical(m.ReturnType);
        if (D.IsIntrinsic(m))
        {
            int before = E.Fn.Body.Count;
            var io = Driver.IsUpstreamIntrinsic(m) ? EmitUpstreamIntrinsic(m) : EmitIntrinsic(m);
            // an intrinsic that writes memory may overwrite an element someone borrows (FieldBorrows.cs)
            for (int k = before; k < E.Fn.Body.Count; k++)
                if (Driver.WritesMemory(E.Fn.Body[k].Op)) { D.NoteFieldWrite(E, Driver.ElementsKey); break; }
            if (!m.ReturnsByRef && !m.ReturnsByRefReadonly) return io;
            // a ref-returning intrinsic leaves the address: callers that want the value load through it
            if (_wantRefReturnAddress) { _wantRefReturnAddress = false; return Own.None; }
            if (Types.IsStruct(rt)) return Own.None;
            E.Load(Types.LoadOp(rt));
            return Types.IsRef(rt) ? Own.Unstable : Own.None;
        }
        if (_clone != null) consts = null;
        if (virt) { E.Call(D.Stub(m, consts)); MarkCertified(); }
        else
        {
            var mi = D.Method(m, consts);
            if (outBorrow > 0 && owned == 0 && !thisByValue) mi = D.OutBorrowVariant(mi, outBorrow - 1);
            else if (outBorrow > 0) throw new CompileException("internal: a borrowed out argument for a call that takes another copy");
            else if (owned != 0) mi = D.OwnedVariant(mi, owned);
            else if (thisByValue) mi = D.ThisValueVariant(mi);
            if (_clone != null)
            {
                // a clone (section 7.1): suppressed imports do nothing; other bodies are cloned too
                if (mi.Impl == MethodImpl.Import && _clone.Suppressed.Contains(mi.Key))
                {
                    for (int i = 0; i < mi.Fn.Type.Params.Length; i++) E.Op(Op.Drop);
                    foreach (var r in mi.Fn.Type.Results)
                        switch (r) { case ValType.I64: E.I64(0); break; case ValType.F32: E.F32(0); break; case ValType.F64: E.F64(0); break; default: E.I32(0); break; }
                    return Own.None;
                }
                if (mi.Impl is MethodImpl.Body or MethodImpl.Synthesized) mi = D.CloneMethod(m, _clone);
            }
            mi.FirstUse ??= syntax?.GetLocation();
            if (mi.Impl == MethodImpl.Missing && m.IsAbstract) throw new CompileException("call to abstract method " + m.ToDisplayString());
            if (mi.BorrowedReturn is { } mb && _clone == null)
            {
                // returns its receiver: borrowed from what the caller already holds (the receiver lives to the end
                // of the statement, in a temp if it was not a variable)
                E.Call(mb.Fn);
                MarkCertified();
                Inst?.Callees.Add(mb);
                // a field's value from a receiver that stays put: held across calls like the field's own load
                // (ToCallSafe: no count while nothing stores the fields it came through)
                if (stableRecv && mb.BorrowKeys != null && mb.BorrowedKind == Own.Unstable) _lastFieldLoad = (E.Fn.Body.Count, mb.BorrowKeys);
                return mb.BorrowedKind;
            }
            E.Call(mi.Fn);
            MarkCertified();
            Inst?.Callees.Add(mi);
        }
        if (m.ReturnsByRef || m.ReturnsByRefReadonly)
        {
            // a ref return: callers that want the value load through it
            if (_wantRefReturnAddress) { _wantRefReturnAddress = false; return Own.None; }
            if (Types.IsStruct(rt)) return Own.None;
            E.Load(Types.LoadOp(rt));
            return Types.IsRef(rt) ? Own.Unstable : Own.None;
        }
        if (sretOff >= 0) { E.Get(_fb); E.I32(sretOff); E.Op(Op.I32Add); NoteFresh(sretOff); return Own.None; }
        if (Types.IsVoid(rt) || ReturnsStruct(m)) return Own.None;
        return Types.IsRef(rt) ? Own.Owned : Own.None;
    }

    bool _wantRefReturnAddress;

    /// <summary>Used by internal callers: receiver pushed by an action, arguments optional (null = default).</summary>
    Own EmitCallResolved(IMethodSymbol m, Action? recv, IReadOnlyList<IOperation?> args, bool virt, Action? intoAddr = null)
    {
        return CallWith(m, virt, m.IsStatic ? null : recv, () =>
        {
            for (int i = 0; i < m.Parameters.Length; i++) PushArg(m.Parameters[i], i < args.Count ? args[i] : null);
        }, intoAddr, null);
    }

    /// <summary>Upstream [Intrinsic] members (Driver.IsUpstreamIntrinsic), with their arguments on the stack: refs and
    /// pointers are addresses, nint/nuint are i32.</summary>
    Own EmitUpstreamIntrinsic(IMethodSymbol m)
    {
        ITypeSymbol Arg(int i) => T.Canonical(m.TypeArguments[i]);
        int Size(ITypeSymbol t) => T.StorageOf(t).size;
        void NoCounted(ITypeSymbol t)
        {
            if (T.NeedsRc(t)) throw new CompileException($"{Driver.Display(m)} on {t}, which holds counted references, is not supported");
        }
        if (Types.FullName(m.ContainingType) == "System.Numerics.BitOperations")
        {
            bool l = Types.Is64(Types.PrimOf(m.Parameters[0].Type));
            switch (m.Name)
            {
                case "LeadingZeroCount": E.Op(l ? Op.I64Clz : Op.I32Clz); break;
                case "TrailingZeroCount": E.Op(l ? Op.I64Ctz : Op.I32Ctz); break;
                case "PopCount": E.Op(l ? Op.I64Popcnt : Op.I32Popcnt); break;
                // (the count is an int; the instruction takes it in the value's width, and by its low bits as .NET does)
                case "RotateLeft": if (l) { E.Op(Op.I64ExtendI32U); E.Op(Op.I64Rotl); } else E.Op(Op.I32Rotl); return Own.None;
                case "RotateRight": if (l) { E.Op(Op.I64ExtendI32U); E.Op(Op.I64Rotr); } else E.Op(Op.I32Rotr); return Own.None;
                // Log2(0) is 0: value |= 1; 31 ^ lzcnt
                case "Log2":
                    if (l) { E.I64(1); E.Op(Op.I64Or); E.Op(Op.I64Clz); E.I64(63); E.Op(Op.I64Xor); }
                    else { E.I32(1); E.Op(Op.I32Or); E.Op(Op.I32Clz); E.I32(31); E.Op(Op.I32Xor); }
                    break;
            }
            if (l) E.Op(Op.I32WrapI64);
            return Own.None;
        }
        switch (Types.FullName(m.ContainingType) + "." + m.Name)
        {
            case "System.Runtime.CompilerServices.Unsafe.AsPointer":
            case "System.Runtime.CompilerServices.Unsafe.AsRef":
                return Own.None;
            case "System.Runtime.CompilerServices.Unsafe.As":
                {
                    // As<T>(object): the same object; As<TFrom, TTo>(ref TFrom): the same address
                    if (m.Parameters[0].RefKind == RefKind.None) return Own.Unstable;
                    // (a store through the TTo ref is one into the TFrom slot: Sharing.cs)
                    if (m.TypeArguments.Length == 2) { Sh?.RefAlias(Arg(0), Arg(1)); if (!InRefWriter) D.Arenas.RefAlias(Arg(0), Arg(1)); }
                    // a float's bits read as something else (BitConverter, BitCast): a NaN becomes the canonical NaN
                    // first, so its payload never shows (section 7.3)
                    if (m.TypeArguments.Length == 2 && Types.PrimOf(Arg(0)) is Prim.F32 or Prim.F64 && Types.PrimOf(Arg(1)) is not (Prim.F32 or Prim.F64))
                    {
                        bool d = Types.PrimOf(Arg(0)) == Prim.F64;
                        int a = Scratch(ValType.I32);
                        E.Tee(a);
                        E.Load(d ? Op.F64Load : Op.F32Load); E.Get(a); E.Load(d ? Op.F64Load : Op.F32Load); E.Op(d ? Op.F64Ne : Op.F32Ne);
                        E.If();
                        E.Get(a);
                        // (.NET's double.NaN and float.NaN: what programs that print the bits of a NaN see there too)
                        if (d) { E.I64(unchecked((long)0xFFF8000000000000)); E.Store(Op.I64Store); } else { E.I32(unchecked((int)0xFFC00000)); E.Store(Op.I32Store); }
                        E.End();
                        E.Get(a);
                        FreeScratch(ValType.I32, a);
                    }
                    return Own.None;
                }
            case "System.Runtime.CompilerServices.Unsafe.AreSame": E.Op(Op.I32Eq); return Own.None;
            case "System.Runtime.CompilerServices.Unsafe.IsAddressGreaterThan": E.Op(Op.I32GtU); return Own.None;
            case "System.Runtime.CompilerServices.Unsafe.IsAddressLessThan": E.Op(Op.I32LtU); return Own.None;
            case "System.Runtime.CompilerServices.Unsafe.AddByteOffset": E.Op(Op.I32Add); return Own.None;
            case "System.Runtime.CompilerServices.Unsafe.SubtractByteOffset": E.Op(Op.I32Sub); return Own.None;
            case "System.Runtime.CompilerServices.Unsafe.ByteOffset":
                {
                    // (origin, target) -> target - origin
                    int t2 = Scratch(ValType.I32); E.Set(t2);
                    int t1 = Scratch(ValType.I32); E.Set(t1);
                    E.Get(t2); E.Get(t1); E.Op(Op.I32Sub);
                    FreeScratch(ValType.I32, t1); FreeScratch(ValType.I32, t2);
                    return Own.None;
                }
            case "System.Reflection.MethodBase.GetCurrentMethod":
                // the method whose code this is, as its stack frame names it (null for what has no record, as a
                // frame's method: a lambda's, a local function's)
                E.I32Addr(D.StringLiteral(E.Fn.FrameName ?? Driver.FrameName(OuterMethod)));
                E.Call(D.LibMethod("Cs2Wasm.Runtime.Meta", "FrameMethod", 1).Fn);
                return Own.Owned;
            case "System.Activator.CreateInstance":
                {
                    var t = Arg(0);
                    if (t.IsValueType) return EmitDefault(t);
                    if (t is not INamedTypeSymbol { IsAbstract: false } nt || nt.InstanceConstructors.FirstOrDefault(c => c.Parameters.Length == 0) is not { } ctor)
                    {
                        // (as .NET: a MissingMethodException when it runs)
                        E.I32Addr(D.StringLiteral(Driver.ClrName(t)));
                        E.Call(D.LibMethod("Cs2Wasm.Runtime.ThrowHelpers", "NoDefaultConstructor", 1).Fn);
                        E.I32(0);
                        return Own.Owned;
                    }
                    int tid = D.MarkConstructed(t);
                    int obj = E.AddLocal(ValType.I32);
                    E.I32(Math.Max(T.LayoutOf(t).Size, Types.ObjectHeader)); E.Tid(tid); E.Call(Rt.AllocObject); E.Set(obj);
                    CallWith(D.CanonicalMethod(ctor), false, () => E.Get(obj), () => { }, null, null);
                    E.Get(obj);
                    return Own.Owned;
                }
            case "System.Runtime.CompilerServices.Unsafe.Copy":
                {
                    var t = Arg(0); NoCounted(t);
                    E.I32(Size(t)); E.Op(Op.MemoryCopy);
                    return Own.None;
                }
            case "System.Runtime.CompilerServices.Unsafe.CopyBlock":
            case "System.Runtime.CompilerServices.Unsafe.CopyBlockUnaligned":
                E.Op(Op.MemoryCopy); return Own.None;
            case "System.Runtime.CompilerServices.Unsafe.InitBlock":
            case "System.Runtime.CompilerServices.Unsafe.InitBlockUnaligned":
                E.Op(Op.MemoryFill); return Own.None;
            case "System.Runtime.CompilerServices.Unsafe.SkipInit": E.Op(Op.Drop); return Own.None;
            case "System.Runtime.CompilerServices.Unsafe.Unbox":
                E.I32(BoxDataOffset(T, Arg(0))); E.Op(Op.I32Add); return Own.None;
            case "System.SpanHelpers.memmove": E.Op(Op.MemoryCopy); return Own.None; // memory.copy allows overlap
            case "System.SpanHelpers.memset": E.Op(Op.MemoryFill); return Own.None;
            case "System.SpanHelpers.Memmove": E.Op(Op.MemoryCopy); return Own.None;
            case "System.SpanHelpers.ClearWithoutReferences":
                {
                    int n = Scratch(ValType.I32);
                    E.Set(n); E.I32(0); E.Get(n); E.Op(Op.MemoryFill);
                    FreeScratch(ValType.I32, n);
                    return Own.None;
                }
            case "System.Double.ConvertToIntegerNative":
            case "System.Single.ConvertToIntegerNative":
                {
                    // "native" is the platform's; cs2wasm's is saturating, NaN to 0 (as on Arm), the same on every machine
                    bool f32 = Types.FullName(m.ContainingType) == "System.Single";
                    var tp = Types.PrimOf(Arg(0));
                    if (Types.Is64(tp))
                        E.Op(tp == Prim.U64 ? (f32 ? Op.I64TruncSatF32U : Op.I64TruncSatF64U) : (f32 ? Op.I64TruncSatF32S : Op.I64TruncSatF64S));
                    else
                    {
                        bool unsigned = !Types.IsSigned(tp);
                        E.Op(unsigned ? (f32 ? Op.I32TruncSatF32U : Op.I32TruncSatF64U) : (f32 ? Op.I32TruncSatF32S : Op.I32TruncSatF64S));
                        if (Types.PrimSize(tp) < 4)
                        {
                            // saturate to the narrow type's range
                            long lo = tp switch { Prim.I8 => sbyte.MinValue, Prim.I16 => short.MinValue, _ => 0 };
                            long hi = tp switch { Prim.I8 => sbyte.MaxValue, Prim.U8 => byte.MaxValue, Prim.I16 => short.MaxValue, _ => ushort.MaxValue };
                            int t = Scratch(ValType.I32);
                            E.Tee(t); E.I32((int)lo); E.Op(unsigned ? Op.I32LtU : Op.I32LtS);
                            E.If(ValType.I32); E.I32((int)lo); E.Else();
                            E.Get(t); E.I32((int)hi); E.Op(unsigned ? Op.I32GtU : Op.I32GtS);
                            E.If(ValType.I32); E.I32((int)hi); E.Else(); E.Get(t); E.End();
                            E.End();
                            FreeScratch(ValType.I32, t);
                        }
                    }
                    return Own.None;
                }
            // an object's data right after its header (a box's value)
            case "System.Runtime.CompilerServices.RuntimeHelpers.GetRawData": E.I32(Types.ObjectHeader); E.Op(Op.I32Add); return Own.None;
            case "System.Runtime.CompilerServices.RuntimeHelpers.IsBitwiseEquatable":
                E.I32(Types.PrimOf(Arg(0)) is not (Prim.None or Prim.F32 or Prim.F64) ? 1 : 0); return Own.None;
            case "System.Runtime.CompilerServices.RuntimeHelpers.IsReferenceOrContainsReferences":
                E.I32(Types.IsRef(Arg(0)) || T.NeedsRc(Arg(0)) ? 1 : 0); return Own.None;
            case "System.Runtime.CompilerServices.RuntimeHelpers.ObjectHasComponentSize":
                {
                    // strings (type id 1) and arrays
                    int tid = Scratch(ValType.I32);
                    E.Load(Op.I32Load, 4); E.Tee(tid); E.I32(1); E.Op(Op.I32Eq);
                    E.Get(tid); E.Call(Rt.TypeFlagsById); E.I32(4); E.Op(Op.I32And); E.I32(0); E.Op(Op.I32Ne);
                    E.Op(Op.I32Or);
                    FreeScratch(ValType.I32, tid);
                    return Own.None;
                }
            case "System.Threading.Interlocked.Exchange":
                {
                    // 1- and 2-byte locations: the narrow atomic exchange (zero-extended; signed types sign-extend after)
                    var pt = Types.PrimOf(m.Parameters[0].Type);
                    bool one = Types.PrimSize(pt) == 1;
                    E.Emit(one ? Op.I32AtomicRmw8XchgU : Op.I32AtomicRmw16XchgU, 0, one ? 0 : 1);
                    if (pt == Prim.I8) E.Op(Op.I32Extend8S); else if (pt == Prim.I16) E.Op(Op.I32Extend16S);
                    return Own.None;
                }
            case "System.Threading.Interlocked.CompareExchange":
                {
                    // (location, value, comparand) -> wasm's (location, expected, replacement)
                    var pt = Types.PrimOf(m.Parameters[0].Type);
                    bool one = Types.PrimSize(pt) == 1;
                    int cmp = Scratch(ValType.I32); E.Set(cmp);
                    int val = Scratch(ValType.I32); E.Set(val);
                    E.Get(cmp); if (!one) { E.I32(0xFFFF); E.Op(Op.I32And); } else { E.I32(0xFF); E.Op(Op.I32And); }
                    E.Get(val);
                    E.Emit(one ? Op.I32AtomicRmw8CmpxchgU : Op.I32AtomicRmw16CmpxchgU, 0, one ? 0 : 1);
                    FreeScratch(ValType.I32, val); FreeScratch(ValType.I32, cmp);
                    if (pt == Prim.I8) E.Op(Op.I32Extend8S); else if (pt == Prim.I16) E.Op(Op.I32Extend16S);
                    return Own.None;
                }
            case "System.Threading.Interlocked.MemoryBarrier":
            case "System.Threading.Volatile.ReadBarrier":
            case "System.Threading.Volatile.WriteBarrier":
                E.Emit(Op.AtomicFence); return Own.None;
            case "System.Object.GetType":
                {
                    // the RuntimeType of the object's type id
                    E.Load(Op.I32Load, 4);
                    var fromId = D.LibType("System.Type").GetMembers("FromId").OfType<IMethodSymbol>().First();
                    E.Call(D.Method(fromId).Fn);
                    return Own.Owned;
                }
        }
        throw new CompileException("intrinsic not supported: " + Driver.Display(m));
    }

    // =====================================================================
    // intrinsics (library methods marked [Intrinsic])
    // =====================================================================
    Own EmitIntrinsic(IMethodSymbol m)
    {
        var name = m.ContainingType.Name + "." + m.Name;
        switch (name)
        {
            case "TextScan.Eq16":
                {
                    // (addr, value): the eight chars compared with the value in every lane, one bit a lane
                    int v = Scratch(ValType.I32);
                    E.Set(v);
                    E.Op(Op.V128Load); E.Get(v); E.Op(Op.I16x8Splat); E.Op(Op.I16x8Eq); E.Op(Op.I16x8Bitmask);
                    FreeScratch(ValType.I32, v);
                    return Own.None;
                }
            case "TextScan.Eq16Any3":
                {
                    int c = Scratch(ValType.I32), b = Scratch(ValType.I32), a = Scratch(ValType.I32), p = Scratch(ValType.I32);
                    E.Set(c); E.Set(b); E.Set(a); E.Set(p);
                    E.Get(p); E.Op(Op.V128Load); E.Get(a); E.Op(Op.I16x8Splat); E.Op(Op.I16x8Eq);
                    E.Get(p); E.Op(Op.V128Load); E.Get(b); E.Op(Op.I16x8Splat); E.Op(Op.I16x8Eq); E.Op(Op.V128Or);
                    E.Get(p); E.Op(Op.V128Load); E.Get(c); E.Op(Op.I16x8Splat); E.Op(Op.I16x8Eq); E.Op(Op.V128Or);
                    E.Op(Op.I16x8Bitmask);
                    FreeScratch(ValType.I32, p); FreeScratch(ValType.I32, a); FreeScratch(ValType.I32, b); FreeScratch(ValType.I32, c);
                    return Own.None;
                }
            case "TextScan.Upper16": case "TextScan.Lower16":
                {
                    // dst = x - ((x - 'a') below 26 ? 32 : 0) (or + for lower, from 'A'); then the lanes above 127
                    bool up = name == "TextScan.Upper16";
                    int d = Scratch(ValType.I32), s = Scratch(ValType.I32);
                    E.Set(d); E.Set(s);
                    E.Get(d);
                    E.Get(s); E.Op(Op.V128Load);
                    E.Get(s); E.Op(Op.V128Load); E.I32(up ? 'a' : 'A'); E.Op(Op.I16x8Splat); E.Op(Op.I16x8Sub);
                    E.I32(26); E.Op(Op.I16x8Splat); E.Op(Op.I16x8LtU);
                    E.I32(32); E.Op(Op.I16x8Splat); E.Op(Op.V128And);
                    E.Op(up ? Op.I16x8Sub : Op.I16x8Add);
                    E.Op(Op.V128Store);
                    E.Get(s); E.Op(Op.V128Load); E.I32(127); E.Op(Op.I16x8Splat); E.Op(Op.I16x8GtU); E.Op(Op.I16x8Bitmask);
                    FreeScratch(ValType.I32, s); FreeScratch(ValType.I32, d);
                    return Own.None;
                }
            case "Mem.LoadI32": E.Load(Op.I32Load); return Own.None;
            case "Mem.LoadU8": E.Load(Op.I32Load8U); return Own.None;
            case "Mem.LoadI8": E.Load(Op.I32Load8S); return Own.None;
            case "Mem.LoadU16": E.Load(Op.I32Load16U); return Own.None;
            case "Mem.LoadI16": E.Load(Op.I32Load16S); return Own.None;
            case "Mem.LoadI64": E.Load(Op.I64Load); return Own.None;
            case "Mem.LoadF32": E.Load(Op.F32Load); return Own.None;
            case "Mem.LoadF64": E.Load(Op.F64Load); return Own.None;
            case "Mem.StoreI32": E.Store(Op.I32Store); return Own.None;
            case "Mem.StoreU8": E.Store(Op.I32Store8); return Own.None;
            case "Mem.StoreU16": E.Store(Op.I32Store16); return Own.None;
            case "Mem.StoreI64": E.Store(Op.I64Store); return Own.None;
            case "Mem.StoreF32": E.Store(Op.F32Store); return Own.None;
            case "Mem.StoreF64": E.Store(Op.F64Store); return Own.None;
            case "Mem.Copy": E.Op(Op.MemoryCopy); return Own.None;
            case "Mem.Fill": E.Op(Op.MemoryFill); return Own.None;
            case "Mem.Size": E.Op(Op.MemorySize); return Own.None;
            case "Mem.Grow": E.Op(Op.MemoryGrow); return Own.None;
            case "Rt.AddressOf": return Own.None;
            case "Rt.Take": return Own.Owned;
            case "Rt.Borrow": return Own.Unstable;
            case "Rt.TypeId": E.Load(Op.I32Load, 4); return Own.None;
            case "Rt.RetainContents": E.Call(D.Rt.RetainContents); return Own.None;
            case "Rt.TypeIdOf": E.Tid(D.TypeId(T.Canonical(m.TypeArguments[0]))); return Own.None;
            case "Rt.MarkConstructed": E.Tid(D.MarkConstructed(T.Canonical(m.TypeArguments[0]))); return Own.None;
            case "Rt.HeapBase": E.I32Addr(new LateAddr(() => D.HeapBase)); return Own.None;
            case "Rt.Retain": E.Call(Rt.Retain); E.Op(Op.Drop); return Own.None;
            case "Rt.Release": E.Call(Rt.Release); return Own.None;
            case "Rt.SccOf": E.Call(Rt.SccOfFn); return Own.None;
            case "Rt.ArenaSlots": E.Call(Rt.ArenaSlotsFn); return Own.None;
            case "Rt.NoWaitingTop": E.I32(Environment.GetEnvironmentVariable("CS2WASM_NO_WAITING_TOP") == "1" ? 1 : 0); return Own.None;
            case "Rt.NoFusedJoin": E.I32(Environment.GetEnvironmentVariable("CS2WASM_NO_FUSED_JOIN") == "1" ? 1 : 0); return Own.None;
            case "Cs2WasmArrays.BoxesElements": D.MarkBoxedElements(); return Own.None;
            case "Rt.ArenaFinalizeObject": E.Call(Rt.ArenaFinFn); return Own.None;
            case "Rt.ArenaFinType": E.Call(Rt.ArenaFinTypeFn); return Own.None;
            case "Rt.SingleThreaded": E.Call(Rt.SingleThreadedFn); return Own.None;
            case "Rt.WideSizes": E.Call(Rt.WideSizesFn); return Own.None;
            case "Rt.FirstChanceWanted": E.Call(Rt.FirstChanceFn); return Own.None;
            case "Rt.FirstChance": E.Call(Rt.RaiseFirstChanceFn); return Own.None;
            case "Rt.FramesWanted": E.Call(Rt.FramesWantedFn); return Own.None;
            case "Rt.TakeThrowSite": E.GGet(D.ThrowSite); E.I32(0); E.GSet(D.ThrowSite); return Own.None;
            case "Rt.ThrowSites": E.Call(D.ThrowSitesFn); return Own.None;
            case "Rt.NoThreads": E.I32(Driver.s_noThreads ? 1 : 0); return Own.None;
            case "Rt.MemStatsGrow": if (MemStats) E.Call(D.LibMethod("Cs2Wasm.Runtime.Rt", "MsGrow", 0).Fn); return Own.None;
            case "Rt.MemStatsRecord": if (MemStats) E.Call(D.LibMethod("Cs2Wasm.Runtime.Rt", "MsRecord", 1).Fn); else E.Op(Op.Drop); return Own.None;
            case "Rt.MemStatsHash": if (MemStats) E.Call(D.LibMethod("Cs2Wasm.Runtime.Rt", "MsHash", 1).Fn); else E.Op(Op.Drop); return Own.None;
            case "Rt.MemStatsOn": E.I32(MemStats ? 1 : 0); return Own.None;
            case "Rt.RipePages": E.I32(s_noRipePages ? 0 : 1); return Own.None;
            case "Rt.HoldPages": E.I32(s_noHoldPages ? 0 : 1); return Own.None;
            case "Rt.DropStats": E.I32(Environment.GetEnvironmentVariable("CS2WASM_DROP_STATS") == "1" ? 1 : 0); return Own.None;
            case "Rt.ArenaReport": E.I32(Environment.GetEnvironmentVariable("CS2WASM_ARENA_REPORT") == "1" ? 1 : 0); return Own.None;
            case "Rt.StaleHolds": E.I32(Environment.GetEnvironmentVariable("CS2WASM_STALE_HOLDS") == "1" ? 1 : 0); return Own.None;
            case "Rt.NoTakeIn": E.I32(Environment.GetEnvironmentVariable("CS2WASM_NO_TAKE_IN") == "1" ? 1 : 0); return Own.None;
            case "Rt.NoStaleNote": E.I32(Environment.GetEnvironmentVariable("CS2WASM_NO_STALE_NOTE") == "1" ? 1 : 0); return Own.None;
            case "Rt.NoDyingQueued": E.I32(Environment.GetEnvironmentVariable("CS2WASM_NO_DYING_QUEUED") == "1" ? 1 : 0); return Own.None;
            case "Rt.NoTurnHold": E.I32(Environment.GetEnvironmentVariable("CS2WASM_NO_TURN_HOLD") == "1" ? 1 : 0); return Own.None;
            case "Rt.TurnCheck": E.I32(Environment.GetEnvironmentVariable("CS2WASM_TURN_CHECK") == "1" ? 1 : 0); return Own.None;
            case "Rt.SurvivorCheck": E.I32(Environment.GetEnvironmentVariable("CS2WASM_SURVIVOR_CHECK") == "1" ? 1 : 0); return Own.None;
            case "Rt.NoFreshJoin":E.I32(Environment.GetEnvironmentVariable("CS2WASM_NO_FRESH_JOIN") == "1" ? 1 : 0); return Own.None;
            case "Rt.NoSlotWalk": E.I32(Environment.GetEnvironmentVariable("CS2WASM_SLOT_WALK") == "1" ? 0 : 1); return Own.None; // (off by default: with it the strings scene of tools/frames.mjs has one 0.6 ms frame, cause not found; tree the same either way)
            case "Rt.ArrayRankAt": E.Call(Rt.ArrayRank); return Own.None;
            case "Rt.ArrayElementSizeAt": E.Call(Rt.ArrayElemSize); return Own.None;
            case "Rt.ArrayGrow": E.Call(Rt.ArrayGrowFn); return Own.None;
            case "Rt.NoMergeRepoint": E.I32(Environment.GetEnvironmentVariable("CS2WASM_NO_MERGE_REPOINT") == "1" ? 1 : 0); return Own.None;
            case "Rt.FusedDrop": E.I32(RuntimeGen.FusedDrop(D) ? 1 : 0); return Own.None;
            case "Rt.NoFreeList": E.I32(Environment.GetEnvironmentVariable("CS2WASM_NO_FREE_LIST") == "1" ? 1 : 0); return Own.None;
            case "Rt.NoPinnedSkip": E.I32(Environment.GetEnvironmentVariable("CS2WASM_NO_PINNED_SKIP") == "1" ? 1 : 0); return Own.None;
            case "Rt.NoOwnedChain": E.I32(Environment.GetEnvironmentVariable("CS2WASM_NO_OWNED_CHAIN") == "1" ? 1 : 0); return Own.None;
            case "Rt.NoFastDestroy": E.I32(Environment.GetEnvironmentVariable("CS2WASM_NO_FAST_DESTROY") == "1" ? 1 : 0); return Own.None;
            case "Rt.NoDyingUp": E.I32(Environment.GetEnvironmentVariable("CS2WASM_NO_DYING_UP") == "1" ? 1 : 0); return Own.None;
            case "Rt.NoDestroyByBits": E.I32(Environment.GetEnvironmentVariable("CS2WASM_NO_DESTROY_BY_BITS") == "1" ? 1 : 0); return Own.None;
            case "Rt.NoStoreWaits": E.I32(Environment.GetEnvironmentVariable("CS2WASM_NO_STORE_WAITS") == "1" ? 1 : 0); return Own.None;
            case "Rt.NoEarlyGiveBack": E.I32(Environment.GetEnvironmentVariable("CS2WASM_NO_EARLY_GIVE_BACK") == "1" ? 1 : 0); return Own.None;
            case "Rt.ArrayMoveMode": E.I32(Environment.GetEnvironmentVariable("CS2WASM_NO_ARRAY_MOVE") == "1" ? 1 : Environment.GetEnvironmentVariable("CS2WASM_ARRAY_MOVE_BREAK") == "1" ? 2 : Environment.GetEnvironmentVariable("CS2WASM_ARRAY_MOVE_CYCLE") == "1" ? 3 : 0); return Own.None;
            case "Rt.NoArrayReplace": E.I32(Environment.GetEnvironmentVariable("CS2WASM_NO_ARRAY_REPLACE") == "1" ? 1 : 0); return Own.None;
            case "Rt.NoDropCheck":E.I32(Environment.GetEnvironmentVariable("CS2WASM_NO_DROP_CHECK") == "1" ? 1 : 0); return Own.None;
            case "Rt.RefLock": E.Call(Rt.RefLock); return Own.None;
            case "Rt.BulkEnter": E.Call(Rt.BulkEnter); return Own.None;
            case "Rt.BulkExit": E.Call(Rt.BulkExit); return Own.None;
            case "Rt.RefUnlock": E.Call(Rt.RefUnlock); return Own.None;
            case "Rt.HoldEpoch": E.Call(Rt.FinHold); return Own.None;
            case "Rt.ReleaseEpoch": E.Call(Rt.FinUnhold); return Own.None;
            case "Rt.ReleaseThreadStatics": E.Call(Rt.ReleaseThreadStaticsFn); return Own.None;
            case "Rt.ObjectArrayDelegate": E.Call(D.ObjectArrayDelegateFn); return Own.Owned;
            case "Rt.DynamicInvokeRaw": E.Call(D.DynamicInvokeFn); return Own.Owned;
            case "Rt.DelegateArity": E.Call(D.DelegateArityFn); return Own.None;
            case "Rt.DelegateMethodEntry": E.Call(D.DelegateMethodFn); return Own.None;
            case "Rt.FrameMethods": E.Call(D.FrameMethodsFn); return Own.None;
            case "Rt.ArenaClearObject": E.Call(Rt.ArenaClearFn); return Own.None;
            case "Rt.ArenaFreeObject": E.Call(Rt.ArenaFreeFn); return Own.None;
            case "Rt.ArenaVisitObject": E.Call(Rt.ArenaVisitFn); return Own.None;
            case "Rt.ArenaIsSlot": E.Call(Rt.ArenaIsSlotFn); return Own.None;
            // (a slot known by its address: Arenas.LinkAt)
            case "Rt.LinkAt": E.Call(D.Arenas.LinkAt(D.ObjectType)); return Own.None;
            case "Rt.UnlinkAt": E.Call(D.Arenas.UnlinkAt(D.ObjectType)); return Own.None;
            case "Rt.MoveRefs": E.Call(D.Arenas.MoveAt()); return Own.None;
            case "Rt.TypeName": D.WantLazyTypeNames(); E.Call(Rt.TypeNameFn); return Own.Stable;
            case "Rt.IsInstanceOf": E.Call(Rt.IsInst(T.Canonical(m.TypeArguments[0]))); return Own.None;
            case "Rt.Unreachable": E.Op(Op.Unreachable); return Own.None;
            case "Rt.F64Bits": E.Op(Op.I64ReinterpretF64); return Own.None;
            case "Rt.F64FromBits": E.Op(Op.F64ReinterpretI64); return Own.None;
            case "Rt.F32Bits": E.Op(Op.I32ReinterpretF32); return Own.None;
            case "Rt.F32FromBits": E.Op(Op.F32ReinterpretI32); return Own.None;
            case "Rt.Sqrt":
                // The square root of both lanes of a 128-bit value, one lane taken: the same result as f64.sqrt.
                // The scalar instruction engines emit for f64.sqrt (sqrtsd) keeps the rest of its destination
                // register, so each root waits for whatever wrote that register last: in a loop, for the previous
                // turn's division. The two-lane instruction writes the whole register.
                if (Environment.GetEnvironmentVariable("CS2WASM_SCALAR_SQRT") == "1") { E.Op(Op.F64Sqrt); return Own.None; }
                E.Op(Op.F64x2Splat); E.Op(Op.F64x2Sqrt); E.Emit(Op.F64x2ExtractLane, 0, 0, null);
                return Own.None;
            case "Rt.Floor": E.Op(Op.F64Floor); return Own.None;
            case "Rt.Ceiling": E.Op(Op.F64Ceil); return Own.None;
            case "Rt.Truncate": E.Op(Op.F64Trunc); return Own.None;
            case "Rt.RoundEven": E.Op(Op.F64Nearest); return Own.None;
            case "Rt.Abs": E.Op(Op.F64Abs); return Own.None;
            case "Rt.Min": E.Op(Op.F64Min); return Own.None;
            case "Rt.Max": E.Op(Op.F64Max); return Own.None;
            // (the sign of a NaN differs between machines: .NET's NaN's, section 7.3)
            case "Rt.CopySign": E.Call(Rt.CanonF64); E.Op(Op.F64Copysign); return Own.None;
            case "Rt.SqrtF": E.Op(Op.F32Sqrt); return Own.None;
            case "Rt.FloorF": E.Op(Op.F32Floor); return Own.None;
            case "Rt.CeilingF": E.Op(Op.F32Ceil); return Own.None;
            case "Rt.TruncateF": E.Op(Op.F32Trunc); return Own.None;
            case "Rt.RoundEvenF": E.Op(Op.F32Nearest); return Own.None;
            case "Rt.AbsF": E.Op(Op.F32Abs); return Own.None;
            case "Rt.MinF": E.Op(Op.F32Min); return Own.None;
            case "Rt.MaxF": E.Op(Op.F32Max); return Own.None;
            case "Rt.I32Clz": E.Op(Op.I32Clz); return Own.None;
            case "Rt.I32Ctz": E.Op(Op.I32Ctz); return Own.None;
            case "Rt.I32Popcnt": E.Op(Op.I32Popcnt); return Own.None;
            case "Rt.I64Clz": E.Op(Op.I64Clz); return Own.None;
            case "Rt.I64Ctz": E.Op(Op.I64Ctz); return Own.None;
            case "Rt.I64Popcnt": E.Op(Op.I64Popcnt); return Own.None;
            case "Rt.I32Rotl": E.Op(Op.I32Rotl); return Own.None;
            case "Rt.I32Rotr": E.Op(Op.I32Rotr); return Own.None;
            case "Rt.I64Rotl": E.Op(Op.I64Rotl); return Own.None;
            case "Rt.I64Rotr": E.Op(Op.I64Rotr); return Own.None;
            case "Rt.SizeOf": E.I32(T.StorageOf(T.Canonical(m.TypeArguments[0])).size); return Own.None;
            case "Rt.IsReference": E.I32(Types.IsRef(T.Canonical(m.TypeArguments[0])) ? 1 : 0); return Own.None;
            case "Rt.ContainsReferences": E.I32(T.NeedsRc(T.Canonical(m.TypeArguments[0])) ? 1 : 0); return Own.None;
            case "Rt.BoxDataOffset": E.I32(BoxDataOffset(T, T.Canonical(m.TypeArguments[0]))); return Own.None;
            case "Rt.ReleaseElements":
                {
                    // (address, count) releases counted contents of `count` elements of T
                    var et = T.Canonical(m.TypeArguments[0]);
                    E.Call(Rt.ReleaseRange(et));
                    return Own.None;
                }
            case "Rt.RetainElements":
                {
                    var et = T.Canonical(m.TypeArguments[0]);
                    E.Call(Rt.RetainRange(et));
                    return Own.None;
                }
            case "Array.ElementSize": case "Cs2WasmArrays.ElementSize": E.Call(Rt.ArrayElemSize); return Own.None;
            case "Array.RankOf": case "Cs2WasmArrays.RankOf": E.Call(Rt.ArrayRank); return Own.None;
            case "Array.HasCountedElements": case "Cs2WasmArrays.HasCountedElements": E.Call(Rt.ArrayCounted); return Own.None;
            case "ArrayHelpers.CopyElements": case "Cs2WasmArrays.CopyElements": E.Call(Rt.ArrayCopy); return Own.None;
            case "ArrayHelpers.ClearElements": case "Cs2WasmArrays.ClearElements": E.Call(Rt.ArrayClear); return Own.None;
            case "EnumFormat.Info": E.Call(Rt.EnumInfo); return Own.None;
            case "EnumFormat.InfoOfId": case "TypeInfo.EnumInfo": E.Call(Rt.EnumInfoId); return Own.None;
            case "Type.FullNameOf": D.WantLazyTypeNames(); E.Call(Rt.TypeNameById); return Own.Stable;
            // Cs2Wasm.Runtime.TypeInfo (TypeInfo.cs): tables by type id
            case "TypeInfo.FullName": D.WantLazyTypeNames(); E.Call(Rt.TypeNameById); return Own.Stable;
            case "TypeInfo.NameTable": E.Call(Rt.TypeNameTableFn); return Own.None;
            case "TypeInfo.ProgramTypes": E.Call(Rt.ProgramTypesFn); return Own.None;
            case "TypeInfo.Instances": E.Call(Rt.GenericInstancesFn); return Own.None;
            case "TypeInfo.Name": E.Call(Rt.TypeShortNameById); return Own.Stable;
            case "TypeInfo.Flags": E.Call(Rt.TypeFlagsById); return Own.None;
            case "TypeInfo.Namespace": E.Call(Rt.TypeInfo("namespace")); return Own.Stable;
            case "TypeInfo.BaseId": E.Call(Rt.TypeInfo("base")); return Own.None;
            case "TypeInfo.ElementId": E.Call(Rt.TypeInfo("element")); return Own.None;
            case "TypeInfo.Rank": E.Call(Rt.TypeInfo("rank")); return Own.None;
            case "TypeInfo.Attributes": E.Call(Rt.TypeInfo("attrs")); return Own.None;
            case "TypeInfo.CorElementType": E.Call(Rt.TypeInfo("cor")); return Own.None;
            case "TypeInfo.Flags2": E.Call(Rt.TypeInfo("flags2")); return Own.None;
            case "TypeInfo.GenericArgs": E.Call(Rt.TypeInfo("generic_args")); return Own.None;
            case "TypeInfo.Interfaces": E.Call(Rt.TypeInfo("interfaces")); return Own.None;
            case "TypeInfo.Nested": E.Call(Rt.TypeInfo("nested")); return Own.None;
            case "TypeInfo.AssemblyOf": E.Call(Rt.TypeInfo("assembly")); return Own.None;
            case "TypeInfo.GenericParam": E.Call(Rt.TypeInfo("gp")); return Own.None;
            case "TypeInfo.GenericParamMethod": E.Call(Rt.TypeInfo("gp_method")); return Own.None;
            case "TypeInfo.GenericConstraints": E.Call(Rt.TypeInfo("gp_constraints")); return Own.None;
            case "TypeInfo.AssemblyNames": E.Call(D.AssemblyNamesFn); return Own.None;
            case "TypeInfo.ManifestResources": E.Call(D.ManifestResourcesFn); return Own.None;
            case "TypeInfo.ResourceInit": E.Call(D.ResourceInitFn); return Own.None;
            case "TypeInfo.ResourceUnpack": E.Call(D.ResourceUnpackFn); return Own.None;
            case "TypeInfo.DeclaringId": E.Call(Rt.TypeInfo("declaring")); return Own.None;
            case "TypeInfo.Size": E.Call(Rt.TypeInfo("size")); return Own.None;
            case "TypeInfo.BoxOffset": E.Call(Rt.TypeInfo("box_offset")); return Own.None;
            case "TypeInfo.ArrayOf": E.Call(Rt.TypeInfo("array_of")); return Own.None;
            case "TypeInfo.Meta": E.Call(D.MetaOf); return Own.None;
            case "Type.ShortNameOf": E.Call(Rt.TypeShortNameById); return Own.Stable;
            case "Type.FlagsOf": E.Call(Rt.TypeFlagsById); return Own.None;
            case "Type.MetaOf": E.Call(D.MetaOf); return Own.None;
            case "Reflect.Invoke":
            case "TypeInfo.Invoke":
                {
                    // (thunk + 1, target, args): call_indirect
                    int a2 = E.AddLocal(ValType.I32), a1 = E.AddLocal(ValType.I32), fi = E.AddLocal(ValType.I32);
                    E.Set(a2); E.Set(a1); E.Set(fi);
                    E.Get(a1); E.Get(a2); E.Get(fi); E.I32(1); E.Op(Op.I32Sub);
                    E.Emit(Op.CallIndirect, 0, D.IndirectBucket(Driver.ThunkBucket), Driver.ThunkType);
                    return Own.Owned;
                }
            case "Atomic.Load32": E.Emit(Op.I32AtomicLoad, 0, 2); return Own.None;
            case "Atomic.Store32": E.Emit(Op.I32AtomicStore, 0, 2); return Own.None;
            case "Atomic.Add32": E.Emit(Op.I32AtomicRmwAdd, 0, 2); return Own.None;
            case "Atomic.Exchange32": E.Emit(Op.I32AtomicRmwXchg, 0, 2); return Own.None;
            case "Atomic.CompareExchange32": E.Emit(Op.I32AtomicRmwCmpxchg, 0, 2); return Own.None;
            case "Atomic.CountAdd32":
                if (D.ThreadMode || s_atomicCounts) { E.Emit(Op.I32AtomicRmwAdd, 0, 2); return Own.None; }
                {
                    // (address, value): the old value; the sum stored
                    int cv = E.AddLocal(ValType.I32), ca = E.AddLocal(ValType.I32), co = E.AddLocal(ValType.I32);
                    E.Set(cv); E.Tee(ca); E.Get(ca); E.Load(Op.I32Load); E.Tee(co); E.Get(cv); E.Op(Op.I32Add); E.Store(Op.I32Store); E.Get(co);
                    return Own.None;
                }
            case "Atomic.CountCompareExchange32":
                if (D.ThreadMode || s_atomicCounts) { E.Emit(Op.I32AtomicRmwCmpxchg, 0, 2); return Own.None; }
                {
                    // (address, expected, replacement): the old value; the replacement stored when it was the expected one
                    int cr = E.AddLocal(ValType.I32), cx = E.AddLocal(ValType.I32), ca = E.AddLocal(ValType.I32), co = E.AddLocal(ValType.I32);
                    E.Set(cr); E.Set(cx); E.Tee(ca); E.Load(Op.I32Load); E.Tee(co); E.Get(cx); E.Op(Op.I32Eq);
                    E.If(); E.Get(ca); E.Get(cr); E.Store(Op.I32Store); E.End();
                    E.Get(co);
                    return Own.None;
                }
            case "Atomic.Load64": E.Emit(Op.I64AtomicLoad, 0, 3); return Own.None;
            case "Atomic.Store64": E.Emit(Op.I64AtomicStore, 0, 3); return Own.None;
            case "Atomic.Add64": E.Emit(Op.I64AtomicRmwAdd, 0, 3); return Own.None;
            case "Atomic.Exchange64": E.Emit(Op.I64AtomicRmwXchg, 0, 3); return Own.None;
            case "Atomic.CompareExchange64": E.Emit(Op.I64AtomicRmwCmpxchg, 0, 3); return Own.None;
            case "Atomic.Wait32": E.Emit(Op.MemoryAtomicWait32, 0, 2); return Own.None;
            case "Atomic.Notify": E.Emit(Op.MemoryAtomicNotify, 0, 2); return Own.None;
            case "Atomic.Fence": E.Emit(Op.AtomicFence); return Own.None;
            case "Rt.ThreadId": E.GGet(D.ThreadId); return Own.None;
            case "Rt.ShareCheck": E.I32(RuntimeFns.ShareCheckOn ? 1 : 0); return Own.None;
            // (a plugin's generated code: PluginContext.SuppressibleImports)
            case "Effects.get_Suppressed": E.GGet(D.SuppressGlobal()); return Own.None;
            case "Effects.set_Suppressed": E.GSet(D.SuppressGlobal()); return Own.None;
            case "Rt.Checked": E.I32(D.Options.Checked ? 1 : 0); return Own.None;
            case "Limbo.DestroyObject": E.Call(Rt.Destroy); return Own.None;
            case "Rt.NullRef": E.I32(0); return Own.None;
            case "Mem.RefI32": return Own.None;
            case "Rt.Share": E.Call(Rt.Share); return Own.None;
            case "Sharing.Visit": E.Call(Rt.ShareVisit); return Own.None;
            case "Rt.SetThreadId": E.GSet(D.ThreadId); return Own.None;
            case "Rt.TlsBase": E.GGet(D.TlsBase); return Own.None;
            case "Rt.SetTlsBase": E.GSet(D.TlsBase); return Own.None;
            case "Rt.TlsSize": E.I32Addr(new LateAddr(() => D.TlsSize)); return Own.None;
            case "Rt.RuntimeLock": E.Call(Rt.LockEnter); return Own.None;
            case "Rt.RuntimeUnlock": E.Call(Rt.LockExit); return Own.None;
            case "Rt.RuntimeLockHeld":
                if (D.ThreadMode) { E.I32Addr(D.StaticAddress("rt.lock.owner", 4, 4)); E.Emit(Op.I32AtomicLoad, 0, 2); E.GGet(D.ThreadId); E.Op(Op.I32Eq); }
                else E.I32(0);
                return Own.None;
            case "Rt.ThreadMode": E.I32(D.ThreadMode ? 1 : 0); return Own.None;
            case "Rt.DeadlockDump": E.I32(RuntimeFns.DeadlockDumpOn ? 1 : 0); return Own.None;
            // (address) -> the word of its place's lock (rt.ref_lock's table)
            case "Rt.PlaceLockWord":
                E.I32(2); E.Op(Op.I32ShrU); E.I32(63); E.Op(Op.I32And); E.I32(6); E.Op(Op.I32Shl);
                E.I32Addr(D.StaticAddress("rt.ref.locks", 64 * 64, 64)); E.Op(Op.I32Add);
                return Own.None;
            case "Rt.Rethrow":
                E.Call(Rt.Retain);
                if (D.UsesFilters) { int rx = E.AddLocal(ValType.I32); E.Tee(rx); E.Call(Rt.FirstPass); E.Get(rx); }
                E.Emit(Op.Throw, 0, 0, D.ExceptionTag); return Own.None;
            case "Rt.StackPointer": E.GGet(D.StackPointer); return Own.None;
            case "Rt.SetStackPointer": E.GSet(D.StackPointer); return Own.None;
            case "Rt.InvokeExport": throw new CompileException("InvokeExport is not an intrinsic call");
        }
        throw new CompileException("unknown intrinsic " + name);
    }

    // =====================================================================
    // object creation
    // =====================================================================
    Own EmitObjectCreation(IObjectCreationOperation oc)
    {
        var t = TypeOf(oc);
        if (t.TypeKind == TypeKind.Delegate)
        {
            // new D(method) is represented with a delegate creation child
            if (oc.Arguments.Length == 1) return EmitValue(oc.Arguments[0].Value);
            throw new CompileException("unsupported delegate construction", oc.Syntax.GetLocation());
        }
        if (Types.IsStruct(t) || Types.PrimOf(t) != Prim.None)
        {
            if (Types.PrimOf(t) != Prim.None)
            {
                // new int(): 0; a primitive's own constructor (new IntPtr(x)) runs on a frame slot
                if (oc.Constructor is not { Parameters.Length: > 0 } pc0 || D.CanonicalMethod(pc0) is not { } pctor || !Driver.HasBody(pctor.OriginalDefinition)) { EmitDefault(t); return Own.None; }
                int pslot = TempValue(t);
                E.Get(_fb); E.I32(0); E.Store(Types.StoreOp(t), pslot);
                CallWith(pctor, false, () => { E.Get(_fb); E.I32(pslot); E.Op(Op.I32Add); }, () => PushArgs(pctor, oc.Arguments.Cast<IOperation>().ToList()), null, oc.Syntax);
                E.Get(_fb); E.Load(Types.LoadOp(t), pslot);
                return Own.None;
            }
            int off = TempStruct(t);
            EmitStructCtor(oc, t, () => { E.Get(_fb); E.I32(off); E.Op(Op.I32Add); });
            E.Get(_fb); E.I32(off); E.Op(Op.I32Add);
            NoteFresh(off);
            return Own.None;
        }
        if (Types.IsString(t))
        {
            // string constructors are static factories in the library
            var ctor = oc.Constructor!;
            var lib = (INamedTypeSymbol)D.StringType;
            var factory = lib.GetMembers("Ctor").OfType<IMethodSymbol>().FirstOrDefault(f => f.Parameters.Length == ctor.Parameters.Length &&
                f.Parameters.Zip(ctor.Parameters).All(z => Types.Key(T.Canonical(z.First.Type)) == Types.Key(T.Canonical(z.Second.Type))))
                ?? throw new CompileException("string constructor not in library: " + ctor.ToDisplayString(), oc.Syntax.GetLocation());
            return CallWith(factory, false, null, () => PushArgs(factory, oc.Arguments.Cast<IOperation>().ToList()), null, oc.Syntax);
        }
        if (t is INamedTypeSymbol { IsAbstract: true }) throw new CompileException("cannot create abstract type");
        var nt = (INamedTypeSymbol)t;
        if (!D.BeforeFieldInit(nt)) EmitStaticInitGuard(nt);
        int tid = D.MarkConstructed(t);
        var layout = T.LayoutOf(t);
        int obj = E.AddLocal(ValType.I32);
        WasmFunction? frameDrop = null;
        if (_frameCreations?.Contains(oc) == true)
        {
            // an object that never escapes (EscapeAnalysis.cs): in the frame
            var (alloc, drop) = D.FrameObjectFns((INamedTypeSymbol)t);
            int off = AllocFrameBytes(Math.Max(layout.Size, Types.ObjectHeader), 8);
            E.Get(_fb); if (off != 0) { E.I32(off); E.Op(Op.I32Add); }
            E.Call(alloc);
            frameDrop = drop;
        }
        else { E.I32(Math.Max(layout.Size, Types.ObjectHeader)); E.Tid(tid); E.Call(Rt.AllocObject); }
        E.Set(obj);
        var slot = new Slot { Local = obj, Type = t, ReleaseMarker = frameDrop };
        RegisterTemp(slot); // released if the constructor throws; ownership is handed out below
        if (oc.Constructor != null)
        {
            var ctor = SubstMethod(oc.Constructor);
            CallWith(ctor, false, () => E.Get(obj), () => PushArgs(ctor, oc.Arguments.Cast<IOperation>().ToList()), null, oc.Syntax);
        }
        if (oc.Initializer != null) EmitInitializer(oc.Initializer, obj, t);
        // hand ownership to the expression result
        _temps[^1].Remove(slot);
        E.Get(obj);
        E.I32(0); E.Set(obj);
        return Own.Owned;
    }

    void EmitStructCtor(IObjectCreationOperation oc, ITypeSymbol t, Action pushDst)
    {
        if (oc.Constructor != null && !(oc.Constructor.IsImplicitlyDeclared && oc.Constructor.Parameters.Length == 0 && !HasInstanceFieldInitializers(oc.Constructor.ContainingType)))
        {
            var ctor = SubstMethod(oc.Constructor);
            CallWith(ctor, false, pushDst, () => PushArgs(ctor, oc.Arguments.Cast<IOperation>().ToList()), null, oc.Syntax);
            // (a ref struct whose constructor puts a reference into a collection's storage in it: StorageKeeps.cs)
            if (D.ExposesStorage(ctor)) KeepStorageOfNew(ctor, oc.Syntax, pushDst, oc.Arguments.Cast<IOperation>().ToList());
        }
        if (oc.Initializer != null)
        {
            int a = E.AddLocal(ValType.I32);
            pushDst(); E.Set(a);
            EmitInitializer(oc.Initializer, a, t);
        }
    }

    static bool HasInstanceFieldInitializers(INamedTypeSymbol t) =>
        t.GetMembers().Any(m => !m.IsStatic && (m is IFieldSymbol f && Driver.FieldInitializer(f) != null || m is IPropertySymbol p && Driver.PropertyInitializer(p) != null));

    void EmitInitializer(IObjectOrCollectionInitializerOperation init, int obj, ITypeSymbol t)
    {
        _implicitReceivers.Add((obj, t));
        foreach (var i in init.Initializers)
        {
            switch (i)
            {
                case IMemberInitializerOperation mi:
                    {
                        // nested: new C { P = { X = 1 } }
                        // (nothing to set or add: the member is not even read, `["k"] = { }` asks for no "k")
                        if (mi.Initializer.Initializers.Length == 0) break;
                        var mt = TypeOf(mi.InitializedMember);
                        int inner = E.AddLocal(ValType.I32);
                        if (Types.IsStruct(mt)) EmitAddress(mi.InitializedMember);
                        else { var o = EmitValue(mi.InitializedMember); ToCallSafe(o, mt); }
                        E.Set(inner);
                        EmitInitializer(mi.Initializer, inner, mt);
                        break;
                    }
                default: EmitDiscard(i); break;
            }
        }
        _implicitReceivers.RemoveAt(_implicitReceivers.Count - 1);
    }

    Own EmitTypeParamCreation(ITypeParameterObjectCreationOperation tpc)
    {
        var t = TypeOf(tpc);
        if (t.IsValueType) return EmitDefault(t);
        var nt = (INamedTypeSymbol)t;
        int tid = D.MarkConstructed(t);
        var layout = T.LayoutOf(t);
        int obj = E.AddLocal(ValType.I32);
        E.I32(Math.Max(layout.Size, Types.ObjectHeader)); E.Tid(tid); E.Call(Rt.AllocObject); E.Set(obj);
        var ctor = nt.InstanceConstructors.First(c => c.Parameters.Length == 0);
        CallWith(D.CanonicalMethod(ctor), false, () => E.Get(obj), () => { }, null, tpc.Syntax);
        if (tpc.Initializer != null) EmitInitializer(tpc.Initializer, obj, t);
        E.Get(obj);
        return Own.Owned;
    }

    Own EmitArrayCreation(IArrayCreationOperation ac)
    {
        var at = (IArrayTypeSymbol)TypeOf(ac);
        if (at.Rank != 1) return EmitMdArrayCreation(ac, at);
        var et = T.Canonical(at.ElementType);
        int tid = D.MarkConstructed(at);
        int size = T.StorageOf(et).size;
        EmitIndexLength(ac.DimensionSizes[0]);
        E.I32(size); E.Tid(tid);
        E.Call(Rt.AllocArray);
        if (ac.Initializer == null) return Own.Owned;
        int arr = E.AddLocal(ValType.I32);
        E.Set(arr);
        if (ConstantInitializerBytes(ac, out _) is { } cbytes)
        {
            // constant elements: copied from the data segment, as C# does
            var data = D.DataBlob(cbytes, Math.Max(1, size));
            E.Get(arr); E.I32(Types.ArrayData); E.Op(Op.I32Add); E.I32Addr(data); E.I32(cbytes.Length); E.Op(Op.MemoryCopy);
            E.Get(arr);
            return Own.Owned;
        }
        var slot = new Slot { Local = arr, Type = at };
        RegisterTemp(slot);
        int i = 0;
        foreach (var v in ac.Initializer.ElementValues)
        {
            int idx = i++;
            if (v is IArrayInitializerOperation) throw new CompileException("jagged initializer", v.Syntax.GetLocation());
            StoreElement(arr, idx, et, v);
        }
        _temps[^1].Remove(slot);
        E.Get(arr); E.I32(0); E.Set(arr);
        return Own.Owned;
    }

    /// <summary>new T[a, b] / new T[,] { {..}, {..} }: dimensions at 16, elements in row-major order after them.</summary>
    Own EmitMdArrayCreation(IArrayCreationOperation ac, IArrayTypeSymbol at)
    {
        var et = T.Canonical(at.ElementType);
        int tid = D.MarkConstructed(at);
        int size = T.StorageOf(et).size;
        int rank = at.Rank;
        var dims = new int[rank];
        int total = E.AddLocal(ValType.I64);
        E.I64(1); E.Set(total);
        for (int i = 0; i < rank; i++)
        {
            dims[i] = E.AddLocal(ValType.I32);
            EmitIndexLength(ac.DimensionSizes[i]);
            E.Tee(dims[i]); E.I32(0); E.Op(Op.I32LtS); E.If(); E.Call(Rt.ThrowOverflow); E.End();
            E.Get(total); E.Get(dims[i]); E.Op(Op.I64ExtendI32U); E.Op(Op.I64Mul); E.Tee(total);
            E.I64(0x7FFFFFFF); E.Op(Op.I64GtU); E.If(); E.Call(D.LibMethod("Cs2Wasm.Runtime.ThrowHelpers", "OutOfMemory").Fn); E.End();
        }
        E.Get(total); E.Op(Op.I32WrapI64); E.I32(size); E.Tid(tid); E.I32(Types.ArrayDataOffset(rank));
        E.Call(D.LibMethod("Cs2Wasm.Runtime.Rt", "AllocArrayWithHeader", 4).Fn);
        int arr = E.AddLocal(ValType.I32); E.Set(arr);
        for (int i = 0; i < rank; i++) { E.Get(arr); E.Get(dims[i]); E.Store(Op.I32Store, Types.ArrayData + 4 * i); }
        if (ac.Initializer != null)
        {
            var slot = new Slot { Local = arr, Type = at };
            RegisterTemp(slot);
            var flat = new List<IOperation>();
            void Flatten(IArrayInitializerOperation ini) { foreach (var v in ini.ElementValues) if (v is IArrayInitializerOperation inner) Flatten(inner); else flat.Add(v); }
            Flatten(ac.Initializer);
            for (int i = 0; i < flat.Count; i++) StoreElement(arr, i, et, flat[i], Types.ArrayDataOffset(rank));
            _temps[^1].Remove(slot);
        }
        E.Get(arr);
        return Own.Owned;
    }

    void StoreElement(int arr, int idx, ITypeSymbol et, IOperation v, int dataOffset = Types.ArrayData)
    {
        int size = T.StorageOf(et).size;
        if (Types.IsStruct(et))
        {
            var o = EmitValueAs(v, et);
            int src = Scratch(ValType.I32); E.Set(src);
            E.Get(arr); E.I32(dataOffset + idx * size); E.Op(Op.I32Add); E.Get(src); CopyStructInit(et);
            FreeScratch(ValType.I32, src);
            return;
        }
        if (Types.IsRef(et))
        {
            var site = D.Arenas.ArraySite(D.Main.CreateArrayTypeSymbol(et));
            site?.AddValue(StaticValueType(v), Sh != null ? FlowSrcs(v) : null);
            var o = EmitValueAs(v, et); ToOwned(o, et);
            int val = Scratch(ValType.I32); E.Set(val);
            if (site != null) { E.Get(val); E.Get(arr); E.I32(0); E.Call(site.Link); E.Set(val); }
            E.Get(arr); E.Get(val); E.Store(Op.I32Store, dataOffset + idx * size);
            // (the store is written: what the link left for then, Rt.AfterStore; the element held nothing)
            if (site != null) { E.I32(0); E.Get(arr); E.I32(0); E.Call(site.UnlinkFresh); }
            FreeScratch(ValType.I32, val);
            return;
        }
        var wt = Types.WasmType(et)!.Value;
        EmitValueAs(v, et);
        int sv = Scratch(wt); E.Set(sv);
        E.Get(arr); E.Get(sv); E.Store(Types.StoreOp(et), dataOffset + idx * size);
        FreeScratch(wt, sv);
    }

    void EmitIndexLength(IOperation op)
    {
        var p = Types.PrimOf(TypeOf(op));
        EmitValue(op);
        if (Types.Is64(p))
        {
            int t = Scratch(ValType.I64);
            E.Tee(t); E.I64(0x7FFFFFFF); E.Op(Op.I64GtU);
            E.If(); E.Call(Rt.ThrowOverflow); E.End();
            E.Get(t); E.Op(Op.I32WrapI64);
            FreeScratch(ValType.I64, t);
        }
    }

    /// <summary>A spread element: its elements added to the collection in `obj` (an ICollection&lt;T&gt;).</summary>
    void EmitSpreadInto(int obj, ITypeSymbol elemType, ISpreadOperation sp)
    {
        if (sp.ElementConversion is var ec && ec.Exists && !ec.IsIdentity && !(ec.IsImplicit && ec.IsReference) && sp.ElementType != null)
        {
            // Each element is converted on its way in (`long[] x = [.. ints]`): the spread's elements are gathered as
            // they are, in a list of their own type (whatever the operand is: any enumerable, a span), and added
            // one by one through the conversion.
            var fromT = T.Canonical(T.Resolve(sp.ElementType, Map));
            var toT = T.Canonical(elemType);
            var listF = (INamedTypeSymbol)T.Canonical(D.LibType("System.Collections.Generic.List`1").Construct(fromT));
            NewObject(listF, sp.Syntax);
            int tmp = E.AddLocal(ValType.I32); E.Set(tmp);
            RegisterTemp(new Slot { Local = tmp, Type = listF });
            EmitSpreadElements(tmp, fromT, sp);
            var count = D.CanonicalMethod(listF.GetMembers("get_Count").OfType<IMethodSymbol>().First());
            var item = D.CanonicalMethod(listF.GetMembers("get_Item").OfType<IMethodSymbol>().First());
            var add = D.CanonicalMethod(((INamedTypeSymbol)T.Canonical(D.LibType("System.Collections.Generic.ICollection`1").Construct(toT))).GetMembers("Add").OfType<IMethodSymbol>().First());
            int n = E.AddLocal(ValType.I32), i = E.AddLocal(ValType.I32);
            CallWith(count, false, () => E.Get(tmp), () => { }, null, sp.Syntax); E.Set(n);
            E.I32(0); E.Set(i);
            var done = E.Block(); var top = E.Loop();
            E.Get(i); E.Get(n); E.Op(Op.I32GeS); E.BrIf(done);
            BeginTemps();
            CallWith(add, true, () => E.Get(obj), () =>
            {
                var o = CallWith(item, false, () => E.Get(tmp), () => E.Get(i), null, sp.Syntax);
                ConvertValue(fromT, toT, false, ref o, false, ec);
                if (Types.IsStruct(toT)) CopyToTemp(toT); else if (Types.IsRef(toT)) ToCallSafe(o, toT);
            }, null, sp.Syntax);
            EndTemps();
            E.Get(i); E.I32(1); E.Op(Op.I32Add); E.Set(i);
            E.Br(top);
            E.End(); E.End();
            return;
        }
        EmitSpreadElements(obj, elemType, sp);
    }

    void EmitSpreadElements(int obj, ITypeSymbol elemType, ISpreadOperation sp)
    {
        var et = T.Canonical(sp.ElementType ?? elemType);
        var operandT = T.Resolve(sp.Operand.Type!, Map);
        string helper = operandT is INamedTypeSymbol { Arity: 1 } st && Types.FullName(st) is "System.ReadOnlySpan`1" ? "AddSpan"
            : operandT is INamedTypeSymbol { Arity: 1 } st2 && Types.FullName(st2) is "System.Span`1" ? "AddSpanMutable" : "AddRange";
        var hm = D.LibType("Cs2Wasm.Runtime.CollectionExpressions").GetMembers(helper).OfType<IMethodSymbol>().First().Construct(T.Canonical(elemType));
        hm = D.CanonicalMethod(hm);
        CallWith(hm, false, null, () => { E.Get(obj); PushArg(hm.Parameters[1], sp.Operand); }, null, sp.Syntax);
    }

    Own EmitCollectionExpression(ICollectionExpressionOperation ce)
    {
        var t = TypeOf(ce);
        // with spreads, an array, a span or a read-only interface is made as a List<T>, then copied (C#: the elements
        // in order, the spreads enumerated)
        if (ce.Elements.Any(e => e is ISpreadOperation)
            && (t is IArrayTypeSymbol { Rank: 1 } || t is INamedTypeSymbol { Arity: 1 } sx && Types.FullName(sx) is "System.Span`1" or "System.ReadOnlySpan`1"
                || t is INamedTypeSymbol { TypeKind: TypeKind.Interface, Arity: 1 } ix && Types.FullName(ix) is "System.Collections.Generic.IEnumerable`1" or "System.Collections.Generic.IReadOnlyCollection`1" or "System.Collections.Generic.IReadOnlyList`1"))
        {
            var et = T.Canonical(t is IArrayTypeSymbol a0 ? a0.ElementType : ((INamedTypeSymbol)t).TypeArguments[0]);
            var listT = (INamedTypeSymbol)T.Canonical(D.LibType("System.Collections.Generic.List`1").Construct(et));
            NewObject(listT, ce.Syntax);
            int lst = E.AddLocal(ValType.I32); E.Set(lst);
            var lslot = new Slot { Local = lst, Type = listT };
            RegisterTemp(lslot);
            var ladd = listT.GetMembers("Add").OfType<IMethodSymbol>().First(m => m.Parameters.Length == 1);
            foreach (var e in ce.Elements)
            {
                if (e is ISpreadOperation sp) EmitSpreadInto(lst, et, sp);
                else CallWith(D.CanonicalMethod(ladd), false, () => E.Get(lst), () => PushArg(ladd.Parameters[0], e), null, e.Syntax);
            }
            var toArray = listT.GetMembers("ToArray").OfType<IMethodSymbol>().First(m => m.Parameters.Length == 0);
            CallWith(D.CanonicalMethod(toArray), false, () => E.Get(lst), () => { }, null, ce.Syntax);
            int arr = E.AddLocal(ValType.I32); E.Set(arr);
            if (t is INamedTypeSymbol { TypeKind: TypeKind.Struct } spanT)
            {
                // the span over the array: the array lives as long as the scope (the span may be held there)
                AddOwned(new Slot { Local = arr, Type = D.LibCompilation.CreateArrayTypeSymbol(et) });
                var sctor = ((INamedTypeSymbol)T.Canonical(spanT)).InstanceConstructors.First(c => c.Parameters.Length == 1);
                int off = TempStruct(spanT);
                CallWith(sctor, false, () => { E.Get(_fb); E.I32(off); E.Op(Op.I32Add); }, () => E.Get(arr), null, ce.Syntax);
                E.Get(_fb); E.I32(off); E.Op(Op.I32Add);
                return Own.None;
            }
            E.Get(arr);
            return Own.Owned;
        }
        if (t is IArrayTypeSymbol { Rank: 1 } at && ce.Elements.All(e => e is not ISpreadOperation))
        {
            var et = T.Canonical(at.ElementType);
            int tid = D.MarkConstructed(at);
            E.I32(ce.Elements.Length); E.I32(T.StorageOf(et).size); E.Tid(tid); E.Call(Rt.AllocArray);
            int arr = E.AddLocal(ValType.I32); E.Set(arr);
            var slot = new Slot { Local = arr, Type = at };
            RegisterTemp(slot);
            for (int i = 0; i < ce.Elements.Length; i++) StoreElement(arr, i, et, ce.Elements[i]);
            _temps[^1].Remove(slot);
            E.Get(arr); E.I32(0); E.Set(arr);
            return Own.Owned;
        }
        if (t is INamedTypeSymbol { Arity: 1 } span && Types.FullName(span) is "System.Span`1" or "System.ReadOnlySpan`1" && ce.Elements.All(e => e is not ISpreadOperation))
        {
            // array-backed span: build the array, wrap it
            var et = T.Canonical(span.TypeArguments[0]);
            var arrT = D.LibCompilation.CreateArrayTypeSymbol(et);
            int tid = D.MarkConstructed(arrT);
            E.I32(ce.Elements.Length); E.I32(T.StorageOf(et).size); E.Tid(tid); E.Call(Rt.AllocArray);
            int arr = E.AddLocal(ValType.I32); E.Set(arr);
            var slot = new Slot { Local = arr, Type = arrT };
            RegisterTemp(slot);
            for (int i = 0; i < ce.Elements.Length; i++) StoreElement(arr, i, et, ce.Elements[i]);
            // (the array lives as long as the scope: the span may be held there)
            _temps[^1].Remove(slot);
            AddOwned(slot);
            var ctor = ((INamedTypeSymbol)T.Canonical(span)).InstanceConstructors.First(c => c.Parameters.Length == 1);
            int off = TempStruct(span);
            CallWith(ctor, false, () => { E.Get(_fb); E.I32(off); E.Op(Op.I32Add); }, () => E.Get(arr), null, ce.Syntax);
            E.Get(_fb); E.I32(off); E.Op(Op.I32Add);
            return Own.None;
        }
        if (ce.ConstructMethod is { MethodKind: MethodKind.Constructor } ctor0 && t is INamedTypeSymbol nt && !nt.IsValueType)
        {
            // List<T> and friends: new T() then Add each element
            var ctorR = SubstMethod(ctor0);
            int tid = D.MarkConstructed(t);
            var layout = T.LayoutOf(t);
            int obj = E.AddLocal(ValType.I32);
            E.I32(Math.Max(layout.Size, Types.ObjectHeader)); E.Tid(tid); E.Call(Rt.AllocObject); E.Set(obj);
            var slot = new Slot { Local = obj, Type = t };
            RegisterTemp(slot);
            CallWith(ctorR, false, () => E.Get(obj), () => { for (int i = 0; i < ctorR.Parameters.Length; i++) PushArg(ctorR.Parameters[i], null); }, null, ce.Syntax);
            var add = AddMethodOf(nt);
            if (add == null && ce.Elements.Length > 0) throw new CompileException("collection expression: no Add method on " + nt, ce.Syntax.GetLocation());
            foreach (var e in ce.Elements)
            {
                if (e is ISpreadOperation sp) { EmitSpreadInto(obj, add!.Parameters[0].Type, sp); continue; }
                var ao = CallWith(D.CanonicalMethod(add!), false, () => E.Get(obj), () => PushArg(add!.Parameters[0], e), null, e.Syntax);
                // (HashSet<T>.Add's bool: an initializer drops what Add returns)
                if (!add!.ReturnsVoid) Drop(ao, T.Resolve(add.ReturnType, Map));
            }
            _temps[^1].Remove(slot);
            E.Get(obj); E.I32(0); E.Set(obj);
            return Own.Owned;
        }
        if (t is INamedTypeSymbol { TypeKind: TypeKind.Interface, Arity: 1 } it && ce.Elements.All(e => e is not ISpreadOperation)
            && Types.FullName(it) is "System.Collections.Generic.IEnumerable`1" or "System.Collections.Generic.IReadOnlyCollection`1" or "System.Collections.Generic.IReadOnlyList`1")
        {
            // a read-only interface target: an array of the elements (an empty one: Array.Empty<T>())
            var et = T.Canonical(it.TypeArguments[0]);
            if (ce.Elements.Length == 0)
            {
                var empty = D.LibType("System.Array").GetMembers("Empty").OfType<IMethodSymbol>().First(m => m.IsGenericMethod).Construct(et);
                return CallWith(D.CanonicalMethod(empty), false, null, () => { }, null, ce.Syntax);
            }
            var arrT = D.LibCompilation.CreateArrayTypeSymbol(et);
            int tid = D.MarkConstructed(arrT);
            E.I32(ce.Elements.Length); E.I32(T.StorageOf(et).size); E.Tid(tid); E.Call(Rt.AllocArray);
            int arr = E.AddLocal(ValType.I32); E.Set(arr);
            var slot = new Slot { Local = arr, Type = arrT };
            RegisterTemp(slot);
            for (int i = 0; i < ce.Elements.Length; i++) StoreElement(arr, i, et, ce.Elements[i]);
            _temps[^1].Remove(slot);
            E.Get(arr); E.I32(0); E.Set(arr);
            return Own.Owned;
        }
        if (t is INamedTypeSymbol { TypeKind: TypeKind.Interface, Arity: 1 } lt && Types.FullName(lt) is "System.Collections.Generic.ICollection`1" or "System.Collections.Generic.IList`1")
        {
            // a mutable interface target: a List<T>
            var listT = (INamedTypeSymbol)T.Canonical(D.LibType("System.Collections.Generic.List`1").Construct(T.Canonical(lt.TypeArguments[0])));
            var list = NewObject(listT, ce.Syntax);
            int obj = E.AddLocal(ValType.I32); E.Set(obj);
            var slot = new Slot { Local = obj, Type = listT };
            RegisterTemp(slot);
            var add = listT.GetMembers("Add").OfType<IMethodSymbol>().First(m => m.Parameters.Length == 1);
            foreach (var e in ce.Elements)
            {
                if (e is ISpreadOperation sp) { EmitSpreadInto(obj, add.Parameters[0].Type, sp); continue; }
                CallWith(D.CanonicalMethod(add), false, () => E.Get(obj), () => PushArg(add.Parameters[0], e), null, e.Syntax);
            }
            _temps[^1].Remove(slot);
            E.Get(obj); E.I32(0); E.Set(obj);
            return Own.Owned;
        }
        // [CollectionBuilder(typeof(B), "Create")] (ImmutableArray<T> and friends): B.Create(ReadOnlySpan<T>) over the elements
        if (ce.ConstructMethod is { IsStatic: true, Parameters.Length: 1 } bm && T.Resolve(bm.Parameters[0].Type, Map) is INamedTypeSymbol { Arity: 1 } ros
            && Types.FullName(ros) == "System.ReadOnlySpan`1")
        {
            var et = T.Canonical(ros.TypeArguments[0]);
            var arrT = D.LibCompilation.CreateArrayTypeSymbol(et);
            EmitElementsArray(ce, et);
            int arr = E.AddLocal(ValType.I32); E.Set(arr);
            // (the builder copies what the span holds: the array goes at the end of the statement)
            RegisterTemp(new Slot { Local = arr, Type = arrT });
            var sctor = ((INamedTypeSymbol)T.Canonical(ros)).InstanceConstructors.First(c => c.Parameters.Length == 1 && c.Parameters[0].Type is IArrayTypeSymbol);
            int off = TempStruct(ros);
            CallWith(sctor, false, () => { E.Get(_fb); E.I32(off); E.Op(Op.I32Add); }, () => E.Get(arr), null, ce.Syntax);
            var builder = SubstMethod(bm);
            return CallWith(builder, false, null, () => { E.Get(_fb); E.I32(off); E.Op(Op.I32Add); }, null, ce.Syntax);
        }
        throw new CompileException("this collection expression is not supported yet", ce.Syntax.GetLocation());
    }

    /// <summary>A collection type's Add(T) (its own or a base type's), as a collection initializer calls it.</summary>
    static IMethodSymbol? AddMethodOf(INamedTypeSymbol t)
    {
        for (var b = t; b != null; b = b.BaseType)
            if (b.GetMembers("Add").OfType<IMethodSymbol>().FirstOrDefault(m => m.Parameters.Length == 1 && !m.IsStatic) is { } add) return add;
        return null;
    }

    /// <summary>The elements of a collection expression as a new T[] (owned, on the stack); spreads through a List&lt;T&gt;.</summary>
    void EmitElementsArray(ICollectionExpressionOperation ce, ITypeSymbol et)
    {
        if (ce.Elements.All(e => e is not ISpreadOperation))
        {
            var at = D.LibCompilation.CreateArrayTypeSymbol(et);
            int tid = D.MarkConstructed(at);
            E.I32(ce.Elements.Length); E.I32(T.StorageOf(et).size); E.Tid(tid); E.Call(Rt.AllocArray);
            int arr = E.AddLocal(ValType.I32); E.Set(arr);
            var slot = new Slot { Local = arr, Type = at };
            RegisterTemp(slot);
            for (int i = 0; i < ce.Elements.Length; i++) StoreElement(arr, i, et, ce.Elements[i]);
            _temps[^1].Remove(slot);
            E.Get(arr); E.I32(0); E.Set(arr);
            return;
        }
        var listT = (INamedTypeSymbol)T.Canonical(D.LibType("System.Collections.Generic.List`1").Construct(et));
        NewObject(listT, ce.Syntax);
        int lst = E.AddLocal(ValType.I32); E.Set(lst);
        RegisterTemp(new Slot { Local = lst, Type = listT });
        var ladd = listT.GetMembers("Add").OfType<IMethodSymbol>().First(m => m.Parameters.Length == 1);
        foreach (var e in ce.Elements)
        {
            if (e is ISpreadOperation sp) EmitSpreadInto(lst, et, sp);
            else CallWith(D.CanonicalMethod(ladd), false, () => E.Get(lst), () => PushArg(ladd.Parameters[0], e), null, e.Syntax);
        }
        var toArray = listT.GetMembers("ToArray").OfType<IMethodSymbol>().First(m => m.Parameters.Length == 0);
        CallWith(D.CanonicalMethod(toArray), false, () => E.Get(lst), () => { }, null, ce.Syntax);
    }

    // =====================================================================
    // delegates
    // =====================================================================
    const int DelegateTarget = 12, DelegateFn = 16;
    internal const int DelegateTargetOffset = DelegateTarget, DelegateFnOffset = DelegateFn;

    Own EmitDelegateCreation(IDelegateCreationOperation dc)
    {
        var dt = TypeOf(dc);
        // a lambda converted to Expression<T>: the tree, not a delegate (ExpressionTrees.cs)
        if (IsExpressionTree(dt) && dc.Target is IAnonymousFunctionOperation eaf) return EmitExpressionLambda(eaf, (INamedTypeSymbol)dt);
        return dc.Target switch
        {
            IAnonymousFunctionOperation af => EmitLambdaDelegate(af, dt),
            IFlowAnonymousFunctionOperation fa => EmitLambdaDelegate((IAnonymousFunctionOperation)D.ModelFor(fa.Syntax.SyntaxTree).GetOperation(fa.Syntax)!, dt),
            IMethodReferenceOperation mr => EmitMethodGroupDelegate(mr, dt),
            _ => throw new CompileException("unsupported delegate creation", dc.Syntax.GetLocation()),
        };
    }

    /// <summary>Allocates a delegate; stack: target (owned i32) → delegate (owned).</summary>
    void NewDelegate(ITypeSymbol dt, WasmFunction fn, ITypeSymbol? targetType = null, Display? env = null)
    {
        D.Arenas.DelegateTarget(dt, targetType, env);
        int target = Scratch(ValType.I32); E.Set(target);
        int tid = D.MarkConstructed(dt);
        var layout = T.LayoutOf(dt);
        E.I32(Math.Max(layout.Size, 24)); E.Tid(tid); E.Call(Rt.AllocObject);
        int obj = Scratch(ValType.I32); E.Tee(obj);
        E.Get(target); E.Store(Op.I32Store, DelegateTarget);
        E.Get(obj); E.I32Fn(D.Module, fn); E.Store(Op.I32Store, DelegateFn);
        D.NoteIndirectTarget(Driver.DelegateBucket(dt), fn);
        D.NoteDelegateTarget(T.Canonical(dt), fn);
        E.Get(obj);
        FreeScratch(ValType.I32, obj); FreeScratch(ValType.I32, target);
    }

    Own EmitMethodGroupDelegate(IMethodReferenceOperation mr, ITypeSymbol dt)
    {
        var m = SubstMethod(mr.Method);
        if (m.MethodKind == MethodKind.LocalFunction)
        {
            var lf = D.LocalFunction(this, m, Map, LocalFunctionEnv(m));
            PushDisplayPtr(LocalFunctionEnv(m), retain: true);
            NewDelegate(dt, lf.Fn, env: LocalFunctionEnv(m));
            return Own.Owned;
        }
        if (m.IsStatic && !(m.IsExtensionMethod && mr.Instance != null))
        {
            E.I32(0);
            var sfn = D.StaticThunk(m);
            D.DelegateFnMethods.TryAdd(sfn, m);
            NewDelegate(dt, sfn);
            return Own.Owned;
        }
        var inst = mr.Instance ?? throw new CompileException("method group without instance");
        var it = TypeOf(inst);
        if (it.IsValueType)
        {
            var o = EmitValue(inst);
            EmitBox(it, o);
            var impl = Types.Key(T.Canonical(m.ContainingType)) == Types.Key(T.Canonical(it)) ? m : D.Implementation(it, m) ?? m;
            var bfn = Types.Key(T.Canonical(impl.ContainingType)) == Types.Key(T.Canonical(it)) ? D.BoxThunk(impl, it) : D.Stub(m);
            D.DelegateFnMethods.TryAdd(bfn, impl);
            NewDelegate(dt, bfn, targetType: it);
            return Own.Owned;
        }
        var ov = EmitValue(inst);
        ToOwned(ov);
        if (!IsKnownNonNull(inst)) NullCheckKeep();
        WasmFunction fn = mr.IsVirtual && IsVirtualDispatch(m) ? D.Stub(m) : D.Method(m).Fn;
        D.DelegateFnMethods.TryAdd(fn, m);
        NewDelegate(dt, fn, targetType: it);
        return Own.Owned;
    }

    Own EmitLambdaDelegate(IAnonymousFunctionOperation af, ITypeSymbol dt)
    {
        var env = _machine != null ? MachineEnvAt(af.Syntax) : CurrentEnv();
        var fn = D.Lambda(this, af, Map, env);
        Sh?.LambdaAs(af.Symbol, dt);
        PushDisplayPtr(env, retain: true);
        NewDelegate(dt, fn.Fn, env: env);
        return Own.Owned;
    }

    Own EmitLocalFunctionCall(IInvocationOperation inv)
    {
        var m = inv.TargetMethod;
        var map = Map;
        if (m.IsGenericMethod)
        {
            var d = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(Map.Map, SymbolEqualityComparer.Default);
            for (int i = 0; i < m.TypeArguments.Length; i++) d[m.OriginalDefinition.TypeParameters[i]] = T.Resolve(m.TypeArguments[i], Map);
            map = new TypeMap(d);
        }
        var env = LocalFunctionEnv(m);
        var lf = D.LocalFunction(this, m, map, env);
        bool sret = Types.IsStruct(T.Resolve(m.ReturnType, map));
        int sretOff = -1;
        if (sret) { sretOff = TempStruct(T.Resolve(m.ReturnType, map)); E.Get(_fb); E.I32(sretOff); E.Op(Op.I32Add); }
        PushDisplayPtr(env, retain: false);
        var byParam = new IOperation?[m.Parameters.Length];
        foreach (var a in inv.Arguments) byParam[a.Parameter!.Ordinal] = a.Value;
        for (int i = 0; i < m.Parameters.Length; i++)
        {
            var p = m.Parameters[i];
            var pt = T.Resolve(p.Type, map);
            PushArgTyped(p, pt, byParam[i]);
        }
        E.Call(lf.Fn);
        var rt = T.Resolve(m.ReturnType, map);
        if (sret) { E.Get(_fb); E.I32(sretOff); E.Op(Op.I32Add); NoteFresh(sretOff); return Own.None; }
        if (Types.IsVoid(rt)) return Own.None;
        return Types.IsRef(rt) ? Own.Owned : Own.None;
    }

    void PushArgTyped(IParameterSymbol p, ITypeSymbol pt, IOperation? value)
    {
        if (value == null) { if (p.HasExplicitDefaultValue) EmitConstant(p.ExplicitDefaultValue, pt); else EmitDefault(pt); return; }
        if (p.RefKind != RefKind.None) { if (p.RefKind == RefKind.In && !IsAddressable(value)) EmitAddressOfCopy(value, pt); else EmitAddress(value); return; }
        if (Types.IsStruct(pt))
        {
            if (IsFreshStruct(value)) { EmitValueAs(value, pt); return; }
            if (TryPushBorrowedStruct(p, value, pt)) return;
            int off = TempStruct(pt);
            EmitValueAs(value, pt);
            int src = Scratch(ValType.I32); E.Set(src);
            E.Get(_fb); E.I32(off); E.Op(Op.I32Add); E.Get(src); CopyStructInit(pt);
            FreeScratch(ValType.I32, src);
            E.Get(_fb); E.I32(off); E.Op(Op.I32Add);
            return;
        }
        var o = EmitValueAs(value, pt);
        if (Types.IsRef(pt)) ToCallSafe(o, pt);
    }

    /// <summary>Body of a delegate type's Invoke: call the target(s) through the function table.</summary>
    void EmitDelegateInvokeBody()
    {
        var sig = Driver.Signature(M, T);
        int nParams = M.Parameters.Length;
        int firstParam = (StructRet ? 1 : 0) + 1;
        int list = E.AddLocal(ValType.I32);
        E.Get(_thisLocal); E.Load(Op.I32Load, 20); E.Tee(list);
        E.Op(Op.I32Eqz);
        E.If();
        {
            if (StructRet) E.Get(_structRetLocal);
            E.Get(_thisLocal); E.Load(Op.I32Load, DelegateTarget);
            for (int i = 0; i < nParams; i++) E.Get(firstParam + i);
            E.Get(_thisLocal); E.Load(Op.I32Load, DelegateFn);
            E.Emit(Op.CallIndirect, 0, D.DelegateCallBucket(T.Canonical(M.ContainingType)), sig);
            E.Op(Op.Return);
        }
        E.End();
        // multicast: invoke each in order, return the last result
        var rt = T.Canonical(M.ReturnType);
        var rwt = StructRet ? (ValType?)null : Types.WasmType(rt);
        int res = rwt != null ? E.AddLocal(rwt.Value) : -1;
        int i0 = E.AddLocal(ValType.I32), d = E.AddLocal(ValType.I32);
        var brk = E.Block();
        var top = E.Loop();
        E.Get(i0); E.Get(list); E.Load(Op.I32Load, 12); E.Op(Op.I32GeU); E.BrIf(brk);
        E.Get(list); E.Get(i0); E.I32(4); E.Op(Op.I32Mul); E.Op(Op.I32Add); E.Load(Op.I32Load, Types.ArrayData); E.Set(d);
        if (res >= 0 && Types.IsRef(rt)) { E.Get(res); E.Call(Rt.Release); }
        if (StructRet) E.Get(_structRetLocal);
        E.Get(d); E.Load(Op.I32Load, DelegateTarget);
        for (int i = 0; i < nParams; i++) E.Get(firstParam + i);
        E.Get(d); E.Load(Op.I32Load, DelegateFn);
        E.Emit(Op.CallIndirect, 0, D.DelegateCallBucket(T.Canonical(M.ContainingType)), sig);
        if (res >= 0) E.Set(res);
        E.Get(i0); E.I32(1); E.Op(Op.I32Add); E.Set(i0);
        E.Br(top);
        E.End(); E.End();
        if (res >= 0) { _retLocal = res; EmitReturnSequence(true); }
        else EmitReturnSequence(false);
    }

}
