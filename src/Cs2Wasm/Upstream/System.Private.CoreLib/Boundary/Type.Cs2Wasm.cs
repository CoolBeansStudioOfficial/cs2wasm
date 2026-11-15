using Cs2Wasm.Runtime;

namespace System
{
    // cs2wasm: a type at run time is its type id (the compiler numbers the types a program uses). typeof(X) and
    // GetType() give the one RuntimeType of an id; its handle's value is the id.
    public abstract partial class Type
    {
        internal static Type FromId(int id) => RuntimeType.FromId(id);
    }

    internal partial class RuntimeType
    {
        static RuntimeType?[] s_byId = new RuntimeType?[64];
        static int s_lock;

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
                return table[element] ??= new RuntimeType(element | Cs2Wasm.Runtime.Meta.ByRef);
            }
            finally { Atomic.Store32(ref s_lock, 0); }
        }

        private RuntimeType(int id) => _impl = new RuntimeTypeHandle((IntPtr)id);

        internal int TypeId => (int)_impl.Value;

        // by-ref types (Meta.ByRef), by element id
        static RuntimeType?[] s_byRef = [];

        internal static RuntimeType FromId(int id)
        {
            if ((id & Cs2Wasm.Runtime.Meta.ByRef) != 0) return ByRefOf(id & ~Cs2Wasm.Runtime.Meta.ByRef);
            var table = s_byId;
            if ((uint)id < (uint)table.Length && table[id] is { } t) return t;
            while (Atomic.CompareExchange32(ref s_lock, 0, 1) != 0) { }
            try
            {
                table = s_byId;
                if ((uint)id >= (uint)table.Length)
                {
                    var bigger = new RuntimeType?[Math.Max(table.Length * 2, id + 1)];
                    for (int i = 0; i < table.Length; i++) bigger[i] = table[i];
                    s_byId = table = bigger;
                }
                return table[id] ??= new RuntimeType(id);
            }
            finally { Atomic.Store32(ref s_lock, 0); }
        }
    }
}
