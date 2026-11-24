#nullable enable
using System.Linq;
using Internal.IL;
using Internal.IL.Stubs;
using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;

namespace Il2Wasm.IL.Lower;

/// <summary>[UnsafeAccessor] extern methods (ECMA has no body for them: the runtime makes one, as CoreCLR and NativeAOT
/// do): IL that reaches the named member of the target type. The target type is the first parameter's (a byref's
/// element type for a struct), or the return type for a constructor. Kinds: Constructor (newobj), Method (callvirt on
/// a class, call on a struct), StaticMethod (call), Field (ldflda), StaticField (ldsflda). A member that does not
/// match by name and signature throws MissingMethodException or MissingFieldException when called.</summary>
static class UnsafeAccessors
{
    enum Kind { Constructor, Method, StaticMethod, Field, StaticField }

    public static MethodIL? Body(MethodDesc m)
    {
        if (m.GetTypicalMethodDefinition() is not EcmaMethod em) return null;
        var attr = em.GetDecodedCustomAttribute("System.Runtime.CompilerServices", "UnsafeAccessorAttribute");
        if (attr is not { } a || a.FixedArguments.Length < 1) return null;
        var kind = (Kind)(int)a.FixedArguments[0].Value!;
        string name = m.GetName();
        foreach (var na in a.NamedArguments) if (na.Name == "Name" && na.Value is string s) name = s;
        var sig = m.Signature;
        var e = new ILEmitter();
        var c = e.NewCodeStream();
        var ctx = m.Context;
        TypeDesc? target = kind == Kind.Constructor ? sig.ReturnType : sig.Length > 0 ? sig[0] : null;
        if (target is ByRefType br) target = br.ParameterType;
        bool ok = false;
        if (target is MetadataType tt)
        {
            switch (kind)
            {
                case Kind.Constructor:
                {
                    var ctor = tt.GetMethods().FirstOrDefault(x => x.IsConstructor && !x.Signature.IsStatic && ParamsMatch(x.Signature, sig, 0));
                    if (ctor == null) break;
                    for (int i = 0; i < sig.Length; i++) c.EmitLdArg(i);
                    c.Emit(ILOpcode.newobj, e.NewToken(ctor));
                    ok = true;
                    break;
                }
                case Kind.Method: case Kind.StaticMethod:
                {
                    bool isStatic = kind == Kind.StaticMethod;
                    MethodDesc? found = null;
                    foreach (var x in tt.GetMethods())
                    {
                        if (x.GetName() != name || x.Signature.IsStatic != isStatic || x.Instantiation.Length != m.Instantiation.Length) continue;
                        var xi = m.HasInstantiation ? ctx.GetInstantiatedMethod(x, m.Instantiation) : x;
                        if (!ParamsMatch(xi.Signature, sig, 1) || !Same(xi.Signature.ReturnType, sig.ReturnType)) continue;
                        found = xi;
                        break;
                    }
                    if (found == null) break;
                    for (int i = 0; i < sig.Length; i++) if (i > 0 || !isStatic) c.EmitLdArg(i);
                    c.Emit(!isStatic && !tt.IsValueType ? ILOpcode.callvirt : ILOpcode.call, e.NewToken(found));
                    ok = true;
                    break;
                }
                case Kind.Field: case Kind.StaticField:
                {
                    bool isStatic = kind == Kind.StaticField;
                    if (sig.ReturnType is not ByRefType rt) break;
                    var f = tt.GetFields().FirstOrDefault(x => x.GetName() == name && x.IsStatic == isStatic && Same(x.FieldType, rt.ParameterType));
                    if (f == null) break;
                    if (isStatic) c.Emit(ILOpcode.ldsflda, e.NewToken(f));
                    else { c.EmitLdArg(0); c.Emit(ILOpcode.ldflda, e.NewToken(f)); }
                    ok = true;
                    break;
                }
            }
        }
        if (!ok)
        {
            // (what .NET throws for an accessor whose member is not there, at the call)
            bool field = kind is Kind.Field or Kind.StaticField;
            var ex = ctx.SystemModule.GetType("System"u8, field ? "MissingFieldException"u8 : "MissingMethodException"u8);
            var ctor = ex.GetMethods().First(x => x.IsConstructor && x.Signature.Length == 0);
            c.Emit(ILOpcode.newobj, e.NewToken(ctor));
            c.Emit(ILOpcode.throw_);
            return e.Link(m);
        }
        c.Emit(ILOpcode.ret);
        return e.Link(m);
    }

    static bool Same(TypeDesc a, TypeDesc b) => a == b;

    /// <summary>The target's parameters are the accessor's from `skip` on (the first is the target for a method).</summary>
    static bool ParamsMatch(MethodSignature target, MethodSignature accessor, int skip)
    {
        if (target.Length != accessor.Length - skip) return false;
        for (int i = 0; i < target.Length; i++) if (!Same(target[i], accessor[i + skip])) return false;
        return true;
    }
}
