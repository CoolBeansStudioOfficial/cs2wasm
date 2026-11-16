using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;

namespace Cs2Wasm.Compiler;

/// <summary>
/// Struct moves (section 4.1): a struct a call returns into a temporary, or a constructor builds in one, holds a count
/// for each reference in it; handing it to an owner (a variable, a field, an element, the caller's return slot) copied it
/// with a retain per reference and released the temporary at the end of the statement. A move copies the bytes and
/// clears the temporary instead: the owner takes the temporary's counts. The producer notes the temporary and where its
/// address was pushed; a consumer that comes straight after (nothing emitted in between: a conditional's join, a later
/// operand, would have) moves. The cleared temporary needs no release at the statement's end (one on an unwinding path
/// finds zeros); a temporary whose producer never ran holds zeros too (released temporaries are cleared).
/// </summary>
sealed partial class FunctionCompiler
{
    (int At, int Off) _fresh = (-1, -1);
    /// <summary>Where a consumed struct parameter's address was pushed (OwnedParams.cs): its store moves it.</summary>
    int _freshParam = -1, _structParamLoads, _structParamMoves;
    static readonly bool s_noStableStatics = Environment.GetEnvironmentVariable("CS2WASM_NO_STABLE_STATICS") == "1";
    static readonly bool s_noStructMoves =Environment.GetEnvironmentVariable("CS2WASM_NO_STRUCT_MOVES") == "1";

    static readonly bool s_noInitStores = Environment.GetEnvironmentVariable("CS2WASM_NO_INIT_STORES") == "1";

    /// <summary>A constructor's store to a field of its own object (InitOnlyFields.cs: the old value may be known null).</summary>
    string? InitStoreKey(IFieldSymbol storage, ITypeSymbol owner, IOperation? instance)
    {
        if (s_noInitStores || M.MethodKind != MethodKind.Constructor || _closure != null || _machine != null || storage.IsStatic) return null;
        if (M.ContainingType.TypeKind != TypeKind.Class || !SymbolEqualityComparer.Default.Equals(storage.ContainingType.OriginalDefinition, M.ContainingType.OriginalDefinition)) return null;
        if (instance != null && instance is not Microsoft.CodeAnalysis.Operations.IInstanceReferenceOperation { ReferenceKind: Microsoft.CodeAnalysis.Operations.InstanceReferenceKind.ContainingTypeInstance }) return null;
        var key = Driver.FieldKey(owner, storage);
        D.NoteBorrowField(key, storage);
        return key;
    }

    /// <summary>The struct temporary at off was just filled and its address pushed.</summary>
    void NoteFresh(int off) => _fresh = (E.Body.Count, off);

    /// <summary>The struct address on the stack is a fresh temporary of the current statement's, with counts: its
    /// offset, and the temporary no longer released at the statement's end.</summary>
    bool TakeFresh(ITypeSymbol t)
    {
        if (_freshParam >= 0 && _freshParam == E.Body.Count) { _freshParam = -1; _structParamMoves++; return true; }
        int off = _fresh.Off;
        if (s_noStructMoves || _fresh.At != E.Body.Count || _temps.Count == 0 || !T.NeedsRc(t)) return false;
        var s = _temps[^1].FirstOrDefault(x => x.Local < 0 && x.FrameOffset == off && !x.Moved);
        if (s == null || Types.Key(T.Canonical(s.Type)) != Types.Key(T.Canonical(t))) return false;
        s.Moved = true;
        _fresh = (-1, -1);
        return true;
    }

    /// <summary>stack: dst src (the fresh temporary): copies the bytes, clears the temporary (rt.move_init), or
    /// releases the destination's old value first (rt.move_assign).</summary>
    void MoveStruct(ITypeSymbol t, bool assign) => E.Call(assign ? D.Rt.MoveAssign(t) : D.Rt.MoveInit(t));
}
