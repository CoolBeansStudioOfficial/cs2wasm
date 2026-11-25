#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Il2Wasm.IL.Types;
using Il2Wasm.Wasm;
using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;
using TF = Internal.TypeSystem.TypeFlags;

namespace Il2Wasm.IL.Lower;

/// <summary>The marshalling records of types (Marshal.SizeOf, OffsetOf, StructureToPtr, PtrToStructure, DestroyStructure):
/// the field list the runtime lays a native structure out from (Boundary/Marshal.Mono.cs, MarshalLayout), read by
/// TypeInfo.Marshaling. A record is kept only for the types a marshalling call can name: a Type the code names (typeof,
/// a generic argument), or every candidate when a call's type is not known (ProgramFacts.MarshalUnknown). A type's
/// records include those of the value types its fields hold: a nested structure is laid out from its own record.
/// <para>Record (i32 words): present (1), layout (0 auto, 1 sequential, 2 explicit), pack, size attribute, charset
/// (0 ansi, 1 unicode, 2 auto), data base (the managed offset of the first data byte: 0 for a structure, the object
/// header for a class, whose field offsets include it), field count, address of the fields.</para>
/// <para>A field is eleven words: name (the address of a string), managed offset, kind, type id, native type (the
/// ECMA-335 NativeType of its marshalling descriptor, 0 none), managed size, count (a fixed array's or fixed buffer's
/// length), element kind, element native type, element type id (-1 none), flags (1: a private field).
/// A class's base is an entry of kind base (its type id): laid out as a structure of its own, first (an explicit
/// layout lists the base's fields instead). Kinds: 1 bool, 2 char, 3 scalar, 4 structure, 5 string, 6 fixed array
/// (ByValArray: a reference to an array), 7 not marshalable, 8 fixed buffer (inline elements), 9 base, 10 delegate (a
/// function pointer). A field's type id is its own (a structure's, or an array's, which the runtime allocates).</para>
/// </summary>
public sealed partial class IlCompiler
{
    const int KindBool = 1, KindChar = 2, KindScalar = 3, KindStruct = 4, KindString = 5, KindFixedArray = 6, KindNone = 7,
        KindInline = 8, KindBase = 9, KindDelegate = 10;
    const int NativeFixedArray = 0x1E, NativeFixedSysString = 0x17, NativeU1 = 0x04, NativeI2 = 0x05;

    readonly HashSet<TypeDesc> _marshalWanted = [];
    static readonly bool s_noMarshalRecords = Environment.GetEnvironmentVariable("IL2WASM_NO_MARSHAL_RECORDS") == "1";

    /// <summary>The types whose marshalling records the program's calls can need, closed over the value types their
    /// fields hold; true when any is new.</summary>
    bool RequestMarshalRecords()
    {
        if (!_typeTables.ContainsKey("marshal") || s_noMarshalRecords) return false;
        bool any = false;
        IEnumerable<TypeDesc> roots = Facts.MarshalUnknown || NoFactsGathered()
            ? _types.ToList().Where(IsMarshalCandidate)
            : Facts.MarshalTargets.Where(IsMarshalCandidate).ToList();
        foreach (var t in roots.ToList()) any |= WantMarshal(t);
        return any;
    }

    static bool IsMarshalCandidate(TypeDesc t) =>
        t is DefType d && !t.IsInterface && !t.HasInstantiation && !t.IsGenericDefinition && !t.IsString && !t.IsPrimitive
        && !t.ContainsSignatureVariables(treatGenericParameterLikeSignatureVariable: true) && !t.IsByRefLike && (t.IsValueType || d is not MetadataType { IsAbstract: true });

    bool WantMarshal(TypeDesc t)
    {
        if (!_marshalWanted.Add(t)) return false;
        TypeId(t);
        if (BaseOf(t) is { } b) WantMarshal(b);
        foreach (var f in ((DefType)t).GetFields())
        {
            if (f.IsStatic) continue;
            var ft = f.FieldType;
            if (FixedBufferElement(ft) is { } buffered) WantMarshalMember(buffered);
            else if (ft is ArrayType at && MarshalDescriptor(f) is { Native: NativeFixedArray })
            {
                Constructed(ft);
                WantMarshalMember(at.ElementType);
            }
            else WantMarshalMember(ft);
        }
        return true;
    }

    void WantMarshalMember(TypeDesc ft)
    {
        if (ft is DefType { IsValueType: true } && !ft.IsPrimitive && !ft.IsEnum && !ft.IsString) WantMarshal(ft);
    }

    /// <summary>A class's base type when its layout has one of its own (not System.Object); null otherwise.</summary>
    static DefType? BaseOf(TypeDesc t) => !t.IsValueType && t.BaseType is DefType b && !b.IsObject ? b : null;

    /// <summary>A field's marshalling descriptor (ECMA-335 II.23.4): the native type, the count of a fixed array or a
    /// fixed string, and the element's native type. Null when the field has none.</summary>
    static (int Native, int Count, int ElementNative)? MarshalDescriptor(FieldDesc f)
    {
        if (f is not EcmaField ef) return null;
        var reader = ef.MetadataReader;
        var h = reader.GetFieldDefinition(ef.Handle).GetMarshallingDescriptor();
        if (h.IsNil) return null;
        var br = reader.GetBlobReader(h);
        int native = br.ReadByte() & 0x7F, count = 0, elementNative = 0;
        if (native is NativeFixedArray or NativeFixedSysString)
        {
            if (br.RemainingBytes > 0) count = br.ReadCompressedInteger();
            if (native == NativeFixedArray && br.RemainingBytes > 0) elementNative = br.ReadByte();
        }
        return (native, count, elementNative);
    }

    /// <summary>The element type of a fixed buffer's type: the compiler's `fixed` buffer struct (named `&lt;name&gt;e__FixedBuffer`,
    /// a nested value type of one instance field, of the element type). Null for any other type.</summary>
    static TypeDesc? FixedBufferElement(TypeDesc t)
    {
        if (t is not MetadataType { IsValueType: true, ContainingType: not null } d || !d.GetName().ToString().Contains("e__FixedBuffer", StringComparison.Ordinal)) return null;
        var fields = InstanceFieldsOf(t).ToList();
        return fields.Count == 1 ? fields[0].FieldType : null;
    }

    /// <summary>The kind of a member of this type (a field's, or a fixed array's element): its kind, size and type id.</summary>
    (int Kind, int Size, int TypeId) MarshalMemberKind(TypeDesc ft)
    {
        if (ft.IsString) return (KindString, 4, -1);
        if (ft.IsPointer || ft.IsFunctionPointer) return (KindScalar, 4, -1);
        // (a delegate marshals as a function pointer)
        if (ft.IsDelegate) return (KindDelegate, 4, -1);
        if (ft.IsEnum) return (KindScalar, ((DefType)ft.UnderlyingType).InstanceFieldSize.AsInt, -1);
        switch (ft.Category)
        {
            case TF.Boolean: return (KindBool, 1, -1);
            case TF.Char: return (KindChar, 2, -1);
            case TF.SByte: case TF.Byte: return (KindScalar, 1, -1);
            case TF.Int16: case TF.UInt16: return (KindScalar, 2, -1);
            case TF.Int32: case TF.UInt32: case TF.Single: case TF.IntPtr: case TF.UIntPtr: return (KindScalar, 4, -1);
            case TF.Int64: case TF.UInt64: case TF.Double: return (KindScalar, 8, -1);
        }
        if (ft is DefType { IsValueType: true } vt) return (KindStruct, vt.InstanceFieldSize.AsInt, TypeId(vt));
        return (KindNone, 0, -1);
    }

    /// <summary>The instance fields of a type, a class's inherited ones first (as its layout lists them), each in
    /// declaration order.</summary>
    static IEnumerable<FieldDesc> InstanceFieldsOf(TypeDesc t)
    {
        if (BaseOf(t) is { } b)
            foreach (var f in InstanceFieldsOf(b)) yield return f;
        foreach (var f in ((DefType)t).GetFields()) if (!f.IsStatic) yield return f;
    }

    /// <summary>A type's marshalling record (null when no marshalling call can name it, so the runtime finds none).</summary>
    Blob? MarshalRecord(TypeDesc t, Func<TypeDesc, int> final)
    {
        if (!_marshalWanted.Contains(t) || t is not DefType d) return null;
        int layout = 0, charset = 0, pack = 0, sizeAttr = 0;
        if (d.GetTypeDefinition() is EcmaType et)
        {
            var attrs = (int)et.Attributes;
            layout = (attrs & 0x18) switch { 0x8 => 1, 0x10 => 2, _ => 0 };
            charset = (attrs & 0x30000) switch { 0x10000 => 1, 0x20000 => 2, _ => 0 };
            var cl = ((Internal.TypeSystem.Ecma.EcmaModule)et.Module).MetadataReader.GetTypeDefinition(et.Handle).GetLayout();
            pack = cl.PackingSize;
            sizeAttr = cl.Size;
        }
        // (an explicit layout lists its base's fields flat, at their explicit offsets; any other lays the base out first,
        // as a structure of its own)
        var items = new List<FieldDesc?>();
        if (BaseOf(t) is { } bt)
        {
            if (layout == 2) items.AddRange(InstanceFieldsOf(bt));
            else items.Add(null);
        }
        items.AddRange(((DefType)t).GetFields().Where(f => !f.IsStatic));
        var fieldBytes = new List<byte>();
        var fieldRelocs = new List<(int, IAddress)>();
        foreach (var f in items)
        {
            if (f == null)
            {
                // (the base: a structure of its own, at the start of the layout)
                fieldRelocs.Add((fieldBytes.Count, StringLiteral("")));
                foreach (var w in new[] { 0, 0, KindBase, final(BaseOf(t)!), 0, 0, 0, -1, 0, -1, 0 })
                    fieldBytes.AddRange(BitConverter.GetBytes(w));
                continue;
            }
            var ft = f.FieldType;
            var desc = MarshalDescriptor(f);
            int kind, native = desc?.Native ?? 0, count = desc?.Count ?? 0, size, tid = -1, ekind = 0, enative = 0, etid = -1;
            if (FixedBufferElement(ft) is { } elem)
            {
                // (a fixed buffer, `fixed T name[n]`: its elements are inline in the structure, marshalled by the
                // structure's own charset; the buffer's type is a value type of one field of T, n * sizeof(T) long)
                var ek = MarshalMemberKind(elem);
                kind = ek.Kind is KindBool or KindChar or KindScalar or KindStruct ? KindInline : KindNone;
                size = ek.Size;
                count = size == 0 ? 0 : ((DefType)ft).InstanceFieldSize.AsInt / size;
                ekind = ek.Kind;
                etid = ek.Kind == KindStruct ? final(elem) : -1;
                // (as the desktop runtime does it: a bool buffer marshals its first element only, as one byte; a char
                // buffer in an ANSI structure its first element only, as two bytes; any other char buffer every element)
                native = 0;
                enative = 0;
                if (ek.Kind == KindBool) { native = 1; enative = NativeU1; }
                else if (ek.Kind == KindChar) { native = charset == 0 ? 1 : 0; enative = NativeI2; }
            }
            else if (ft is ArrayType at && desc is { Native: NativeFixedArray })
            {
                var ek = MarshalMemberKind(at.ElementType);
                if (ek.Kind is KindBool or KindChar or KindScalar or KindStruct)
                {
                    kind = KindFixedArray; size = ek.Size; ekind = ek.Kind; etid = ek.TypeId;
                    if (ek.Kind == KindStruct) etid = final(at.ElementType);
                    enative = desc.Value.ElementNative;
                    tid = final(ft);
                }
                else { kind = KindNone; size = 0; }
            }
            else
            {
                var mk = MarshalMemberKind(ft);
                kind = mk.Kind; size = mk.Size;
                if (kind == KindStruct) tid = final(ft);
            }
            // (a private field: a base class's is found by no lookup by name through a derived type, as the desktop's
            // reflection does not find it)
            int flags = f is EcmaField ef && ((int)ef.Attributes & 0x7) == 0x1 ? 1 : 0;
            fieldRelocs.Add((fieldBytes.Count, StringLiteral(f.Name.ToString())));
            foreach (var w in new[] { 0, f.Offset.AsInt, kind, tid, native, size, count, ekind, enative, etid, flags })
                fieldBytes.AddRange(BitConverter.GetBytes(w));
        }
        var header = new List<byte>();
        foreach (var w in new[] { 1, layout, pack, sizeAttr, charset, d.IsValueType ? 0 : IlContext.ObjectHeader, items.Count, 0 })
            header.AddRange(BitConverter.GetBytes(w));
        var fieldArr = DataBlob(fieldBytes.ToArray(), 4, fieldRelocs);
        return DataBlob(header.ToArray(), 4, [(7 * 4, fieldArr)]);
    }
}
