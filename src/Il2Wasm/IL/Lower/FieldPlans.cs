#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Il2Wasm.Wasm;
using Internal.TypeSystem;

namespace Il2Wasm.IL.Lower;

public sealed partial class IlCompiler
{
    /// <summary>What a field of a value type is compared and hashed by (ValueType.Equals and GetHashCode in the runtime):
    /// Raw bytes; Ref, an object by object.Equals; F32 and F64, a float by double.Equals (0.0 equals -0.0, NaN equals NaN);
    /// Override, a struct with its own Equals or GetHashCode, called on the boxed field; Nullable, a Nullable&lt;T&gt; boxed
    /// as its value (null when it has none), as Nullable&lt;T&gt;.Equals compares it.</summary>
    internal enum PlanKind { Raw = 0, Ref = 1, F32 = 2, F64 = 3, Override = 4, Nullable = 5 }

    /// <summary>One compared field: its kind, offset and size from the start of the value; Type is the boxed struct (Override)
    /// or the Nullable's argument (Nullable), ValueOff the argument's offset in the Nullable.</summary>
    internal readonly record struct PlanSeg(PlanKind Kind, int Off, int Size, TypeDesc? Type, int ValueOff);

    readonly Dictionary<TypeDesc, PlanSeg[]?> _plans = new();
    readonly HashSet<TypeDesc> _boxedValues = new();

    /// <summary>A value type the program boxes: its Equals and GetHashCode may be ValueType's, so its plan is made (a value
    /// type that is only an array's element is not boxed by the program and gets none: its table entry would be unused).</summary>
    internal void Boxed(TypeDesc t)
    {
        Constructed(t);
        if (!t.IsValueType || !_boxedValues.Add(t)) return;
        if (_fieldPlansWanted) PlanOf(t);
    }
    readonly Dictionary<string, Blob> _planLists = new();
    bool _fieldPlansWanted;
    Blob? _fieldPlans;

    /// <summary>CoreCLR's CanCompareBits, the rule for comparing a value's bytes: the value's fields are compared one by one
    /// unless it has no reference, float or overriding field and no padding. Null when the bytes compare (the fast path);
    /// otherwise its fields, in offset order.</summary>
    PlanSeg[]? PlanOf(TypeDesc t)
    {
        if (_plans.TryGetValue(t, out var known)) return known;
        _plans[t] = null; // (a struct does not contain itself)
        // (a primitive's Equals and GetHashCode are its own: ValueType's are never reached for it)
        if (t is not MetadataType { IsValueType: true } mt || t.IsPrimitive || t.ContainsSignatureVariables(treatGenericParameterLikeSignatureVariable: true) || mt.IsByRefLike) return null;
        var segs = new List<PlanSeg>();
        Walk(mt, 0, segs);
        segs.Sort((x, y) => x.Off.CompareTo(y.Off));
        bool special = false;
        int cursor = 0;
        foreach (var s in segs)
        {
            if (s.Kind != PlanKind.Raw || s.Off != cursor) special = true; // (a reference, float or override, or a gap: padding)
            cursor = s.Off + s.Size;
        }
        if (cursor != mt.InstanceFieldSize.AsInt) special = true;
        if (!special) return null;
        return _plans[t] = segs.ToArray();
    }

    /// <summary>The fields of a struct, flattened through its fields that are compared by their own fields (structs
    /// without an overriding Equals or GetHashCode), and through its inline array's elements.</summary>
    void Walk(MetadataType st, int baseOff, List<PlanSeg> segs)
    {
        int repeat = st.IsInlineArray ? st.GetClassLayout().InlineArrayLength : 1;
        foreach (var f in st.GetFields())
        {
            if (f.IsStatic) continue;
            int stride = SizeOfField(f.FieldType);
            for (int i = 0; i < repeat; i++) AddField(f.FieldType, baseOff + f.Offset.AsInt + i * stride, segs);
        }
    }

    void AddField(TypeDesc ft, int off, List<PlanSeg> segs)
    {
        int ptr = ft.Context.Target.PointerSize;
        if (ft.IsGCPointer) { segs.Add(new(PlanKind.Ref, off, ptr, null, 0)); return; }
        if (!ft.IsValueType || ft is not DefType dt) { segs.Add(new(PlanKind.Raw, off, ptr, null, 0)); return; }
        int size = dt.InstanceFieldSize.AsInt;
        if (ft.IsPrimitive && !ft.IsEnum)
        {
            if (ft.Category == TypeFlags.Single) segs.Add(new(PlanKind.F32, off, size, null, 0));
            else if (ft.Category == TypeFlags.Double) segs.Add(new(PlanKind.F64, off, size, null, 0));
            else segs.Add(new(PlanKind.Raw, off, size, null, 0));
            return;
        }
        if (ft is MetadataType { IsValueType: true } vt && IsNullableType(vt))
        {
            var value = vt.GetFields().First(f => !f.IsStatic && f.GetName() == "value");
            TypeDesc arg = vt.Instantiation[0];
            TypeId(arg);
            Constructed(arg);
            segs.Add(new(PlanKind.Nullable, off, size, arg, value.Offset.AsInt));
            return;
        }
        if (ft is MetadataType mt && Overrides(mt))
        {
            TypeId(ft);
            Constructed(ft);
            segs.Add(new(PlanKind.Override, off, size, ft, 0));
            return;
        }
        if (ft is MetadataType struct_) { Walk(struct_, off, segs); return; }
        segs.Add(new(PlanKind.Raw, off, size, null, 0));
    }

    static bool IsNullableType(MetadataType t) =>
        t.HasInstantiation && t.GetTypeDefinition() is MetadataType d && d.GetName() == "Nullable`1" && d.GetNamespace() == "System";

    /// <summary>Whether the struct declares its own Equals or GetHashCode (an override of ValueType's).</summary>
    static bool Overrides(MetadataType t) =>
        t.GetMethods().Any(m => m.IsVirtual && m.OwningType == t && (m.GetName() == "Equals" || m.GetName() == "GetHashCode"));

    static int SizeOfField(TypeDesc ft) => ft.IsValueType && ft is DefType d ? d.InstanceFieldSize.AsInt : ft.Context.Target.PointerSize;

    /// <summary>TypeInfo.FieldPlans, made when the program's code asks for it: [count, (final type id, plan list)...] ascending
    /// by id, for the value types whose plans are not the bytes. A plan list is [count, (kind, offset, size, type id, value
    /// offset)...].</summary>
    void EmitFieldPlans(Func<TypeDesc, int> final)
    {
        if (!_fieldPlansWanted) return;
        var entries = new List<(int id, Blob list)>();
        foreach (var (t, plan) in _plans)
            if (plan != null) entries.Add((final(t), PlanList(plan, final)));
        entries.Sort((x, y) => x.id.CompareTo(y.id));
        var words = new List<int> { entries.Count };
        var relocs = new List<(int, IAddress)>();
        foreach (var (id, list) in entries)
        {
            words.Add(id);
            relocs.Add((words.Count * 4, list));
            words.Add(0);
        }
        _fieldPlans = DataBlob([.. words.SelectMany(BitConverter.GetBytes)], 4, relocs);
    }

    Blob PlanList(PlanSeg[] plan, Func<TypeDesc, int> final)
    {
        var words = new List<int> { plan.Length };
        foreach (var s in plan) words.AddRange([(int)s.Kind, s.Off, s.Size, s.Type == null ? 0 : final(s.Type), s.ValueOff]);
        var key = string.Join(",", words);
        if (!_planLists.TryGetValue(key, out var b))
            _planLists[key] = b = DataBlob([.. words.SelectMany(BitConverter.GetBytes)], 4);
        return b;
    }
}
