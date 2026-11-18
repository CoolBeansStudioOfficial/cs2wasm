using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

/// <summary>
/// Moves (section 4.1, Lobster's model): an owned local's last use, when it hands the value to an owner (a field, an
/// element, another variable, a return), takes the local's count instead of a new one, and the local is cleared, so its
/// release at the end of its scope (or at a return or during unwinding) finds nothing: `var n = new Node(); list.head
/// = n;` costs no count operation. The last use is the last reference to the local in the method's text, not inside a
/// loop the local is declared outside of (an earlier reference would come after it in the next iteration), nor in a
/// lambda; a method with `goto` gets none. Behaviour never changes: the moved value is the one the owner would have
/// had, with one count where it had two.
/// </summary>
sealed partial class FunctionCompiler
{
    readonly HashSet<IOperation> _localMoves = [];
    static readonly bool s_noLocalMoves = Environment.GetEnvironmentVariable("CS2WASM_NO_MOVES") == "1";

    void ComputeLocalMoves(IOperation body)
    {
        if (s_noLocalMoves || _machine != null) return;
        var all = body.DescendantsAndSelf().ToList();
        if (all.Any(o => o is ILabeledOperation || o is IBranchOperation { BranchKind: BranchKind.GoTo })) return;
        foreach (var g in all.OfType<ILocalReferenceOperation>().GroupBy(r => r.Local, SymbolEqualityComparer.Default))
        {
            var l = (ILocalSymbol)g.Key!;
            if (l.IsRef || !Types.IsRef(T.Resolve(l.Type, Map)) || IsCaptured(l) || _addrTaken.Contains(l)) continue;
            var last = g.OrderBy(r => r.Syntax.SpanStart).ThenBy(r => r.Syntax.Span.Length).Last();
            if (last.IsDeclaration || InLambda(last, body)) continue;
            if (!IsOwningSink(last)) continue;
            // inside a loop, only when the local is declared in it (each iteration has its own)
            var decl = l.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
            if (decl == null) continue;
            // (declared in the loop's body: a `for` initializer's variable lives across the iterations, and its
            // increment, earlier in the text, runs after the body; a `foreach` variable is assigned afresh each time)
            bool ok = true;
            for (var p = last.Parent; p != null && !ReferenceEquals(p, body); p = p.Parent)
                if (p is ILoopOperation loop && !loop.Body.Syntax.Span.Contains(decl.Span)
                    && !(loop is IForEachLoopOperation fe && fe.Locals.Contains(l, SymbolEqualityComparer.Default)))
                { ok = false; break; }
            if (ok) _localMoves.Add(last);
        }
        // `x = new Node { Next = x }`: the one read of x in the value assigned to x, when
        // it hands the value to an owner, is the last use of what x held (the assignment replaces it): a move too,
        // in a loop or not. Not under a `try` of this method (a handler could read x after an exception between the
        // read and the assignment; one that leaves the method finds x gone with its frame either way).
        foreach (var a in all.OfType<ISimpleAssignmentOperation>())
        {
            if (a.IsRef || a.Parent is not IExpressionStatementOperation || a.Target is not ILocalReferenceOperation { Local: var l }) continue;
            if (l.IsRef || !Types.IsRef(T.Resolve(l.Type, Map)) || IsCaptured(l) || _addrTaken.Contains(l)) continue;
            ILocalReferenceOperation? only = null;
            int reads = 0;
            foreach (var d in a.Value.DescendantsAndSelf())
                if (d is ILocalReferenceOperation r && SymbolEqualityComparer.Default.Equals(r.Local, l)) { reads++; only = r; }
            if (reads != 1 || only == null || InLambda(only, body) || !IsOwningSink(only)) continue;
            // (a read in a branch of the value that does not run leaves x as it was: the assignment releases it)
            bool ok = true;
            for (var p = a.Parent; p != null && !ReferenceEquals(p, body); p = p.Parent)
                if (p is ITryOperation) { ok = false; break; }
            if (ok) _localMoves.Add(only);
        }
        // `a.Next = x; x = b;`: the statement after the one that hands x's value to an owner assigns x without
        // reading it, so that read is the last use of what x held: a move, in a loop or not (reversing a list in
        // place: `n.Next = prev; prev = n;`). The handing statement reads x once, as the value it assigns. Not under a
        // `try` of this method, as above.
        foreach (var blk in all.OfType<IBlockOperation>())
        {
            var ops = blk.Operations;
            for (int i = 0; i + 1 < ops.Length; i++)
            {
                if (ops[i] is not IExpressionStatementOperation { Operation: ISimpleAssignmentOperation { IsRef: false } give }) continue;
                if (ops[i + 1] is not IExpressionStatementOperation { Operation: ISimpleAssignmentOperation { IsRef: false, Target: ILocalReferenceOperation { Local: var l } } take }) continue;
                var value = give.Value;
                while (value is IConversionOperation { OperatorMethod: null } c && (c.Conversion.IsIdentity || c.Conversion.IsImplicit && c.Conversion.IsReference)) value = c.Operand;
                if (value is not ILocalReferenceOperation read || !SymbolEqualityComparer.Default.Equals(read.Local, l)) continue;
                if (l.IsRef || !Types.IsRef(T.Resolve(l.Type, Map)) || IsCaptured(l) || _addrTaken.Contains(l)) continue;
                if (_localMoves.Contains(read) || InLambda(read, body) || !IsOwningSink(read)) continue;
                bool ok = true;
                foreach (var d in give.Target.DescendantsAndSelf().Concat(take.Value.DescendantsAndSelf()))
                    if (d is ILocalReferenceOperation r && SymbolEqualityComparer.Default.Equals(r.Local, l) || d is IAnonymousFunctionOperation or ILocalFunctionOperation) { ok = false; break; }
                for (var p = blk.Parent; ok && p != null && !ReferenceEquals(p, body); p = p.Parent)
                    if (p is ITryOperation) ok = false;
                if (ok) _localMoves.Add(read);
            }
        }
    }

    static bool InLambda(IOperation op, IOperation body)
    {
        for (var p = op.Parent; p != null && !ReferenceEquals(p, body); p = p.Parent)
            if (p is IAnonymousFunctionOperation or ILocalFunctionOperation) return true;
        return false;
    }

    /// <summary>The read hands its value to something that owns it: a field, an element, another variable, a return.</summary>
    bool IsOwningSink(ILocalReferenceOperation read)
    {
        IOperation child = read;
        var p = read.Parent;
        while (p is IConversionOperation { OperatorMethod: null } c && (c.Conversion.IsIdentity || c.Conversion.IsImplicit && c.Conversion.IsReference)) { child = p; p = p.Parent; }
        switch (p)
        {
            case ISimpleAssignmentOperation { IsRef: false } sa when ReferenceEquals(sa.Value, child):
                return sa.Target switch
                {
                    IFieldReferenceOperation f => !f.Field.IsVolatile,
                    IArrayElementReferenceOperation => true,
                    ILocalReferenceOperation tl => !SymbolEqualityComparer.Default.Equals(tl.Local, read.Local) && !tl.Local.IsRef,
                    _ => false,
                };
            case IVariableInitializerOperation { Parent: IVariableDeclaratorOperation vd } when !vd.Symbol.IsRef:
                return true;
            case IReturnOperation { ReturnedValue: var rv } when ReferenceEquals(rv, child):
                return !M.ReturnsByRef && !M.ReturnsByRefReadonly && _closure == null && Inst?.BorrowedReturn == null;
            case IArrayInitializerOperation:
                return true;
        }
        return false;
    }

    /// <summary>A move's load: the local's value with its count, the local cleared.</summary>
    bool TryEmitLocalMove(ILocalReferenceOperation l, out Own own)
    {
        own = Own.None;
        if (!_localMoves.Contains(l)) return false;
        // (a local that may borrow every value it gets has no count to hand over)
        if (WrittenBorrow(l.Local) != null) return false;
        var v = LocalVar(l.Local);
        // (only an owner moves: a borrowing local, a field borrow's or a frame object's, has a release marker)
        var slot = _allSlots.FirstOrDefault(s => v.InMemory ? s.Local < 0 && s.FrameOffset == v.Offset && v.AddrLocal == _fb : s.Local == v.Local);
        if (slot == null || slot.ReleaseMarker != null || !Types.IsRef(v.Type)) return false;
        if (!v.InMemory) { E.Get(v.Local); E.I32(0); E.Set(v.Local); }
        else
        {
            if (v.AddrLocal != _fb) return false;
            E.Get(_fb); E.Load(Op.I32Load, v.Offset);
            E.Get(_fb); E.I32(0); E.Store(Op.I32Store, v.Offset);
        }
        own = Own.Owned;
        return true;
    }
}
