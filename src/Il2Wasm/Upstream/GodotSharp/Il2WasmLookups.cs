// il2wasm: the batch form of GodotSharp's method-bind lookup (the manifest's pure-init-batch). A generated class's
// static fields each hold one method bind (MethodBindN = ClassDB_get_method_with_compatibility(NativeName,
// MethodName.X, hash)); when the program reads none of a run of them, the compiler leaves their fields out but still
// looks each one up, in the same order, through this method over tables of their names and hashes, so the same
// lookups are made and a missing method fails as it would in the field's initializer.
using System;
using System.Diagnostics;

namespace Godot
{
    public partial class GodotObject
    {
        [StackTraceHidden]
        internal static unsafe void ClassDB_check_methods_with_compatibility(StringName type, char* names, int* lengths, ulong* hashes, int count)
        {
            for (int i = 0; i < count; i++)
            {
                // (the name made as the initializer makes it: string to StringName)
                StringName method = new string(names, 0, lengths[i]);
                ClassDB_get_method_with_compatibility(type, method, hashes[i]);
                names += lengths[i];
            }
        }
    }
}
