#nullable enable
using System;
using Internal.TypeSystem;

namespace Il2Wasm.IL.Ir;

/// <summary>A field of a struct read where the struct is, with no copy of it (the old front end's struct field reads
/// through the address; the JIT's and NativeAOT's importers do the same). IL reads a field of a struct value with
/// `ldarg a; ldfld X` or `ldloc v; ldfld X`: the value is a copy of the whole struct in the frame (a struct value is
/// one, VK.Struct), and the field is read from the copy. In `Vec3 operator+(Vec3 a, Vec3 b) => new(a.X + b.X, ...)`
/// that is six copies of 24 bytes for six reads of 8 (docs/S1-BARS.md, the NBody profile).
///
/// When the instruction that made the struct value is the one just before the ldfld (nothing in between can write
/// the struct's storage), and nothing else uses the value (it is not on the stack again, nor in a variable), the
/// value's own operation becomes the address of the same storage, and the ldfld reads through it:
///   - ldarg/ldloc of a struct variable (VarLd): the variable's address (LocA);
///   - ldfld of a struct field (LdFld): the field's address (LdFldA; its null check is ldfld's);
///   - ldelem of a struct element (LdElem): the element's address (LdElemA; the same index check; no exact-type check,
///     the element type is a value type);
///   - ldsfld of a struct static (LdSFld): the static's address (LdSFldA; the same initialization check);
///   - ldobj (LdObj): its address operand.
/// A chain (`a.B.X`) becomes a chain of addresses. Only structs with no references in them: such a struct's copy
/// holds no counts, so nothing about the memory model changes.
///
/// Switch: IL2WASM_NO_STRUCT_IN_PLACE=1 (every field read from a copy, as before).</summary>
public sealed partial class Importer
{
    internal static readonly bool NoStructInPlace = Environment.GetEnvironmentVariable("IL2WASM_NO_STRUCT_IN_PLACE") == "1";

    sealed partial class BlockImporter
    {
        /// <summary>The base for an ldfld: a struct value just made for it, turned into the address it was read
        /// from; otherwise the value itself.</summary>
        Node InPlace(Node o)
        {
            if (NoStructInPlace || o.Kind != VK.Struct || o.Type == null || Kinds.Counted(o.Type)) return o;
            if (o.Block != B || B.Code.Count == 0 || B.Code[^1] != o || S.Contains(o)) return o;
            if (I._cur.TryGetValue(B, out var vars) && vars.ContainsValue(o)) return o;
            var byref = o.Type.MakeByRefType();
            switch (o.Op)
            {
                case Opc.VarLd: o.Op = Opc.LocA; break;
                case Opc.LdFld: o.Op = Opc.LdFldA; break;
                case Opc.LdElem: o.Op = Opc.LdElemA; o.Ovf = false; break;
                case Opc.LdSFld: o.Op = Opc.LdSFldA; break;
                case Opc.LdObj:
                    B.Code.RemoveAt(B.Code.Count - 1);
                    return o.A[0];
                default: return o;
            }
            o.Kind = VK.Ptr; o.SType = StackType.ByRef; o.Type = byref;
            return o;
        }
    }
}
