using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

// A local that is assigned again borrows too (section 4.1), when every value it is ever given is one that something
// else keeps alive for the local's whole scope: `this`, a parameter, null, a field of one of those, or a field of
// the local's own value (`for (var n = head; n != null; n = n.Next)`, `node = key < node.Key ? node.Left : node.Right`).
// The object the local holds is then kept by the field it was read from, and that object by the one before it, back
// to the parameter or `this`, as long as none of those fields is stored while the local is in scope: the same
// question a local borrowing from one field asks (FieldBorrows.cs), over all the fields the values come through. Every
// assignment goes through one placeholder, which becomes nothing (and the local's releases go) or the count.
sealed partial class FunctionCompiler
{
    sealed class BorrowLocal { public required string Keys; public required WasmFunction Marker; public bool Registered; }

    Dictionary<ILocalSymbol, List<IOperation?>>? _localWrites; // each written local's assigned values; null: not only plain assignments
    readonly Dictionary<ILocalSymbol, List<int>> _localWritePositions = new(SymbolEqualityComparer.Default); // where they are, in the text
    readonly Dictionary<ILocalSymbol, BorrowLocal?> _borrowLocals = new(SymbolEqualityComparer.Default);
    static readonly bool s_noLocalBorrows = Environment.GetEnvironmentVariable("CS2WASM_NO_LOCAL_BORROWS") == "1";

    /// <summary>The values each local of the method is assigned after its declaration (a local written any other way,
    /// by `ref`, `out`, a compound assignment, a pattern, maps to a list holding null).</summary>
    Dictionary<ILocalSymbol, List<IOperation?>> LocalWrites()
    {
        if (_localWrites != null) return _localWrites;
        _localWrites = new(SymbolEqualityComparer.Default);
        foreach (var r in M.OriginalDefinition.DeclaringSyntaxReferences)
        {
            if (D.ModelFor(r.SyntaxTree).GetOperation(r.GetSyntax()) is not { } body) continue;
            foreach (var o in body.DescendantsAndSelf())
            {
                if (o is not ILocalReferenceOperation lr) continue;
                var p = o.Parent;
                IOperation? value;
                if (p is ISimpleAssignmentOperation { IsRef: false } a && a.Target == o) value = a.Value;
                else if (p is ISimpleAssignmentOperation { IsRef: true } || p is ICompoundAssignmentOperation ca && ca.Target == o || p is ICoalesceAssignmentOperation qa && qa.Target == o
                    || p is IIncrementOrDecrementOperation || p is IAddressOfOperation || p is IDeconstructionAssignmentOperation
                    || p is IArgumentOperation { Parameter.RefKind: not RefKind.None } || p is ITupleOperation
                    || p is IVariableInitializerOperation { Parent: IVariableDeclaratorOperation { Symbol.IsRef: true } }
                    || lr.IsDeclaration && p is not IVariableDeclaratorOperation)
                    value = null;
                else continue;
                if (!_localWrites.TryGetValue(lr.Local, out var list)) { _localWrites[lr.Local] = list = []; _localWritePositions[lr.Local] = []; }
                list.Add(value);
                _localWritePositions[lr.Local].Add(lr.Syntax.SpanStart);
            }
        }
        return _localWrites;
    }

    /// <summary>The local, when it is assigned again and every value it gets can be borrowed for its scope.</summary>
    BorrowLocal? WrittenBorrow(ILocalSymbol l)
    {
        if (_borrowLocals.TryGetValue(l, out var known)) return known;
        BorrowLocal? r = null;
        if (!s_noLocalBorrows && !l.IsRef && _closure == null && _machine == null && !IsCaptured(l) && !_addrTaken.Contains(l)
            && Types.IsRef(T.Resolve(l.Type, Map)) && !Types.IsImmortalType(T.Resolve(l.Type, Map))
            && LocalWrites().TryGetValue(l, out var writes) && writes.All(w => w != null))
        {
            var keys = new SortedSet<string>(StringComparer.Ordinal);
            bool ok = true;
            // (its declaration's value too)
            var decl = l.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
            if (decl is not VariableDeclaratorSyntax || D.ModelFor(decl.SyntaxTree).GetOperation(decl) is not IVariableDeclaratorOperation vd) ok = false;
            else if ((vd.Initializer?.Value ?? (vd.Parent as IVariableDeclarationOperation)?.Initializer?.Value) is { } init) ok = BorrowSource(l, init, keys);
            foreach (var w in writes) if (ok) ok = BorrowSource(l, w!, keys);
            if (ok)
                r = new BorrowLocal
                {
                    Keys = string.Join("\n", keys),
                    Marker = D.Module.AddFunction("borrow? " + (keys.Count == 0 ? "" : string.Join("|", keys)), new FuncType([ValType.I32], [ValType.I32])),
                };
        }
        _borrowLocals[l] = r;
        return r;
    }

    /// <summary>Whether a value assigned to the local is kept alive by something else for the local's scope; the fields
    /// it comes through are added to `keys`.</summary>
    bool BorrowSource(ILocalSymbol self, IOperation e, SortedSet<string> keys)
    {
        while (e is IConversionOperation { OperatorMethod: null } c && (c.Conversion.IsIdentity || c.Conversion.IsReference && c.Conversion.IsImplicit) || e is IParenthesizedOperation)
            e = e is IConversionOperation cc ? cc.Operand : ((IParenthesizedOperation)e).Operand;
        if (e.ConstantValue is { HasValue: true, Value: null }) return true;
        switch (e)
        {
            case IConditionalOperation { WhenFalse: { } wf } co:
                return BorrowSource(self, co.WhenTrue, keys) && BorrowSource(self, wf, keys);
            case ICoalesceOperation q:
                return BorrowSource(self, q.Value, keys) && BorrowSource(self, q.WhenNull, keys);
            case ILocalReferenceOperation lr when SymbolEqualityComparer.Default.Equals(lr.Local, self):
                return true;
            case ILocalReferenceOperation lr:
                return HeldThroughScope(self, lr.Local);
            case IInstanceReferenceOperation or IParameterReferenceOperation:
                return IsStableBase(e);
            case IFieldReferenceOperation { Field: { IsStatic: false, IsVolatile: false } f, Instance: { } inst } fr
                when !D.ThreadMode && f.ContainingType.TypeKind == TypeKind.Class && Types.IsRef(T.Resolve(f.Type, Map)):
                {
                    while (inst is IConversionOperation { OperatorMethod: null, Conversion.IsReference: true } ic) inst = ic.Operand;
                    // the object the field is read from: the local's own value, `this`, a parameter
                    bool held = inst is ILocalReferenceOperation il && (SymbolEqualityComparer.Default.Equals(il.Local, self) || HeldThroughScope(self, il.Local))
                        || inst is IInstanceReferenceOperation or IParameterReferenceOperation && IsStableBase(inst);
                    if (!held) return false;
                    var key = Driver.FieldKey(Owner(fr.Field), StorageField(fr.Field));
                    D.NoteBorrowField(key, StorageField(fr.Field), T.Resolve(f.Type, Map));
                    keys.Add(key);
                    return true;
                }
        }
        return false;
    }

    /// <summary>Another local keeps its value for the whole scope of `self`: it is declared outside that scope and never
    /// written inside it (so whatever it holds there, as an owner or as a borrower itself, it holds throughout).</summary>
    bool HeldThroughScope(ILocalSymbol self, ILocalSymbol other)
    {
        if (other.IsRef || IsCaptured(other) || _addrTaken.Contains(other) || !Types.IsRef(T.Resolve(other.Type, Map))) return false;
        var sd = self.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        var od = other.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        if (sd == null || od == null || sd.SyntaxTree != od.SyntaxTree) return false;
        // the scope of `self`: the statement list or `for` statement its declaration is in
        SyntaxNode? scope = sd;
        while (scope != null && scope is not (BlockSyntax or ForStatementSyntax or SwitchSectionSyntax or UsingStatementSyntax or FixedStatementSyntax)) scope = scope.Parent;
        if (scope == null) return false;
        var span = scope.Span;
        if (span.Contains(od.Span)) return false;
        LocalWrites();
        if (_localWritePositions.TryGetValue(other, out var ws))
            foreach (int p in ws) if (span.Contains(p)) return false;
        return true;
    }

    /// <summary>A value for such a local is on the stack (not owned): through the placeholder; false if it is owned
    /// (the local then owns, here and everywhere: the placeholder's absence tells FieldBorrows).</summary>
    void EmitBorrowMark(Var v, BorrowLocal wb, Own o)
    {
        if (o == Own.Owned) return;
        E.Call(wb.Marker);
        if (!wb.Registered) { wb.Registered = true; D.FieldBorrows.Add((E.Fn, v.Local, wb.Keys, wb.Marker)); }
    }

    /// <summary>`x = value` for such a local.</summary>
    Own EmitBorrowAssign(Var v, IOperation value, BorrowLocal wb, bool needResult)
    {
        var o = EmitValueAs(value, v.Type);
        EmitBorrowMark(v, wb, o);
        int nv = Scratch(ValType.I32);
        E.Set(nv);
        E.Get(v.Local); E.Call(Rt.Release);
        E.Get(nv); E.Set(v.Local);
        FreeScratch(ValType.I32, nv);
        if (!needResult) return Own.None;
        E.Get(v.Local);
        return Own.Stable;
    }
}
