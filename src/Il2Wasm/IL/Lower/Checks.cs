#nullable enable
using Il2Wasm.Wasm;
using WOp = Il2Wasm.Wasm.Op;

namespace Il2Wasm.IL.Lower;

// The checked build (il2wasm-il --checked; the suites' checked mode), what the operation-tree front end's checked
// build was: the module is the same program with the runtime's checks on (Rt.Checked is 1, folded at import:
// Ir/Constants.cs), so
//   - freed memory is poisoned (the count word 0x0EAD0000, the type word 0x7EAD0000, the rest 0xDD) and never used
//     again (Runtime.cs, FreeBlock): a reference used after its object was freed reads the poison;
//   - rt.release traps on a release too many (an object whose count is zero, being destroyed, or freed) and rt.retain
//     on a count of a freed object;
//   - every function is validated (as in every build of the IL front end).
public sealed partial class IlCompiler
{
    bool _checked;
    // (a compiler starts as the shipped build: the importer's folding is process-wide)
    readonly bool _checkedReset = ResetChecked();
    static bool ResetChecked() { Ir.Constants.CheckedBuild = false; return true; }

    /// <summary>The checked build: the runtime's checks that trap on a release too many and a use after a free.</summary>
    public bool Checked { get => _checked; set { _checked = value; Ir.Constants.CheckedBuild = value; } }

    const int PoisonedCount = 0x0EAD0000;

    /// <summary>At the start of rt.retain (release false) or rt.release: the checked build's trap.</summary>
    internal void CheckedCount(Emitter e, bool release)
    {
        if (!_checked) return;
        int t = e.AddLocal(ValType.I32);
        e.Get(0);
        e.If();
        e.Get(0); e.Load(WOp.I32Load); e.Tee(t); e.I32(PoisonedCount); e.Op(WOp.I32Eq);
        if (release) { e.Get(t); e.Op(WOp.I32Eqz); e.Op(WOp.I32Or); }
        e.If(); e.Op(WOp.Unreachable); e.End();
        e.End();
    }
}
