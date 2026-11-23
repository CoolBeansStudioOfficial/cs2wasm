using Il2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Il2Wasm.Compiler;

/// <summary>
/// A constant argument of a call: null, the default value of a struct (an empty span), or a constant (bool, integer,
/// char, string).
/// </summary>
internal readonly record struct ConstArg(char Kind, object? Value)
{
    public const char Null = 'n', Default = 'd', Value_ = 'v', TypeOf = 't';
    public override string ToString() => Kind == TypeOf ? "t=" + Value : Kind + (Kind == Value_ ? "=" + (Value is string s ? "\"" + s + "\"" : Convert.ToString(Value, System.Globalization.CultureInfo.InvariantCulture)) : "");
}

/// <summary>
/// Constant-argument specialization (the compiler's stand-in for the JIT's inlining). Upstream writes the cheap case
/// of a method beside the general one and relies on the JIT inlining it into callers that pass a constant (a null or
/// empty format: `if (format.Length == 0) return fast(); return Slow(format);`), so the general case costs nothing at
/// those calls. Here a call whose arguments are constants gets its own copy of the library method in which those
/// parameters are the constants: the branches they decide fold (Folding.cs), and what only the other side reached is
/// never compiled. Constants flow on through the copy's calls, and through virtual calls (the dispatch stub for the
/// constants calls each implementation's copy). Only parameters the method tests or passes on are specialized, the
/// method must not assign them, string constants only go into short methods (forwarders such as SR.Format), and a
/// method gets at most a few copies.
/// </summary>
internal sealed partial class Driver
{
    const int MaxCopies = 8, ShortBody = 40;
    readonly Dictionary<string, int> _copies = [];
    readonly Dictionary<string, (HashSet<int> useful, HashSet<int> format, HashSet<int> compared, int size)> _paramUse = [];

    static string ConstKey(IReadOnlyDictionary<int, ConstArg> c) => string.Join(",", c.OrderBy(p => p.Key).Select(p => p.Key + ":" + p.Value));

    /// <summary>The method for a call with these constant arguments: its specialized copy, or the method itself.</summary>
    public MethodInst Method(IMethodSymbol m, IReadOnlyDictionary<int, ConstArg>? consts)
    {
        if (consts == null || consts.Count == 0) return Method(m);
        var target = MethodTarget(m);
        var baseKey = Types.MethodKey(target);
        // (the general method is not made unless a call needs it)
        if (_methods.TryGetValue(baseKey, out var existing) ? existing.Impl != MethodImpl.Body || existing.Compile != null : Classify(target) != MethodImpl.Body)
            return Method(m);
        if (!IsLibrary(target)) return Method(m);
        var kept = Useful(target, consts);
        if (kept == null) return Method(m);
        var mi = new MethodInst { Sym = target, Fn = null!, Key = baseKey, Impl = MethodImpl.Body };
        var key = mi.Key + "|" + ConstKey(kept);
        if (_methods.TryGetValue(key, out var si)) return si;
        _copies.TryGetValue(mi.Key, out int n);
        // a short forwarder's copy is about the size of the call it replaces (SR.Format with each resource string)
        var pu = _paramUse[Types.MethodKey(mi.Sym.OriginalDefinition)];
        if (n >= (pu.size <= ShortBody ? 1024 : IsInlineHint(mi.Sym.OriginalDefinition) ? MaxCopies : 2)) return Method(m);
        _copies[mi.Key] = n + 1;
        var fn = Module.AddFunction(Display(mi.Sym) + " [" + ConstKey(kept) + "]", Signature(mi.Sym, Types));
        fn.FrameName = IsHiddenFrame(mi.Sym) ? null : FrameName(mi.Sym);
        si = new MethodInst { Sym = mi.Sym, Fn = fn, Key = key, Impl = MethodImpl.Body, ReachedFrom = Compiling, Consts = kept };
        _methods[key] = si;
        _work.Enqueue(si);
        return si;
    }

    /// <summary>The dispatch stub for a virtual call with these constant arguments.</summary>
    public WasmFunction Stub(IMethodSymbol slot, IReadOnlyDictionary<int, ConstArg>? consts)
    {
        if (consts == null || consts.Count == 0) return Stub(slot);
        slot = CanonicalMethod(slot);
        var key = Types.MethodKey(slot) + "|" + ConstKey(consts);
        if (Stubs.TryGetValue(key, out var s)) return s.Fn;
        var fn = Module.AddFunction("virtual " + Display(slot) + " [" + ConstKey(consts) + "]", Signature(slot, Types));
        Stubs[key] = new VirtualStub { Slot = slot, Fn = fn, IsInterface = slot.ContainingType.TypeKind == TypeKind.Interface, Consts = consts };
        return fn;
    }

    /// <summary>The constants worth a copy of m: for parameters its body tests or passes on and never assigns.</summary>
    IReadOnlyDictionary<int, ConstArg>? Useful(IMethodSymbol m, IReadOnlyDictionary<int, ConstArg> consts)
    {
        var def = m.OriginalDefinition;
        if (def.MethodKind is not (MethodKind.Ordinary or MethodKind.PropertyGet or MethodKind.ExplicitInterfaceImplementation) || def.IsAsync) return null;
        var k = Types.MethodKey(def);
        if (!_paramUse.TryGetValue(k, out var use)) _paramUse[k] = use = ParamUse(def);
        // upstream's AggressiveInlining marks the methods it means the JIT to fold into callers; a short forwarder
        // (SR.Format) passes its constants on
        bool inline = IsInlineHint(def);
        bool shortBody = use.size <= ShortBody;
        // a bool that decides a branch of a longer method (getFullName(false, false)): at most two copies
        bool flags = consts.Any(c => c.Value is { Kind: ConstArg.Value_, Value: bool } && use.compared.Contains(c.Key));
        if (!inline && !shortBody && !flags) return null;
        Dictionary<int, ConstArg>? kept = null;
        foreach (var (i, c) in consts)
        {
            if (!use.useful.Contains(i)) continue;
            // a string decides something only as a format string (FormatLowering)
            if (c.Kind == ConstArg.Value_ && c.Value is string && (!shortBody || !use.format.Contains(i))) continue;
            // a type decides something where it is compared (GetFormat(typeof(NumberFormatInfo)))
            if (c.Kind == ConstArg.TypeOf && !use.compared.Contains(i)) continue;
            if (!inline && !shortBody && (c.Value is not bool || !use.compared.Contains(i))) continue;
            (kept ??= [])[i] = c;
        }
        return kept;
    }

    /// <summary>MethodImplOptions.NoInlining (8).</summary>
    static bool IsNoInline(IMethodSymbol def) => def.GetAttributes().Any(a => a.AttributeClass?.Name == "MethodImplAttribute" && a.ConstructorArguments.Length > 0
        && a.ConstructorArguments[0].Value is { } v && (Convert.ToInt32(v) & 8) != 0);

    static bool IsInlineHint(IMethodSymbol def) => def.GetAttributes().Any(a => a.AttributeClass?.Name == "MethodImplAttribute" && a.ConstructorArguments.Length > 0
        && a.ConstructorArguments[0].Value is int flags && (flags & 0x100) != 0);

    (HashSet<int> useful, HashSet<int> format, HashSet<int> compared, int size) ParamUse(IMethodSymbol def)
    {
        var compared = new HashSet<int>();
        var useful = new HashSet<int>();
        var format = new HashSet<int>();
        var written = new HashSet<int>();
        int size = 0;
        foreach (var r in def.DeclaringSyntaxReferences)
        {
            if (ModelFor(r.SyntaxTree).GetOperation(r.GetSyntax()) is not { } body) continue;
            // (the body's own size: local functions are compiled on their own)
            var inLocal = body.Descendants().OfType<ILocalFunctionOperation>().SelectMany(l => l.DescendantsAndSelf()).ToHashSet();
            foreach (var op in body.DescendantsAndSelf())
            {
                if (inLocal.Contains(op)) continue;
                size++;
                if (op.Kind is OperationKind.YieldReturn or OperationKind.YieldBreak) return ([], [], [], 0);
                if (op is not IParameterReferenceOperation p || !SymbolEqualityComparer.Default.Equals(p.Parameter.ContainingSymbol.OriginalDefinition, def)) continue;
                int i = p.Parameter.Ordinal;
                var parent = p.Parent;
                while (parent is IConversionOperation or IParenthesizedOperation) parent = parent.Parent;
                switch (parent)
                {
                    case ISimpleAssignmentOperation a when a.Target == p:
                    case ICompoundAssignmentOperation ca when ca.Target == p:
                    case IIncrementOrDecrementOperation:
                    case IArgumentOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out }:
                    case IAddressOfOperation:
                        written.Add(i); break;
                    case IBinaryOperation or IConditionalOperation or IUnaryOperation:
                        useful.Add(i); compared.Add(i); break;
                    case IIsPatternOperation
                        or ICoalesceOperation or ISwitchOperation or ISwitchExpressionOperation:
                    case IPropertyReferenceOperation { Property.Name: "Length" or "IsEmpty" }:
                    case IArgumentOperation:
                        useful.Add(i); break;
                }
                if (p.Parent is IArgumentOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out }) written.Add(i);
                if (p.Parent is IArgumentOperation { Parameter.Name: "format" } fa && fa.Parent is IInvocationOperation { TargetMethod: { Name: "Format" } fm }
                    && fm.ContainingType.SpecialType == SpecialType.System_String) format.Add(i);
            }
        }
        useful.ExceptWith(written);
        format.ExceptWith(written);
        compared.ExceptWith(written);
        return (useful, format, compared, size);
    }
}

sealed partial class FunctionCompiler
{
    /// <summary>The constant arguments of a call (by parameter), when the callee is a library method.</summary>
    IReadOnlyDictionary<int, ConstArg>? ConstArgsFor(IMethodSymbol m, IReadOnlyList<IOperation> args)
    {
        if (!D.IsLibrary(m)) return null;
        Dictionary<int, ConstArg>? r = null;
        var given = new bool[m.Parameters.Length];
        int k = 0;
        foreach (var a in args)
        {
            int i = a is IArgumentOperation { Parameter: { } ap } ? ap.Ordinal : k;
            k++;
            if (i >= m.Parameters.Length) continue;
            given[i] = true;
            var value = a is IArgumentOperation ao ? ao.Value : a;
            if (m.Parameters[i].RefKind != RefKind.None) continue;
            if (a is IArgumentOperation { InConversion.IsUserDefined: true }) continue;
            if (ConstOf(value, T.Canonical(m.Parameters[i].Type)) is { } c) (r ??= [])[i] = c;
        }
        for (int i = 0; i < m.Parameters.Length; i++)
        {
            var p = m.Parameters[i];
            if (given[i] || !p.HasExplicitDefaultValue || p.RefKind != RefKind.None) continue;
            var pt = T.Canonical(p.Type);
            ConstArg? c = p.ExplicitDefaultValue switch
            {
                null => pt.IsValueType ? (Types.IsStruct(pt) ? new ConstArg(ConstArg.Default, null) : null) : new ConstArg(ConstArg.Null, null),
                string => new ConstArg(ConstArg.Value_, p.ExplicitDefaultValue),
                _ => null,
            };
            if (c is { } cc) (r ??= [])[i] = cc;
        }
        return r;
    }

    ConstArg? ConstOf(IOperation value, ITypeSymbol paramType)
    {
        // string -> ReadOnlySpan<char> of null or "": the empty span
        if (value is IConversionOperation { OperatorMethod: { Name: "op_Implicit" } } sc && Types.IsStruct(paramType)
            && ConstOf(sc.Operand, sc.Operand.Type ?? paramType) is { } inner
            && (inner.Kind == ConstArg.Null || inner.Value is "")) return new ConstArg(ConstArg.Default, null);
        while (value is IConversionOperation { OperatorMethod: null } conv && (conv.Conversion.IsIdentity || conv.Conversion.IsReference || conv.Conversion.IsImplicit && conv.Operand.ConstantValue.HasValue))
            value = conv.Operand;
        if (value.ConstantValue is { HasValue: true } cv)
        {
            if (cv.Value == null) return paramType.IsValueType ? null : new ConstArg(ConstArg.Null, null);
            // (integers, enum values among them, would make a copy per value: only the constants that choose a path)
            if (cv.Value is string or bool) return new ConstArg(ConstArg.Value_, cv.Value);
            return null;
        }
        if (value is ITypeOfOperation to && TypeOfKey(to) is { } tk) return new ConstArg(ConstArg.TypeOf, tk);
        if (value is IDefaultValueOperation && Types.IsStruct(paramType)) return new ConstArg(ConstArg.Default, null);
        if (value is IDefaultValueOperation && !paramType.IsValueType) return new ConstArg(ConstArg.Null, null);
        if (value is IParameterReferenceOperation pr && Bound(pr) is { } b) return b;
        return null;
    }

    /// <summary>The constant a parameter of this specialized copy stands for.</summary>
    ConstArg? Bound(IOperation op)
    {
        while (op is IConversionOperation { Conversion.IsIdentity: true } or IParenthesizedOperation)
            op = op is IConversionOperation c ? c.Operand : ((IParenthesizedOperation)op).Operand;
        if (op is not IParameterReferenceOperation p || Inst?.Consts is not { } consts || _closure != null) return null;
        if (!SymbolEqualityComparer.Default.Equals(p.Parameter.ContainingSymbol.OriginalDefinition, M.OriginalDefinition)) return null;
        return consts.TryGetValue(p.Parameter.Ordinal, out var v) ? v : null;
    }

    /// <summary>The type a typeof names, when it is closed (no type parameters left).</summary>
    string? TypeOfKey(ITypeOfOperation to)
    {
        var t = T.Canonical(T.Resolve(to.TypeOperand, Map));
        static bool Open(ITypeSymbol x) => x switch
        {
            ITypeParameterSymbol => true,
            IArrayTypeSymbol a => Open(a.ElementType),
            IPointerTypeSymbol p => Open(p.PointedAtType),
            INamedTypeSymbol n => n.TypeArguments.Any(Open) || n.ContainingType is { } ct && Open(ct),
            _ => false,
        };
        return Open(t) ? null : Types.Key(t);
    }

    /// <summary>Folding of the conditions a bound parameter decides (Fold).</summary>
    bool? FoldBound(IOperation op)
    {
        switch (op)
        {
            // Type == / != : a bound typeof against another typeof, or null
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals, OperatorMethod: { } om } tb
                when om.ContainingType?.ToDisplayString() == "System.Type":
                {
                    bool eq = tb.OperatorKind == BinaryOperatorKind.Equals;
                    var (l, r) = (tb.LeftOperand, tb.RightOperand);
                    while (l is IConversionOperation lc) l = lc.Operand;
                    while (r is IConversionOperation rc) r = rc.Operand;
                    ConstArg? bl = Bound(l), br = Bound(r);
                    if (bl is not { Kind: ConstArg.TypeOf } && br is not { Kind: ConstArg.TypeOf }) return null;
                    var (bnd, other) = bl is { Kind: ConstArg.TypeOf } ? (bl.Value, r) : (br!.Value, l);
                    if (IsNullLiteral(other)) return !eq;
                    string? ok = other is ITypeOfOperation oto ? TypeOfKey(oto) : Bound(other) is { Kind: ConstArg.TypeOf } ob ? (string)ob.Value! : null;
                    if (ok == null) return null;
                    return (ok == (string)bnd.Value!) == eq;
                }
            case IParameterReferenceOperation when Bound(op) is { Kind: ConstArg.Value_, Value: bool b }: return b;
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals, OperatorMethod: null } bin:
                {
                    var (side, other) = IsNullLiteral(bin.RightOperand) ? (bin.LeftOperand, true) : IsNullLiteral(bin.LeftOperand) ? (bin.RightOperand, true) : (null, false);
                    if (!other || Bound(side!) is not { } c) return null;
                    bool isNull = c.Kind == ConstArg.Null;
                    if (c.Kind == ConstArg.Default) return null;
                    return bin.OperatorKind == BinaryOperatorKind.Equals ? isNull : !isNull;
                }
            case IIsPatternOperation { Value: var v, Pattern: var pat } when Bound(v) is { Kind: ConstArg.Null or ConstArg.Value_ } c:
                {
                    bool isNull = c.Kind == ConstArg.Null;
                    if (pat is IConstantPatternOperation { Value: var cpv } && IsNullLiteral(cpv)) return isNull;
                    if (pat is INegatedPatternOperation { Pattern: IConstantPatternOperation { Value: var npv } } && IsNullLiteral(npv)) return !isNull;
                    return null;
                }
            case IInvocationOperation { Instance: null, TargetMethod: { Name: "IsNullOrEmpty" } im, Arguments: [var arg] }
                when im.ContainingType.SpecialType == SpecialType.System_String && Bound(arg.Value) is { } c:
                return c.Kind == ConstArg.Null || c.Value is "" ? true : c.Value is string ? false : null;
            case IPropertyReferenceOperation { Property.Name: "IsEmpty", Instance: { } inst } when Bound(inst) is { Kind: ConstArg.Default }:
                return true;
        }
        return null;
    }

    long? FoldIntBound(IOperation op) => op switch
    {
        IParameterReferenceOperation when Bound(op) is { Kind: ConstArg.Value_, Value: int or char } c => Convert.ToInt64(c.Value),
        IPropertyReferenceOperation { Property.Name: "Length", Instance: { } inst } when Bound(inst) is { } c =>
            c.Kind == ConstArg.Default ? 0 : c.Value is string s ? s.Length : null,
        _ => null,
    };

    static bool IsNullLiteral(IOperation op)
    {
        while (op is IConversionOperation c) op = c.Operand;
        return op.ConstantValue is { HasValue: true, Value: null };
    }
}
