using Il2Wasm.Runtime;

namespace System
{
    // il2wasm: a type at run time is its type id (the compiler numbers the types a program uses). typeof(X) and
    // GetType() give the one RuntimeType of an id; its handle's value is the id.
    public abstract partial class Type
    {
        internal static Type FromId(int id) => RuntimeType.FromId(id);
    }

    internal partial class RuntimeType
    {
        static RuntimeType?[] s_byId = new RuntimeType?[64];
        static int s_lock;

        // ([DeclaredEffects]: FromId and ByRefOf only fill this type's tables of immortal type objects, s_byId, s_byRef and
        // s_retired, which no code outside this file names; a table replaced by a bigger one is kept, and an entry once
        // set is never replaced)
        [Il2Wasm.Runtime.DeclaredEffects]
        internal static RuntimeType ByRefOf(int element)
        {
            while (Atomic.CompareExchange32(ref s_lock, 0, 1) != 0) { }
            try
            {
                var table = s_byRef;
                if ((uint)element >= (uint)table.Length)
                {
                    var bigger = new RuntimeType?[Math.Max(table.Length * 2, element + 1)];
                    for (int i = 0; i < table.Length; i++) bigger[i] = table[i];
                    s_byRef = table = bigger;
                }
                return table[element] ??= Immortal(new RuntimeType(element | Il2Wasm.Runtime.Meta.ByRef));
            }
            finally { Atomic.Store32(ref s_lock, 0); }
        }

        private RuntimeType(int id) => _impl = new RuntimeTypeHandle((IntPtr)id);

        // a type's object lives as long as the program (the table keeps it): its count is made immortal, so the
        // compiler counts no RuntimeType value (Compiler/Types.cs: IsImmortalType) and no retain or release changes it
        static RuntimeType Immortal(RuntimeType t) { Mem.StoreI32(Rt.AddressOf(t), -1); return t; }

        internal int TypeId => (int)_impl.Value;

        // by-ref types (Meta.ByRef), by element id
        static RuntimeType?[] s_byRef = [];

        [Il2Wasm.Runtime.DeclaredEffects]
        internal static RuntimeType FromId(int id)
        {
            if ((id & Il2Wasm.Runtime.Meta.ByRef) != 0) return ByRefOf(id & ~Il2Wasm.Runtime.Meta.ByRef);
            // (the table read where it is, without a count: a table replaced by a bigger one is kept, s_retired, so a
            // thread that read the old one reads valid memory, and its entries are in the new one)
            if ((uint)id < (uint)s_byId.Length && s_byId[id] is { } t) return t;
            return Make(id);
        }

        static RetiredTable? s_retired;
        sealed class RetiredTable(RuntimeType?[] table, RetiredTable? next) { readonly RuntimeType?[] _table = table; readonly RetiredTable? _next = next; }

        static RuntimeType Make(int id)
        {
            while (Atomic.CompareExchange32(ref s_lock, 0, 1) != 0) { }
            try
            {
                var table = s_byId;
                if ((uint)id >= (uint)table.Length)
                {
                    var bigger = new RuntimeType?[Math.Max(table.Length * 2, id + 1)];
                    for (int i = 0; i < table.Length; i++) bigger[i] = table[i];
                    s_retired = new RetiredTable(table, s_retired);
                    s_byId = table = bigger;
                }
                return table[id] ??= Immortal(new RuntimeType(id));
            }
            finally { Atomic.Store32(ref s_lock, 0); }
        }
    }
}
