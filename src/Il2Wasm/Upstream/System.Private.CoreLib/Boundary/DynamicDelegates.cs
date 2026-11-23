// il2wasm: what NativeAOT's runtime gives System.Linq.Expressions when no IL can be emitted (its interpreter makes
// delegates of any type that pack their arguments into an object[] for one handler). The compiler makes such a
// delegate for every delegate type the program names (Rt.ObjectArrayDelegate); the type comes as its id.

using System;

namespace Internal.Runtime.Augments
{
    internal static class DynamicDelegateAugments
    {
        public static Delegate CreateObjectArrayDelegate(Type delegateType, Func<object?[], object?> invoker)
        {
            ArgumentNullException.ThrowIfNull(delegateType);
            ArgumentNullException.ThrowIfNull(invoker);
            if (delegateType is not RuntimeType rt || !rt.IsSubclassOf(typeof(MulticastDelegate)))
                throw new ArgumentException(SR.Arg_MustBeDelegate, nameof(delegateType));
            return Il2Wasm.Runtime.Rt.ObjectArrayDelegate(rt.TypeId, invoker)
                ?? throw new PlatformNotSupportedException(delegateType + " is not a delegate type the program names");
        }
    }
}
