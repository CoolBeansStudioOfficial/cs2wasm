using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

// An `out` value that is borrowed (section 4.1: a value read out of a place that keeps it needs no count while
// nothing can change that place). `if (map.TryGetValue(key, out var v)) sum += v.Life;` counted the value into the
// caller's slot in TryGetValue and counted it off at the end of the `if`, on every hit, and on every call read,
// released and cleared the slot: 0.46 us of the collections scene's 300 lookups a frame (tools/colstr-parts.cs).
// Decided from the text alone, both sides:
//   - the callee (OutBorrowable): a method of a class, not virtual, that only ever assigns the parameter, each time
//     either `default`, or a value it reads at that moment out of the object (a field or an element from `this` on,
//     or through a `ref` local a call on `this` gave: Dictionary's `value = valRef` after `ref FindValue(key)`) with
//     `return` of a constant as the next statement, and that has no local that holds a reference (whose release at
//     the return could be what frees the place read). A copy of it (OutBorrowVariant) stores the value as it is: no
//     count, the slot's old value not released.
//   - the caller (OutBorrowCall): a direct call, the argument `out var v`, the call the condition of an `if` (or the
//     left of its `&&`s), every use of v in the rest of the condition and the `if`'s first branch, and nothing there
//     that could store a reference or run other code: reads of locals, fields and elements, arithmetic, stores of
//     numbers, `return`. No call, no allocation, no property, no static's first read. Then nothing between the
//     callee's read and v's last use changes any place, and the object v names is kept by where it was read from.
//     v's slot is no owner: not released at the scope's end nor when an exception unwinds.
// A program with threads counts as before (another thread could store the place). CS2WASM_NO_OUT_BORROW=1.
internal sealed partial class Driver
{
    static readonly bool s_noOutBorrow = Environment.GetEnvironmentVariable("CS2WASM_NO_OUT_BORROW") == "1";
    /// <summary>CS2WASM_OUT_BORROW_BREAK=1 (to see test 221 fail): calls count as nothing in the caller's branch.</summary>
    internal static readonly bool OutBorrowBreak = Environment.GetEnvironmentVariable("CS2WASM_OUT_BORROW_BREAK") == "1";
    internal static readonly bool OutBorrowDebug = Environment.GetEnvironmentVariable("CS2WASM_OUT_BORROW_DEBUG") == "1";
    readonly Dictionary<string, bool> _outBorrowable = [];

    internal bool OutBorrowOn => !s_noOutBorrow && !ThreadMode && !CompilingInstrumented;

    /// <summary>Whether the method's `out` parameter is only ever given `default` or a value read, just before a
    /// return, out of a place of the object's (see above).</summary>
    internal bool OutBorrowable(IMethodSymbol m, int ordinal)
    {
        m = m.OriginalDefinition;
        if (!OutBorrowOn || ordinal >= m.Parameters.Length) return false;
        var key = Types.MethodKey(m) + "#" + ordinal;
        if (_outBorrowable.TryGetValue(key, out var known)) return known;
        _outBorrowable[key] = false;
        return _outBorrowable[key] = Compute();

        bool Compute()
        {
            var p = m.Parameters[ordinal];
            if (p.RefKind != RefKind.Out || m.IsStatic || m.IsVirtual || m.IsAbstract || m.IsOverride || m.IsAsync || m.IsIterator || m.IsExtern || m.MethodKind != MethodKind.Ordinary) return false;
            if (m.ContainingType is not { TypeKind: TypeKind.Class } || m.DeclaringSyntaxReferences.Length != 1 || HooksSeeAny([m])) return false;
            var r = m.DeclaringSyntaxReferences[0];
            if (ModelFor(r.SyntaxTree).GetOperation(r.GetSyntax()) is not { } body) return false;
            bool any = false;
            foreach (var op in body.Descendants())
            {
                switch (op)
                {
                    case IAnonymousFunctionOperation or ILocalFunctionOperation or ITryOperation or ILabeledOperation or IDeclarationExpressionOperation or IDeclarationPatternOperation or IUsingOperation or ILockOperation or IForEachLoopOperation:
                        return false;
                    case IBranchOperation { BranchKind: BranchKind.GoTo }: return false;
                    // (a local that holds a reference: its release at the return could free what was read)
                    case IVariableDeclaratorOperation vd when vd.Symbol.RefKind == RefKind.None && !PlainValueType(vd.Symbol.Type): return false;
                }
                if (op is not IParameterReferenceOperation pr || !SymbolEqualityComparer.Default.Equals(pr.Parameter, p)) continue;
                if (pr.Parent is not ISimpleAssignmentOperation { IsRef: false, Parent: IExpressionStatementOperation st } sa || sa.Target != pr) return false;
                any = true;
                var v = sa.Value;
                var dv = v;
                while (dv is IConversionOperation { OperatorMethod: null } dc) dv = dc.Operand;
                if (v.ConstantValue is { HasValue: true, Value: null } || dv is IDefaultValueOperation || dv.ConstantValue is { HasValue: true, Value: null }) continue;
                v = ChainFlow.Strip(v);
                bool read = v switch
                {
                    ILocalReferenceOperation { Local: { RefKind: not RefKind.None } rl } => RefFromThis(rl, body),
                    IFieldReferenceOperation or IArrayElementReferenceOperation => FromThis(v) && v is not IInstanceReferenceOperation,
                    _ => false,
                };
                if (!read) return false;
                // (the next statement: a return of a constant; nothing runs between the read and the return)
                if (st.Parent is not IBlockOperation blk) return false;
                int at = blk.Operations.IndexOf(st);
                if (at < 0 || at + 1 >= blk.Operations.Length || blk.Operations[at + 1] is not IReturnOperation ret) return false;
                if (ret.ReturnedValue != null && ret.ReturnedValue.ConstantValue.HasValue == false) return false;
            }
            return any;
        }

        // a place of the object's: `this`, then fields and elements (the index a local, a parameter or a constant)
        static bool FromThis(IOperation op)
        {
            op = ChainFlow.Strip(op);
            return op switch
            {
                IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance } => true,
                IFieldReferenceOperation { Instance: { } inst, Field.IsStatic: false } => FromThis(inst),
                IArrayElementReferenceOperation ae => ae.Indices.All(i => ChainFlow.Strip(i) is ILocalReferenceOperation or IParameterReferenceOperation or ILiteralOperation) && FromThis(ae.ArrayReference),
                _ => false,
            };
        }
        // a `ref` local given once, at its declaration, by a call on `this` whose arguments are all by value (a
        // reference such a call returns is to a place in the heap: C#'s rules let it name nothing of the callee's
        // own) or by a place of the object's
        static bool RefFromThis(ILocalSymbol rl, IOperation body)
        {
            IOperation? init = null;
            foreach (var op in body.Descendants())
            {
                if (op is IVariableDeclaratorOperation vd && SymbolEqualityComparer.Default.Equals(vd.Symbol, rl)) init = vd.Initializer?.Value;
                if (op is ISimpleAssignmentOperation { IsRef: true, Target: ILocalReferenceOperation t } && SymbolEqualityComparer.Default.Equals(t.Local, rl)) return false;
            }
            if (init == null) return false;
            init = ChainFlow.Strip(init);
            if (init is IInvocationOperation { Instance: IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance } } inv)
                return inv.Arguments.All(a => a.Parameter is { RefKind: RefKind.None });
            return init is IFieldReferenceOperation or IArrayElementReferenceOperation && FromThis(init);
        }
    }

    /// <summary>The copy of a method that stores its `out` parameter's value as it is (ordinal).</summary>
    internal MethodInst OutBorrowVariant(MethodInst baseMi, int ordinal)
    {
        var key = baseMi.Key + "|outborrow" + ordinal;
        if (_methods.TryGetValue(key, out var vi)) return vi;
        var fn = Module.AddFunction(Display(baseMi.Sym) + " [out borrowed]", baseMi.Fn.Type);
        fn.FrameName = baseMi.Fn.FrameName;
        fn.ProgramCode = baseMi.Fn.ProgramCode;
        fn.InlineHint = baseMi.Fn.InlineHint;
        fn.NoInline = baseMi.Fn.NoInline;
        vi = new MethodInst { Sym = baseMi.Sym, Fn = fn, Key = key, Impl = MethodImpl.Body, ReachedFrom = Compiling, OutBorrow = ordinal + 1 };
        _methods[key] = vi;
        _work.Enqueue(vi);
        return vi;
    }
}

sealed partial class FunctionCompiler
{
    /// <summary>The `out var` locals that hold a borrowed value: no owner's slot (LocalVar).</summary>
    readonly HashSet<ISymbol> _borrowedOut = new(SymbolEqualityComparer.Default);
    /// <summary>Set once a call's arguments are pushed: the callee's copy to call (the parameter's ordinal + 1).</summary>
    int _outBorrowNext;

    /// <summary>The ordinal of the `out var` argument this call can leave borrowed, or -1.</summary>
    int OutBorrowCall(IMethodSymbol m, bool virt, IReadOnlyList<IOperation> args)
    {
        if (!D.OutBorrowOn || virt || _closure != null || _machine != null || _clone != null || m.IsStatic || IsVirtualDispatch(m)) return -1;
        IArgumentOperation? arg = null; ILocalSymbol? local = null;
        foreach (var a in args)
            if (a is IArgumentOperation { Parameter.RefKind: RefKind.Out, Value: IDeclarationExpressionOperation { Expression: ILocalReferenceOperation lr } } ao)
            {
                if (arg != null) return -1;
                arg = ao; local = lr.Local;
            }
        if (arg == null || local == null) return -1;
        int No(string why) { if (Driver.OutBorrowDebug) Console.Error.WriteLine($"out borrow: no, {why}: {arg.Syntax} in {M.ToDisplayString()}"); return -1; }
        if (arg.Parent is not IInvocationOperation inv || IsCaptured(local)) return No("captured");
        var lt = T.Resolve(local.Type, Map);
        if (!Types.IsRef(lt) || !T.NeedsRc(lt) || Types.IsImmortalType(lt)) return No("the type");
        int ord = arg.Parameter!.Ordinal;
        if (!D.OutBorrowable(m, ord)) return No("the callee");
        var mi = D.Method(m);
        if (mi.Impl != MethodImpl.Body || mi.Compile != null || D.IsIntrinsic(m) || Library.Substitution(m.OriginalDefinition) != null) return No("the callee's kind " + mi.Impl);
        // the call is an `if`'s condition, or the left of its `&&`s
        var region = new List<IOperation>();
        IOperation top = inv;
        while (true)
        {
            var parent = top.Parent;
            if (parent is IParenthesizedOperation) { top = parent; continue; }
            if (parent is IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalAnd, OperatorMethod: null } and && and.LeftOperand == top) { region.Add(and.RightOperand); top = and; continue; }
            if (parent is IConditionalOperation { IsRef: false } c && c.Condition == top && c.Type == null) { region.Add(c.WhenTrue); break; }
            return No("not an if's condition: " + parent?.Kind);
        }
        // every use of the local is in there, and nothing in there stores a reference or runs other code
        IOperation root = inv;
        while (root.Parent != null) root = root.Parent;
        var inside = new HashSet<IOperation>();
        foreach (var part in region)
            foreach (var op in part.DescendantsAndSelf())
            {
                if (!OutBorrowQuiet(op)) { if (Driver.OutBorrowDebug) Console.Error.WriteLine($"out borrow: no, {op.Kind} {op.Syntax} after {inv.Syntax}"); return -1; }
                inside.Add(op);
            }
        foreach (var op in root.Descendants())
            if (op is ILocalReferenceOperation use && SymbolEqualityComparer.Default.Equals(use.Local, local) && !use.IsDeclaration && !inside.Contains(use)) return -1;
        if (Driver.OutBorrowDebug) Console.Error.WriteLine($"out borrow: {inv.Syntax} in {M.ToDisplayString()}");
        _borrowedOut.Add(local);
        return ord;
    }

    /// <summary>An operation that stores no reference, allocates nothing and runs no other code.</summary>
    bool OutBorrowQuiet(IOperation op)
    {
        bool Plain(ITypeSymbol? t) => t != null && t.TypeKind != TypeKind.TypeParameter && t.IsValueType && !(Types.IsStruct(T.Resolve(t, Map)) && T.LayoutOf(T.Resolve(t, Map)).HasRefs);
        bool Place(IOperation target) => target is ILocalReferenceOperation or IParameterReferenceOperation or IArrayElementReferenceOperation or IFieldReferenceOperation { Field.IsStatic: false };
        switch (op)
        {
            case IBlockOperation or IExpressionStatementOperation or IEmptyOperation or IParenthesizedOperation or ILiteralOperation or IDefaultValueOperation:
            case ILocalReferenceOperation { IsDeclaration: false } or IParameterReferenceOperation or IInstanceReferenceOperation or IArrayElementReferenceOperation:
            case IConditionalOperation { IsRef: false } or IReturnOperation or IIsTypeOperation or ICoalesceOperation:
                return true;
            case IBranchOperation b: return b.BranchKind is BranchKind.Break or BranchKind.Continue;
            // (a static's first read runs its type's initializer)
            case IFieldReferenceOperation f: return !f.Field.IsStatic || f.Field.IsConst;
            case IBinaryOperation b: return b.OperatorMethod == null && b.Type?.SpecialType != SpecialType.System_String && b.Type?.SpecialType != SpecialType.System_Object;
            case IUnaryOperation u: return u.OperatorMethod == null;
            case IConversionOperation c: return c.OperatorMethod == null && !c.Conversion.IsUserDefined && !(c.Operand.Type?.IsValueType == true && c.Type?.IsReferenceType == true) && c.Type?.TypeKind != TypeKind.Dynamic;
            case ISimpleAssignmentOperation { IsRef: false } a: return Plain(a.Target.Type) && Place(a.Target);
            case ICompoundAssignmentOperation a: return a.OperatorMethod == null && Plain(a.Target.Type) && Place(a.Target) && !a.InConversion.IsUserDefined && !a.OutConversion.IsUserDefined;
            case IIncrementOrDecrementOperation i: return i.OperatorMethod == null && Plain(i.Target.Type) && Place(i.Target);
            // (the deliberate break: a call as if it ran nothing)
            case IInvocationOperation or IArgumentOperation or IPropertyReferenceOperation: return Driver.OutBorrowBreak;
            default: return false;
        }
    }

    /// <summary>In a callee's borrowed-out copy: `value = x;` for that parameter stores x as it is.</summary>
    bool TryEmitBorrowedOutStore(ISimpleAssignmentOperation sa)
    {
        if (Inst is not { OutBorrow: > 0 } || sa.IsRef || sa.Target is not IParameterReferenceOperation pr || pr.Parameter.Ordinal != Inst.OutBorrow - 1 || pr.Parameter.RefKind != RefKind.Out) return false;
        if (!SymbolEqualityComparer.Default.Equals(pr.Parameter.ContainingSymbol.OriginalDefinition, M.OriginalDefinition)) return false;
        EmitAddress(sa.Target);
        var o = EmitValue(sa.Value);
        if (o == Own.Owned) throw new CompileException("internal: an owned value for a borrowed out parameter", sa.Syntax.GetLocation());
        E.Store(Op.I32Store, 0);
        return true;
    }
}
