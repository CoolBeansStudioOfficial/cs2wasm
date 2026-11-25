#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using Il2Wasm.IL.Ir;
using Il2Wasm.IL.Types;
using Il2Wasm.Wasm;
using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;
using WOp = Il2Wasm.Wasm.Op;

namespace Il2Wasm.IL.Lower;

/// <summary>The method reflection records (IL front end; the runtime reads them in Boundary/Meta.cs and RuntimeType's
/// GetMethodsByName). A type's record lists the methods the program can find by reflection: those of the type and of its
/// bases, the names a query asks for (a Type.GetMethod's constant name, or any name for GetMethods), and for each one its
/// invoke thunk: (target, args: object[]) -> the result boxed, the arguments unboxed from the array, a virtual method
/// dispatched on the target. Only methods the program can reach this way are kept: an unknown query target or name
/// makes every reflectable type's (or every name's) methods kept, as for attributes. IL2WASM_NO_METHOD_RECORDS=1: none.
/// A method's record entry is 13 words (Meta.MethodSize): name, return type id (-1 void), flags, thunk + 1, parameter
/// count, parameter type ids, parameter names, attribute count and list (none), declaring type id, generic block and
/// defaults (none).</summary>
public sealed partial class IlCompiler
{
    const int MethodEntryWords = 13;

    /// <summary>A method a type's record lists: its name, its parameter names, and its invoke thunk.</summary>
    sealed record MethodRecord(MethodDesc Method, string Name, string[] ParamNames, WasmFunction Thunk);

    readonly Dictionary<TypeDesc, List<MethodRecord>> _typeMethods = new();
    readonly Dictionary<TypeDesc, string> _typeMethodsKey = new();
    static readonly bool s_noMethodRecords = Environment.GetEnvironmentVariable("IL2WASM_NO_METHOD_RECORDS") == "1";

    /// <summary>The methods records the program's method queries can reach, for each reflectable type; true when anything
    /// new was made. A type's record is made again when its queries' names change.</summary>
    bool RequestMethodRecords()
    {
        if (!_typeTables.ContainsKey("meta") || s_noMethodRecords || Il2Wasm.IL.Reach.ProgramFacts.Off) return false;
        var queries = Facts.MethodQueries.ToList();
        if (queries.Count == 0) return false;
        bool any = false;
        foreach (var t in _types.ToList())
        {
            if (t is not MetadataType || !IsReflectable(t) || t.IsGenericDefinition || t.IsCanonicalSubtype(CanonicalFormKind.Any)
                || t.ContainsSignatureVariables(treatGenericParameterLikeSignatureVariable: true)) continue;
            bool all = false;
            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var (target, name) in queries)
            {
                if (target != null && target != t) continue;
                if (name == null) all = true; else names.Add(name);
            }
            if (!all && names.Count == 0) continue;
            string key = (all ? "*" : "") + string.Join(",", names);
            if (_typeMethodsKey.TryGetValue(t, out var old) && old == key) continue;
            _typeMethodsKey[t] = key;
            var list = MethodRecordsOf(t, all, names);
            if (list.Count == 0) { _typeMethods.Remove(t); continue; }
            _typeMethods[t] = list;
            any = true;
        }
        return any;
    }

    /// <summary>The methods of a type and its bases the query names (all names when `all`), each once (a derived method
    /// hides a base method of the same name and parameter types), with their thunks.</summary>
    List<MethodRecord> MethodRecordsOf(TypeDesc t, bool all, ISet<string> names)
    {
        var list = new List<MethodRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (TypeDesc? b = t; b != null; b = b.BaseType)
        {
            if (b.IsGenericDefinition || b.ContainsSignatureVariables(treatGenericParameterLikeSignatureVariable: true)) continue;
            foreach (var m in b.GetMethods())
            {
                if (m.HasInstantiation || m.IsConstructor || m.IsStaticConstructor || m.IsAbstract || m.IsPInvoke || m.OwningType.IsDelegate) continue;
                if (!all && !names.Contains(m.GetName())) continue;
                if (!RecordableSignature(m)) continue;
                string sigKey = m.GetName() + "(" + string.Join(",", Enumerable.Range(0, m.Signature.Length).Select(i => m.Signature[i].ToString())) + ")";
                if (!seen.Add(sigKey)) continue;
                Reflectable(m.OwningType);
                foreach (var p in m.Signature) Reflectable(p);
                if (!m.Signature.ReturnType.IsVoid) Reflectable(m.Signature.ReturnType);
                list.Add(new MethodRecord(m, m.GetName(), ParamNamesOf(m), MethodInvokeThunk(m)));
            }
        }
        return list;
    }

    /// <summary>A method whose signature the thunk can call: no by-ref, pointer or ref-like parameter or result, no
    /// signature variables (the same as a delegate's Invoke: DelegateThunks.cs).</summary>
    static bool RecordableSignature(MethodDesc m)
    {
        if (m.OwningType.IsCanonicalSubtype(CanonicalFormKind.Any) || m.Signature.ReturnType.ContainsSignatureVariables(treatGenericParameterLikeSignatureVariable: true)) return false;
        foreach (var p in m.Signature) if (!Plain(p) || p.ContainsSignatureVariables(treatGenericParameterLikeSignatureVariable: true)) return false;
        return Plain(m.Signature.ReturnType) || m.Signature.ReturnType.IsVoid;
    }

    /// <summary>The parameter names of a method, from its metadata ("" where there is none).</summary>
    static string[] ParamNamesOf(MethodDesc m)
    {
        var names = new string[m.Signature.Length];
        Array.Fill(names, "");
        if (m.GetTypicalMethodDefinition() is EcmaMethod em)
        {
            var md = ((EcmaModule)em.Module).MetadataReader;
            foreach (var ph in md.GetMethodDefinition(em.Handle).GetParameters())
            {
                var p = md.GetParameter(ph);
                if (p.SequenceNumber >= 1 && p.SequenceNumber <= names.Length && !p.Name.IsNil) names[p.SequenceNumber - 1] = md.GetString(p.Name);
            }
        }
        return names;
    }

    /// <summary>The flags a record entry carries (Meta.cs: FStatic 1, FPublic 2, FVirtual 4, FAbstract 8, FPrivate 32,
    /// FFamily 64, FAssembly 128, special name 1 &lt;&lt; 13, final 1 &lt;&lt; 14, new slot 1 &lt;&lt; 15).</summary>
    static int MethodFlagsOf(MethodDesc m)
    {
        int f = 0;
        if (m.Signature.IsStatic) f |= 1;
        if (m.IsVirtual) f |= 4;
        if (m.IsAbstract) f |= 8;
        if (m.GetTypicalMethodDefinition() is EcmaMethod em)
        {
            var access = em.Attributes & System.Reflection.MethodAttributes.MemberAccessMask;
            f |= access switch
            {
                System.Reflection.MethodAttributes.Public => 2,
                System.Reflection.MethodAttributes.Private => 32,
                System.Reflection.MethodAttributes.Family => 64,
                System.Reflection.MethodAttributes.Assembly => 128,
                System.Reflection.MethodAttributes.FamANDAssem => 64 | 128,
                System.Reflection.MethodAttributes.FamORAssem => 64,
                _ => 0,
            };
            if ((em.Attributes & System.Reflection.MethodAttributes.SpecialName) != 0) f |= 1 << 13;
            if ((em.Attributes & System.Reflection.MethodAttributes.Final) != 0) f |= 1 << 14;
            if ((em.Attributes & System.Reflection.MethodAttributes.NewSlot) != 0) f |= 1 << 15;
        }
        if (m.IsFinal) f |= 1 << 14;
        if (m.IsNewSlot) f |= 1 << 15;
        return f;
    }

    /// <summary>The invoke thunk of a method: (target, args: object[]) -> the result boxed (null for void). Like the
    /// delegate's dynamic thunk (DelegateThunks.cs DynamicThunk): the target first (a value type's box data for an
    /// instance method), then the arguments unboxed from the array, the struct result's buffer last. A static method
    /// runs its class constructor first; a virtual one is called through its dispatch.</summary>
    WasmFunction MethodInvokeThunk(MethodDesc m)
    {
        var sig = m.Signature;
        var f = Module.AddFunction("method invoke thunk " + m, ThunkType);
        f.TableRoot = true; // (called through the table: the record names its slot)
        var e = new Emitter(f);
        int n = sig.Length;
        var rt = sig.ReturnType;
        bool isStatic = sig.IsStatic;
        Constructed(m.OwningType);
        if (isStatic) CctorCheckForCall(e, m);
        if (!isStatic)
        {
            e.Get(0);
            if (m.OwningType.IsValueType) { e.I32(IlContext.ObjectHeader); e.Op(WOp.I32Add); }
        }
        var copies = new List<int>();
        for (int i = 0; i < n; i++)
        {
            var pt = sig[i];
            e.Get(1); e.Load(WOp.I32Load, ElemDataOffset + 4 * i);
            if (Kinds.Of(pt) == VK.Ref) continue;
            e.Call(UnboxFn(pt));
            e.I32(IlContext.ObjectHeader); e.Op(WOp.I32Add);
            if (Kinds.Of(pt) != VK.Struct) { LoadTyped(e, pt, 0); continue; }
            // (a struct argument: a copy, so the callee's own writes stay off the boxed value)
            Constructed(pt);
            int src = e.AddLocal(ValType.I32), copy = e.AddLocal(ValType.I32);
            e.Set(src);
            e.I32(IlContext.ObjectHeader + Math.Max(4, Kinds.SizeOf(pt))); e.Tid(TypeId(pt)); e.Call(Rt.AllocObject); e.Set(copy);
            e.Get(copy); e.I32(IlContext.ObjectHeader); e.Op(WOp.I32Add); e.Get(src); e.I32(SizeOfStructOf(pt)); e.Emit(WOp.MemoryCopy);
            if (Kinds.Counted(pt)) { e.Get(copy); e.I32(IlContext.ObjectHeader); e.Op(WOp.I32Add); e.Call(Rt.RetainStruct(pt)); }
            copies.Add(copy);
            e.Get(copy); e.I32(IlContext.ObjectHeader); e.Op(WOp.I32Add);
        }
        int resBox = -1;
        if (Kinds.Of(rt) == VK.Struct && !rt.IsVoid)
        {
            Constructed(rt);
            resBox = e.AddLocal(ValType.I32);
            e.I32(IlContext.ObjectHeader + Math.Max(4, Kinds.SizeOf(rt))); e.Tid(TypeId(rt)); e.Call(Rt.AllocObject); e.Set(resBox);
            e.Get(resBox); e.I32(IlContext.ObjectHeader); e.Op(WOp.I32Add);
        }
        e.Call(!isStatic && m.IsVirtual && !m.IsFinal ? DispatchFor(m) : FunctionFor(m));
        int res = -1;
        if (rt.IsVoid || resBox >= 0) { }
        else if (Kinds.Of(rt) == VK.Ref) { res = e.AddLocal(ValType.I32); e.Set(res); }
        else
        {
            int v = e.AddLocal(FunctionLowering.ValOf(Kinds.Of(rt)));
            e.Set(v);
            Constructed(rt);
            res = e.AddLocal(ValType.I32);
            e.I32(IlContext.ObjectHeader + Math.Max(4, Kinds.SizeOf(rt))); e.Tid(TypeId(rt)); e.Call(Rt.AllocObject); e.Set(res);
            e.Get(res); e.I32(IlContext.ObjectHeader); e.Op(WOp.I32Add); e.Get(v); StoreTyped(e, rt, 0);
        }
        foreach (int c in copies) { e.Get(c); e.Call(Rt.Release); }
        if (rt.IsVoid) e.I32(0);
        else if (resBox >= 0) e.Get(resBox);
        else e.Get(res);
        e.Op(WOp.Return);
        return f;
    }

    /// <summary>The bytes of a type's method table: one entry per record, the name, parameter types and names as
    /// relocated lists (Meta.cs's layout).</summary>
    Blob MethodTable(List<MethodRecord> methods, Func<TypeDesc, int> final)
    {
        var bytes = new List<byte>();
        var relocs = new List<(int, IAddress)>();
        foreach (var r in methods)
        {
            int at = bytes.Count;
            var m = r.Method;
            var sig = m.Signature;
            int n = sig.Length;
            var rt = sig.ReturnType;
            var words = new int[MethodEntryWords];
            words[1] = rt.IsVoid ? -1 : final(rt);
            words[2] = MethodFlagsOf(m);
            words[3] = Module.TableSlot(r.Thunk) + 1;
            words[4] = n;
            words[9] = final(m.OwningType);
            relocs.Add((at, StringLiteral(r.Name)));
            if (n > 0)
            {
                var types = DataBlob([.. Enumerable.Range(0, n).Select(i => final(sig[i])).SelectMany(BitConverter.GetBytes)], 4);
                var names = DataBlob([.. Enumerable.Range(0, n).SelectMany(_ => BitConverter.GetBytes(0))], 4,
                    Enumerable.Range(0, n).Select(i => (i * 4, (IAddress)StringLiteral(r.ParamNames[i]))).ToList());
                relocs.Add((at + 5 * 4, types));
                relocs.Add((at + 6 * 4, names));
            }
            foreach (var w in words) bytes.AddRange(BitConverter.GetBytes(w));
        }
        return DataBlob([.. bytes], 4, relocs);
    }
}
