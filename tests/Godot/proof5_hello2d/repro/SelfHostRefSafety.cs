// Repro (proof 5), for the SELF-HOSTED compiler only (the .NET-hosted one compiles this; the program prints True): a
// call that passes a ref struct and has an out argument, in the shape of what Godot's source generators write for
// every script:
//     protected override bool InvokeGodotClassMethod(in godot_string_name method, NativeVariantPtrArgs args, out godot_variant ret)
//     { ... return base.InvokeGodotClassMethod(method, args, out ret); }
// Roslyn's ref-safety analysis of such a call (RefSafetyAnalysis.VisitCall -> CheckInvocationArgMixingWithUpdatedRules
// -> inferDeclarationExpressionValEscape) runs EscapeValue.Deconstruct(out ParameterSymbol, out BoundExpression,
// out EscapeLevel, out bool), and there the toolchain module traps, in a worker thread, about three seconds into the
// build: "RuntimeError: operation does not support unaligned accesses" (an atomic operation at an address that is
// not aligned; the frames are named in ../README.md). A call with an out argument and no ref struct builds
// (int.TryParse("42", out int x)).
//   node tests/Godot/proof5_hello2d/check/self.mjs tests/Godot/proof5_hello2d/repro/SelfHostRefSafety.cs -o r.wasm
using System;
using System.Runtime.InteropServices;

ref struct Args { public int Count; }

class Base
{
    protected virtual bool Invoke(in int method, Args args, out long ret) { ret = 0; return false; }
}

class Derived : Base
{
    protected override bool Invoke(in int method, Args args, out long ret)
    {
        if (args.Count == 0) { ret = default; return true; }
        return base.Invoke(method, args, out ret);
    }

    public bool Run(Args a) => Invoke(1, a, out _);
}

static class Program
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main() => Console.WriteLine(new Derived().Run(default));
}
