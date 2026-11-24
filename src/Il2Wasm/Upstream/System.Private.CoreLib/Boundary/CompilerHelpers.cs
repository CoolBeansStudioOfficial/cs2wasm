// il2wasm: the throw helpers that IL the compiler makes calls (NativeAOT's type system's UnsafeAccessors.TryGetIL,
// which the IL front end uses for [UnsafeAccessor] bodies, names them in this namespace, as NativeAOT's CoreLib
// has them; upstream's half of this class has the rest, ThrowNotSupportedException among them). The id is the compiler's ExceptionStringID; the exception type is what .NET throws.

using System;

namespace Internal.Runtime.CompilerHelpers
{
    internal static partial class ThrowHelpers
    {
        internal static void ThrowMissingMethodException(int id, string methodName) => throw new MissingMethodException("Method not found: '" + methodName + "'.");
        internal static void ThrowMissingFieldException(int id, string fieldName) => throw new MissingFieldException("Field not found: '" + fieldName + "'.");
        internal static void ThrowAmbiguousMatchException(int id) => throw new System.Reflection.AmbiguousMatchException();
        internal static void ThrowInvalidProgramException(int id) => throw new InvalidProgramException();
        internal static void ThrowBadImageFormatException(int id) => throw new BadImageFormatException();
        internal static void ThrowUnavailableType() => throw new TypeLoadException();
    }
}
