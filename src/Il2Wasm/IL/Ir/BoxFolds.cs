#nullable enable
using System;
using Internal.IL;
using Internal.TypeSystem;

namespace Il2Wasm.IL.Ir;

/// <summary>Box folds in the importer, as the JIT and NativeAOT make them (importer.cpp's impBoxPatternMatch): a value
/// type's box that only feeds a test or an unbox is no object at all. Generic code over a value-type T writes these
/// shapes: `(int)(object)left` in GenericArraySortHelper&lt;T&gt;, `x != null` in GenericEqualityComparer&lt;T&gt; and
/// GenericComparer&lt;T&gt;, `value is int i` in pattern matches. Each one was an allocation and a release per
/// comparison (docs/S1-BARS.md, cause 1).
///
/// The rules (each exact, ECMA-335 III.4.1, III.4.3, III.4.6, III.4.33):
///   - box T then unbox.any T (the same type) is the value. For a Nullable&lt;T&gt; too: its box is null or a T's box,
///     and unbox.any Nullable&lt;T&gt; of either gives the Nullable back as it was. Only a box nothing else has seen
///     (made for the unbox, as `(int)(object)x` makes it): a box stored or passed on can be changed through an
///     interface call on it before it is unboxed;
///   - box T compared with null, or branched on, is not null when T is not a Nullable&lt;T&gt; (a Nullable without a
///     value boxes as null);
///   - box T then isinst T or castclass T (the same type, not a Nullable) is the box itself: the cast succeeds.
/// A box a fold consumed is removed when nothing else uses it (DropFolded); otherwise it stays, and the fold only
/// saves the test or the unbox.
///
/// Switches: IL2WASM_NO_BOX_FOLD=1, none of them. Break twin, for the tests: IL2WASM_BOX_FOLD_BREAK=1 takes a
/// Nullable's box for not null and folds unbox.any of a box of another type (a Nullable&lt;T&gt;'s box unboxed as T) and of a box that was stored and changed, all wrong (tests/Il2Wasm.IL.Tests/FrameworkPrograms/f24_box_folds.cs).</summary>
public sealed partial class Importer
{
    internal static readonly bool NoBoxFold = Environment.GetEnvironmentVariable("IL2WASM_NO_BOX_FOLD") == "1";
    internal static readonly bool BoxFoldBreak = Environment.GetEnvironmentVariable("IL2WASM_BOX_FOLD_BREAK") == "1";

    /// <summary>A box of this type is never null: a value type that is not a Nullable.</summary>
    static bool BoxNeverNull(TypeDesc t) => t.IsValueType && (!t.IsNullable || BoxFoldBreak);

    /// <summary>The instruction at the offset is `box T` of a type whose box is never null (LinkBlocks: a branch on it
    /// goes one way).</summary>
    bool NonNullBoxAt(int at)
    {
        if (NoBoxFold) return false;
        var rd = new ILReader(_code, at);
        if (rd.ReadILOpcode() != ILOpcode.box) return false;
        return _il.GetObject(rd.ReadILToken()) is TypeDesc t && BoxNeverNull(t);
    }

    sealed partial class BlockImporter
    {
        static bool IsNonNullBox(Node n) => !NoBoxFold && n.Op == Opc.Box && n.X is TypeDesc t && BoxNeverNull(t);

        /// <summary>unbox.any T of a box of T: the boxed value (null: no fold).</summary>
        Node? UnboxOfBox(Node o, TypeDesc t)
        {
            if (NoBoxFold || o.Op != Opc.Box || o.X is not TypeDesc bt) return null;
            if (bt != t && !(BoxFoldBreak && bt.IsNullable && bt.Instantiation[0] == t)) return null;
            // (the box seen by nothing else: no one can have changed what is in it, through an interface call on it,
            // since it was made. A box stored in a local and unboxed later is a box, NetWasm's fixture-rtti cases)
            if (!Unused(o) && !BoxFoldBreak) return null;
            var v = o.A[0];
            if (bt != t) v = AddOfType(Opc.LdFld, t, v).WithX(((DefType)bt).GetField("value"u8));
            DropFolded(o);
            return v;
        }

        /// <summary>isinst T or castclass T of a box of T: the box (it is a T).</summary>
        static bool CastOfBox(Node o, TypeDesc t) => !NoBoxFold && o.Op == Opc.Box && o.X == t && !t.IsNullable;

        /// <summary>A box that is never null compared with null (ceq, cgt.un, and the branches on it): a constant.</summary>
        Node? CompareBoxWithNull(CmpOp op, Node a, Node b)
        {
            if (op is not (CmpOp.Eq or CmpOp.Ne or CmpOp.GtUn or CmpOp.LtUn)) return null;
            Node box;
            bool nonNullFirst;
            if (IsNonNullBox(a) && b.Op == Opc.LdNull) { box = a; nonNullFirst = true; }
            else if (a.Op == Opc.LdNull && IsNonNullBox(b)) { box = b; nonNullFirst = false; }
            else return null;
            // (box != null, box >un null, null <un box: true; box == null, and the other orders: false)
            bool r = op switch
            {
                CmpOp.Eq => false,
                CmpOp.Ne => true,
                CmpOp.GtUn => nonNullFirst,
                _ => !nonNullFirst,
            };
            var c = Const(VK.I32, r ? 1 : 0);
            DropFolded(box);
            return c;
        }
    }
}
