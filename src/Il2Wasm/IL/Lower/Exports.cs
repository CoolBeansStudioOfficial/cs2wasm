#nullable enable
using System.Linq;
using Il2Wasm.IL.Ir;
using Il2Wasm.Wasm;
using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;
using WOp = Il2Wasm.Wasm.Op;

namespace Il2Wasm.IL.Lower;

// Exports (SPEC 6.2): a static method marked [UnmanagedCallersOnly(EntryPoint = "name")] in the program's assembly
// becomes a WASM export of that name, its parameters and result WASM numbers. No exception crosses an export: the
// export is wrapped, an exception that escapes it is reported through the host's exception-report import (type,
// message, stack trace: Rt.ReportUnhandled) and the export returns its failure value (zero).
public sealed partial class IlCompiler
{
    /// <summary>Each [UnmanagedCallersOnly(EntryPoint = ...)] method of the module, compiled and exported.</summary>
    void RootExports(EcmaModule module)
    {
        foreach (var (name, m) in ExportMethods(module)) Export(name, m);
    }

    void Export(string name, MethodDesc m)
    {
        var inner = FunctionFor(m);
        var t = inner.Type;
        var f = Module.AddFunction("export " + name, t);
        f.Exports.Add(name);
        var e = new Emitter(f);
        var caught = e.Block(ValType.I32);
        e.TryTable(null, (0, ExceptionTag, caught));
        for (int i = 0; i < t.Params.Length; i++) e.Get(i);
        e.Call(inner);
        e.Op(WOp.Return);
        e.End();
        e.Op(WOp.Unreachable);
        e.End();
        // (the exception reported, then let go; the failure value)
        int ex = e.AddLocal(ValType.I32);
        e.Set(ex);
        if (TryCoreMethod("Il2Wasm.Runtime", "Rt", "ReportUnhandled") is { } report) { e.Get(ex); e.Call(FunctionFor(report)); }
        e.Get(ex); e.Call(Rt.Release);
        foreach (var r in t.Results)
        {
            switch (r)
            {
                case ValType.I64: e.I64(0); break;
                case ValType.F32: e.Emit(WOp.F32Const, 0); break;
                case ValType.F64: e.F64(0); break;
                default: e.I32(0); break;
            }
        }
    }
}
