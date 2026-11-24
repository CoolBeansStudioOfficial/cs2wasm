#nullable enable
using Internal.TypeSystem;

namespace Il2Wasm.IL.Ir;

// il-runtime: pinned locals (SPEC 6.5: a pointer into an object, `fixed`, keeps the object alive and in place for its
// scope). A store to a pinned local of an address also stores the object the address points into (Ownership.RootOf,
// the bases the address tracking follows) into a hidden reference variable, which holds a count on it until the
// pinned local is stored again (C#'s end of a fixed block stores 0 or null) or the method returns. A pinned local of
// a reference type is a variable itself. Without this, the object's count went at its last use as a reference, and
// the pointer read freed memory (the checked build's poison: every Console.WriteLine's pinned buffers).
public sealed partial class Importer
{
    readonly System.Collections.Generic.Dictionary<int, int> _pinOf = new();

    void PinSlots()
    {
        for (int i = 0; i < _nLocals; i++)
        {
            if (!_locals[i].IsPinned) continue;
            int v = _nArgs + i;
            var t = VarType(v);
            if (Kinds.Of(t) == VK.Ref)
            {
                if (!_slotOf.ContainsKey(v)) { _slotOf[v] = _body.Vars.Count; _body.Vars.Add(new Var { Kind = VK.Ref, Type = t }); }
                continue;
            }
            _pinOf[v] = _body.Vars.Count;
            _body.Vars.Add(new Var { Kind = VK.Ref, Type = _ctx.GetWellKnownType(WellKnownType.Object) });
        }
    }

    sealed partial class BlockImporter
    {
        /// <summary>A store of val to the IL variable v: if v is a pinned local, its object is held (null: let go).</summary>
        void Pin(int v, Node val)
        {
            if (!I._pinOf.TryGetValue(v, out int slot)) return;
            var root = Ownership.RootOf(val) ?? Add(Opc.LdNull, VK.Ref, StackType.ObjRef, Ctx.GetWellKnownType(WellKnownType.Object));
            var n = Add(Opc.VarSt, VK.Void, StackType.None, null, root);
            n.I = slot;
        }
    }
}
