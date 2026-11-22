namespace Cs2Wasm.Runtime
{
    public static partial class Rt
    {
        /// <summary>0; 1 with CS2WASM_NO_ARRAY_MOVE=1 (at compile time): a collection that grows copies its items, as
        /// upstream; 2 with CS2WASM_ARRAY_MOVE_BREAK=1 (to see test 220 fail): the items move whoever else reads the
        /// old array, which is cleared.</summary>
        [Intrinsic] static extern int ArrayMoveMode();

        /// <summary>A collection grows (edits of Dictionary.cs, HashSet.cs and List.cs, the manifest): the n items of
        /// the full array it is about to let go, into the new array it has just made. Where nothing but the
        /// collection's field refers to the old array (its count is one: no method further up the stack and no other
        /// object still reads it), the references move: the items' bytes are copied, and the old array is left an
        /// array of bytes of the same size, so that no item is counted for the new array and counted off again when
        /// the old one goes (two visits of every object the items refer to, which the copy itself never reads: 6 us
        /// of the 29 us frame in which the collections scene's dictionary resizes). The old array's bytes are as they
        /// were, and it is freed where it was before, at the collection's store of the new one. True when done.
        /// False, and nothing done: the items hold no reference (a copy is all there is to do), a program with threads
        /// (another thread may read the old array meanwhile, as .NET lets it), an array anything else refers to (a
        /// method that is still reading the old entries, as when a key's Equals adds to the dictionary it is called
        /// from), a member of a group, an array that is not full (its other places would have to be looked at). The
        /// new array's places are empty: it is new.</summary>
        public static bool MoveRange<T>(T[] from, int fromIndex, T[] to, int toIndex, int n)
        {
            if (!System.Runtime.CompilerServices.RuntimeHelpers.IsReferenceOrContainsReferences<T>()) return false;
            return MoveItems(AddressOf(from), fromIndex, AddressOf(to), toIndex, n);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static bool MoveItems(int from, int fromIndex, int to, int toIndex, int n)
        {
            int mode = ArrayMoveMode();
            if (mode == 1 || ThreadMode() || from == 0 || to == 0 || from == to) return false;
            // (a plain object with one count each; the same array type)
            int held = Mem.LoadI32(from);
            if ((held != 1 && !(mode == 2 && held > 1)) || Mem.LoadI32(to) != 1 || Mem.LoadI32(from + 4) != Mem.LoadI32(to + 4)) return false;
            if (ArrayRankAt(from) != 1) return false;
            int fl = Mem.LoadI32(from + 12), tl = Mem.LoadI32(to + 12);
            if (fromIndex != 0 || n != fl || n <= 0 || toIndex < 0 || toIndex > tl - n) return false;
            int size = ArrayElementSizeAt(from);
            if (fl > 0x7fffffff / size) return false;
            Mem.Copy(to + 16 + toIndex * size, from + 16, n * size);
            if (mode == 2) { Mem.Fill(from + 16, 0, n * size); return true; }
            // (the old array: the same bytes, of a type whose end counts nothing off and frees the same size)
            Mem.StoreI32(from + 4, MarkConstructed<byte[]>());
            Mem.StoreI32(from + 12, fl * size);
            return true;
        }

        /// <summary>A queue grows (an edit of Queue.cs, the manifest): its array is full and its first item is at
        /// `head`, so the items are two stretches, from head to the array's end and from its start to head, which go
        /// to the new array's start one after the other. As MoveRange otherwise: under the same conditions the
        /// references move and the old array is left an array of bytes; false, and nothing done, when the items hold
        /// no reference, with threads, when anything else refers to the old array, or when it is not full (a queue
        /// trimmed or given room by EnsureCapacity copies).</summary>
        public static bool MoveRotated<T>(T[] from, int head, T[] to, int n)
        {
            if (!System.Runtime.CompilerServices.RuntimeHelpers.IsReferenceOrContainsReferences<T>()) return false;
            return MoveItemsRotated(AddressOf(from), head, AddressOf(to), n);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static bool MoveItemsRotated(int from, int head, int to, int n)
        {
            int mode = ArrayMoveMode();
            if (mode == 1 || ThreadMode() || from == 0 || to == 0 || from == to) return false;
            int held = Mem.LoadI32(from);
            if ((held != 1 && !(mode == 2 && held > 1)) || Mem.LoadI32(to) != 1 || Mem.LoadI32(from + 4) != Mem.LoadI32(to + 4)) return false;
            if (ArrayRankAt(from) != 1) return false;
            int fl = Mem.LoadI32(from + 12), tl = Mem.LoadI32(to + 12);
            if (n != fl || n <= 0 || n > tl || (uint)head >= (uint)fl) return false;
            int size = ArrayElementSizeAt(from);
            if (fl > 0x7fffffff / size) return false;
            int first = fl - head;
            Mem.Copy(to + 16, from + 16 + head * size, first * size);
            if (head > 0) Mem.Copy(to + 16 + first * size, from + 16, head * size);
            if (mode == 2) { Mem.Fill(from + 16, 0, n * size); return true; }
            Mem.StoreI32(from + 4, MarkConstructed<byte[]>());
            Mem.StoreI32(from + 12, fl * size);
            return true;
        }
    }
}
