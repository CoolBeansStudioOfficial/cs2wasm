#nullable enable
using Internal.IL;

namespace Il2Wasm.IL.Ir;

// il-runtime: .NET's two-pass exception handling runs a filter before the finally blocks of the frames it would
// unwind (SPEC 3), so a filter is compiled as a function of its own that the first pass calls (Lower/Filters.cs),
// while the method's frame is still there. Every local and argument the filter uses is therefore a frame variable
// (InFrame): the method and its filter read and write the same memory. An argument that is a struct lives in the
// caller's copy, which the filter's function cannot reach: such a method keeps its filters in one pass.
public sealed partial class Importer
{
    void FilterSlots()
    {
        bool any = false;
        foreach (var c in _il.GetExceptionRegions()) if (c.Kind == ILExceptionRegionKind.Filter) { any = true; break; }
        if (!any) return;
        var rd = new ILReader(_code);
        while (rd.HasNext)
        {
            int at = rd.Offset;
            var op = rd.ReadILOpcode();
            int v = -1;
            switch (op)
            {
                case ILOpcode.ldloc_0: case ILOpcode.ldloc_1: case ILOpcode.ldloc_2: case ILOpcode.ldloc_3: v = _nArgs + (op - ILOpcode.ldloc_0); break;
                case ILOpcode.stloc_0: case ILOpcode.stloc_1: case ILOpcode.stloc_2: case ILOpcode.stloc_3: v = _nArgs + (op - ILOpcode.stloc_0); break;
                case ILOpcode.ldarg_0: case ILOpcode.ldarg_1: case ILOpcode.ldarg_2: case ILOpcode.ldarg_3: v = op - ILOpcode.ldarg_0; break;
                case ILOpcode.ldloc_s: case ILOpcode.stloc_s: case ILOpcode.ldloca_s: v = _nArgs + rd.ReadILByte(); break;
                case ILOpcode.ldloc: case ILOpcode.stloc: case ILOpcode.ldloca: v = _nArgs + rd.ReadILUInt16(); break;
                case ILOpcode.ldarg_s: case ILOpcode.starg_s: case ILOpcode.ldarga_s: v = rd.ReadILByte(); break;
                case ILOpcode.ldarg: case ILOpcode.starg: case ILOpcode.ldarga: v = rd.ReadILUInt16(); break;
                default: rd.Skip(op); break;
            }
            if (v < 0) continue;
            bool inFilter = false;
            for (var r = Innermost(at); r != null; r = r.Parent) if (r.Kind == RegionKind.Filter) { inFilter = true; break; }
            if (!inFilter) continue;
            if (!_slotOf.TryGetValue(v, out int si)) { _slotOf[v] = si = _body.Vars.Count; _body.Vars.Add(new Var { Kind = Kinds.Of(VarType(v)), Type = VarType(v) }); }
            if (_body.Vars[si].ArgIndex >= 0) { _body.FiltersOnePass = true; continue; }
            _body.Vars[si].InFrame = true;
        }
    }
}
