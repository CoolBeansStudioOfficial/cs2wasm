// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// il2wasm: Mono's internal calls are implemented over il2wasm's runtime; those it has no equivalent for yet throw
// NotSupportedException, and those the sandbox lacks are build errors (MissingCapability).

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;

namespace System.Runtime.CompilerServices
{
    public partial class RuntimeHelpers
    {
        public static void InitializeArray(Array array, RuntimeFieldHandle fldHandle)
        {
            ArgumentNullException.ThrowIfNull(array);
            ArgumentNullException.ThrowIfNull(fldHandle.Value, nameof(fldHandle));

            InitializeArray(array, fldHandle.Value);
        }

        private static unsafe ref byte GetSpanDataFrom(
            RuntimeFieldHandle fldHandle,
            RuntimeTypeHandle targetTypeHandle,
            out int count)
        {
            fixed (int *pCount = &count)
            {
                return ref GetSpanDataFrom(fldHandle.Value, targetTypeHandle.Value, new IntPtr(pCount));
            }
        }

        [Obsolete("OffsetToStringData has been deprecated. Use string.GetPinnableReference() instead.")]
        public static int OffsetToStringData => string.OFFSET_TO_STRING;

                private static int InternalGetHashCode(object? o) => o == null ? 0 : Il2Wasm.Runtime.Rt.HashOf(o);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetHashCode(object? o)
        {
            // NOTE: the interpreter does not run this code.  It intrinsifies the whole RuntimeHelpers.GetHashCode function
            if (Threading.ObjectHeader.TryGetHashCode(o, out int hash))
                return hash;
            return InternalGetHashCode(o);
        }

        /// <summary>
        /// If a hash code has been assigned to the object, it is returned. Otherwise zero is
        /// returned.
        /// </summary>
        /// <remarks>
        /// The advantage of this over <see cref="GetHashCode" /> is that it avoids assigning a hash
        /// code to the object if it does not already have one.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int TryGetHashCode(object? o)
        {
            // NOTE: the interpreter does not run this code.  It intrinsifies the whole RuntimeHelpers.TryGetHashCode function
            if (Threading.ObjectHeader.TryGetHashCode(o, out int hash))
                return hash;
            return 0;
        }

        public static new bool Equals(object? o1, object? o2)
        {
            if (o1 == o2)
                return true;

            if (o1 == null || o2 == null)
                return false;

            if (o1 is ValueType)
                return ValueType.DefaultEquals(o1, o2);

            return false;
        }

                [return: NotNullIfNotNull(nameof(obj))]
        public static object? GetObjectValue(object? obj) => obj;

        [RequiresUnreferencedCode("Trimmer can't guarantee existence of class constructor")]
        public static void RunClassConstructor(RuntimeTypeHandle type)
        {
            if (type.Value == IntPtr.Zero)
                throw new ArgumentException(SR.InvalidOperation_HandleIsNotInitialized, nameof(type));

            RunClassConstructor(type.Value);
        }

        public static void EnsureSufficientExecutionStack()
        {
            if (SufficientExecutionStack())
                return;

            throw new InsufficientExecutionStackException();
        }

        public static bool TryEnsureSufficientExecutionStack()
        {
            return SufficientExecutionStack();
        }

        public static void PrepareDelegate(Delegate d)
        {
        }

        public static void PrepareMethod(RuntimeMethodHandle method)
        {
            if (method.IsNullHandle())
                throw new ArgumentException(SR.Argument_InvalidHandle);
            unsafe
            {
                PrepareMethod(method.Value, null, 0);
            }
        }

        public static void PrepareMethod(RuntimeMethodHandle method, RuntimeTypeHandle[]? instantiation)
        {
            if (method.IsNullHandle())
                throw new ArgumentException(SR.Argument_InvalidHandle);
            unsafe
            {
                IntPtr[]? instantiations = RuntimeTypeHandle.CopyRuntimeTypeHandles(instantiation, out int length);
                fixed (IntPtr* pinst = instantiations)
                {
                    PrepareMethod(method.Value, pinst, length);
                    GC.KeepAlive(instantiation);
                }
            }
        }

        public static void RunModuleConstructor(ModuleHandle module)
        {
            if (module == ModuleHandle.EmptyHandle)
                throw new ArgumentException(SR.InvalidOperation_HandleIsNotInitialized, nameof(module));

            RunModuleConstructor(module.Value);
        }

        /// <summary>Allocates memory that's associated with the <paramref name="type" /> and is freed if and when the <see cref="Type" /> is unloaded.</summary>
        /// <param name="type">The type associated with the allocated memory.</param>
        /// <param name="size">The amount of memory to allocate, in bytes.</param>
        /// <returns>The allocated memory.</returns>
        /// <exception cref="ArgumentException"><paramref name="type" /> must be a type provided by the runtime.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="size" /> is negative.</exception>
        public static unsafe IntPtr AllocateTypeAssociatedMemory(Type type, int size)
        {
            if (type is not RuntimeType)
            {
                throw new ArgumentException(SR.Arg_MustBeType, nameof(type));
            }

            ArgumentOutOfRangeException.ThrowIfNegative(size);

            // We don't support unloading; the memory will never be freed.
            return (IntPtr)NativeMemory.AllocZeroed((uint)size);
        }

        /// <summary>Allocates aligned memory that's associated with the <paramref name="type" /> and is freed if and when the <see cref="Type" /> is unloaded.</summary>
        /// <param name="type">The type associated with the allocated memory.</param>
        /// <param name="size">The amount of memory to allocate, in bytes.</param>
        /// <param name="alignment">The alignment, in bytes, of the memory to allocate. This must be a power of <c>2</c>.</param>
        /// <returns>The allocated aligned memory.</returns>
        /// <exception cref="ArgumentException"><paramref name="type" /> must be a type provided by the runtime.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="size" /> is negative.</exception>
        /// <exception cref="ArgumentException"><paramref name="alignment" /> is not a power of <c>2</c>.</exception>
        public static unsafe IntPtr AllocateTypeAssociatedMemory(Type type, int size, int alignment)
        {
            if (type is not RuntimeType)
            {
                throw new ArgumentException(SR.Arg_MustBeType, nameof(type));
            }

            ArgumentOutOfRangeException.ThrowIfNegative(size);

            if (!BitOperations.IsPow2(alignment))
            {
                // The C standard doesn't define what a valid alignment is, however Windows and POSIX implementation requires a power of 2
                ThrowHelper.ThrowArgumentException(ExceptionResource.Argument_AlignmentMustBePow2);
            }

            // We don't support unloading; the memory will never be freed.
            void* result = NativeMemory.AlignedAlloc((uint)size, (uint)alignment);
            NativeMemory.Clear(result, (uint)size);
            return (IntPtr)result;
        }

        [Intrinsic]
        internal static ref byte GetRawData(this object obj) => ref obj.GetRawData();

        [Intrinsic]
        internal static bool IsBitwiseEquatable<T>() => IsBitwiseEquatable<T>();

        [Intrinsic]
        internal static bool ObjectHasComponentSize(object obj) => ObjectHasComponentSize(obj);

        [Intrinsic]
        internal static bool ObjectHasReferences(object obj)
        {
            // TODO: Missing intrinsic in interpreter
            return RuntimeTypeHandle.HasReferences((obj.GetType() as RuntimeType)!);
        }

        public static object GetUninitializedObject(
            // This API doesn't call any constructors, but the type needs to be seen as constructed.
            // A type is seen as constructed if a constructor is kept.
            // This obviously won't cover a type with no constructor. Reference types with no
            // constructor are an academic problem. Valuetypes with no constructors are a problem,
            // but IL Linker currently treats them as always implicitly boxed.
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)]
            Type type)
        {
            if (type is not RuntimeType rt)
            {
                ArgumentNullException.ThrowIfNull(type);

                throw new SerializationException(SR.Format(SR.Serialization_InvalidType, type));
            }

            return GetUninitializedObjectInternal(new RuntimeTypeHandle(rt).Value);
        }


                private static unsafe void PrepareMethod(IntPtr method, IntPtr* instantiations, int ninst) { }

        // il2wasm: a zeroed object (a value type's box) of the type's size; the type must be one the program
        // constructs (its destroy function exists): reflection marks the value types of by-ref parameters it keeps
        private static unsafe object GetUninitializedObjectInternal(IntPtr type)
        {
            int id = (int)type;
            int size = Il2Wasm.Runtime.TypeInfo.Size(id);
            int total = RuntimeType.FromId(id).IsValueType ? Il2Wasm.Runtime.TypeInfo.BoxOffset(id) + size : Math.Max(size, 12);
            int a = Il2Wasm.Runtime.Rt.AllocObject(total, id);
            new Span<byte>((void*)(a + 12), total - 12).Clear();
            return Il2Wasm.Runtime.Rt.Take<object>(a);
        }

                private static void InitializeArray(Array array, IntPtr fldHandle) => throw Il2Wasm.Runtime.TypeInfo.NotYet("InitializeArray");

                private static unsafe ref byte GetSpanDataFrom(
            IntPtr fldHandle,
            IntPtr targetTypeHandle,
            IntPtr count) => throw Il2Wasm.Runtime.TypeInfo.NotYet("GetSpanDataFrom");

                private static void RunClassConstructor(IntPtr type) => throw Il2Wasm.Runtime.TypeInfo.NotYet("RunClassConstructor");

                private static void RunModuleConstructor(IntPtr module) { }

                private static bool SufficientExecutionStack() => true;

        // il2wasm: a box of the value at target (as Array.GetValue boxes an element: Boundary/Arrays.cs)
        private static unsafe object InternalBox(QCallTypeHandle type, ref byte target)
        {
            int id = Il2Wasm.Runtime.TypeInfo.Id(type);
            int addr = (int)Unsafe.AsPointer(ref target);
            if ((Il2Wasm.Runtime.TypeInfo.Flags(id) & 1) == 0) return Il2Wasm.Runtime.Rt.Borrow<object>(Il2Wasm.Runtime.Mem.LoadI32(addr));
            if ((Il2Wasm.Runtime.TypeInfo.Flags2(id) & 1) != 0 || (Il2Wasm.Runtime.TypeInfo.Flags2(id) & 4) != 0 && Il2Wasm.Runtime.TypeInfo.Name(id) == "Nullable`1")
                throw Il2Wasm.Runtime.TypeInfo.NotYet("RuntimeHelpers.Box of a struct with references or of a Nullable");
            int size = Il2Wasm.Runtime.TypeInfo.Size(id), off = Il2Wasm.Runtime.TypeInfo.BoxOffset(id);
            int box = Il2Wasm.Runtime.Rt.AllocObject(off + size, id);
            Il2Wasm.Runtime.Mem.Copy(box + off, addr, size);
            return Il2Wasm.Runtime.Rt.Take<object>(box);
        }

        /// <summary>
        /// Create a boxed object of the specified type from the data located at the target reference.
        /// </summary>
        /// <param name="target">The target data</param>
        /// <param name="type">The type of box to create.</param>
        /// <returns>A boxed object containing the specified data.</returns>
        /// <exception cref="ArgumentNullException">The specified type handle is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">The specified type cannot have a boxed instance of itself created.</exception>
        /// <exception cref="NotSupportedException">The passed in type is a by-ref-like type.</exception>
        /// <remarks>This returns an object that is equivalent to executing the IL box instruction with the provided target address and type.</remarks>
        public static object? Box(ref byte target, RuntimeTypeHandle type)
        {
            if (type.Value is 0)
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.type);

            // Compatibility with CoreCLR, throw on a null reference to the unboxed data.
            if (Unsafe.IsNullRef(ref target))
                throw new NullReferenceException();

            RuntimeType rtType = (RuntimeType)Type.GetTypeFromHandle(type)!;

            if (rtType.ContainsGenericParameters
                || rtType.IsPointer
                || rtType.IsFunctionPointer
                || rtType.IsByRef
                || rtType.IsGenericParameter
                || rtType == typeof(void))
            {
                throw new ArgumentException(SR.Arg_TypeNotSupported);
            }

            if (!rtType.IsValueType)
            {
                return Unsafe.As<byte, object?>(ref target);
            }

            if (rtType.IsByRefLike)
                throw new NotSupportedException(SR.NotSupported_ByRefLike);

            object? result = InternalBox(new QCallTypeHandle(ref rtType), ref target);
            return result;
        }

                private static int SizeOf(QCallTypeHandle handle) => Il2Wasm.Runtime.TypeInfo.Size(Il2Wasm.Runtime.TypeInfo.Id(handle));

        /// <summary>
        /// Get the size of an object of the given type.
        /// </summary>
        /// <param name="type">The type to get the size of.</param>
        /// <returns>The size of instances of the type.</returns>
        /// <exception cref="ArgumentException">The passed-in type is not a valid type to get the size of.</exception>
        /// <remarks>
        /// This API returns the same value as <c>sizeof(T)</c> for the type that <paramref name="type"/> represents.
        /// </remarks>
        public static int SizeOf(RuntimeTypeHandle type)
        {
            if (type.Value == IntPtr.Zero)
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.type);

            Type typeObj = Type.GetTypeFromHandle(type)!;
            if (typeObj.ContainsGenericParameters || typeObj.IsGenericParameter || typeObj == typeof(void))
                throw new ArgumentException(SR.Arg_TypeNotSupported);

            return SizeOf(new QCallTypeHandle(ref type));
        }
    }
}
