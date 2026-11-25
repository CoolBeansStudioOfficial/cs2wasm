// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// il2wasm: Mono's internal calls are implemented over il2wasm's runtime; those it has no equivalent for yet throw
// NotSupportedException, and those the sandbox lacks are build errors (MissingCapability).

using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace System.Runtime.InteropServices
{
    public partial class Marshal
    {
        [ThreadStatic] private static int t_lastPInvokeError;

        /// <summary>
        /// Get the last platform invoke error on the current thread
        /// </summary>
        /// <returns>The last platform invoke error</returns>
        /// <remarks>
        /// The last platform invoke error corresponds to the error set by either the most recent platform
        /// invoke that was configured to set the last error or a call to <see cref="SetLastPInvokeError(int)" />.
        /// </remarks>
                public static int GetLastPInvokeError() => t_lastPInvokeError;

        /// <summary>
        /// Set the last platform invoke error on the current thread
        /// </summary>
        /// <param name="error">Error to set</param>
                public static void SetLastPInvokeError(int error) { t_lastPInvokeError = error; }

        [RequiresDynamicCode("Marshalling code for the object might not be available. Use the DestroyStructure<T> overload instead.")]
                [EditorBrowsable(EditorBrowsableState.Never)]
        public static void DestroyStructure(IntPtr ptr, Type structuretype)
        {
            ArgumentNullException.ThrowIfNull(structuretype);
            if (structuretype is not RuntimeType rt) throw new ArgumentException(SR.Argument_MustBeRuntimeType, nameof(structuretype));
            MarshalLayout.Destroy(ptr.ToInt32(), MarshalLayout.RecordOrThrow(rt.TypeId));
        }

                [EditorBrowsable(EditorBrowsableState.Never)]
        public static IntPtr OffsetOf(Type t, string fieldName)
        {
            ArgumentNullException.ThrowIfNull(t);
            ArgumentNullException.ThrowIfNull(fieldName);
            if (t is not RuntimeType rt) throw new ArgumentException(SR.Argument_MustBeRuntimeType, nameof(t));
            return new IntPtr(MarshalLayout.OffsetOf(MarshalLayout.RecordOrThrow(rt.TypeId), fieldName, rt.FullName!));
        }

        [RequiresDynamicCode("Marshalling code for the object might not be available. Use the StructureToPtr<T> overload instead.")]
                [EditorBrowsable(EditorBrowsableState.Never)]
        public static void StructureToPtr(object structure, IntPtr ptr, bool fDeleteOld)
        {
            ArgumentNullException.ThrowIfNull(structure);
            int id = Il2Wasm.Runtime.Rt.TypeId(structure);
            int rec = MarshalLayout.RecordOrThrow(id);
            if (fDeleteOld) MarshalLayout.Destroy(ptr.ToInt32(), rec);
            MarshalLayout.ToNative(Il2Wasm.Runtime.Rt.AddressOf(structure) + Il2Wasm.Runtime.TypeInfo.BoxOffset(id), rec, ptr.ToInt32());
        }

        internal static bool IsPinnable(object? obj)
        {
            if (obj == null || obj is string)
                return true;
            var type = (obj.GetType() as RuntimeType)!;
            return !RuntimeTypeHandle.HasReferences(type);
        }

        private static void PrelinkCore(MethodInfo m)
        {
            if (!(m is RuntimeMethodInfo))
            {
                throw new ArgumentException(SR.Argument_MustBeRuntimeMethodInfo, nameof(m));
            }

            PrelinkInternal(m);
        }

        private static void PtrToStructureHelper(IntPtr ptr, object structure, bool allowValueClasses)
        {
            int id = Il2Wasm.Runtime.Rt.TypeId(structure);
            if (!allowValueClasses && Il2Wasm.Runtime.TypeInfo.Type(id).IsValueType) throw new ArgumentException(SR.Argument_StructMustNotBeValueClass, nameof(structure));
            MarshalLayout.FromNative(ptr.ToInt32(), MarshalLayout.RecordOrThrow(id), Il2Wasm.Runtime.Rt.AddressOf(structure) + Il2Wasm.Runtime.TypeInfo.BoxOffset(id));
        }

                [Il2Wasm.Runtime.MissingCapability("platform invoke")]        private static extern void GetDelegateForFunctionPointerInternal(QCallTypeHandle t, IntPtr ptr, ObjectHandleOnStack res);

                [Il2Wasm.Runtime.MissingCapability("platform invoke")]        private static extern IntPtr GetFunctionPointerForDelegateInternal(Delegate d);

        private static Delegate GetDelegateForFunctionPointerInternal(IntPtr ptr, RuntimeType t)
        {
            Delegate? res = null;
            GetDelegateForFunctionPointerInternal(new QCallTypeHandle(ref t), ptr, ObjectHandleOnStack.Create(ref res));
            return res!;
        }

                [Il2Wasm.Runtime.MissingCapability("platform invoke")]        private static extern void PrelinkInternal(MethodInfo m);

                // il2wasm: a value type's size in memory, which is its size as other code sees it (its layout is the declared one);
        // bool and char have their sizes as C has them
        private static int SizeOfHelper(QCallTypeHandle t, bool throwIfNotMarshalable)
        {
            int id = Il2Wasm.Runtime.TypeInfo.Id(t);
            // (a structure the program's marshalling calls can name has its record: its native layout, from its fields)
            int rec = MarshalLayout.Record(id);
            if (rec != 0) return MarshalLayout.Layout(rec, null).Size;
            if (id == Il2Wasm.Runtime.Rt.TypeId((object)true)) return 4;
            if (id == Il2Wasm.Runtime.Rt.TypeId((object)'a')) return 1;
            // (a pointer or function pointer type: a pointer's size)
            if ((Il2Wasm.Runtime.TypeInfo.Flags2(id) & 32) != 0 || Il2Wasm.Runtime.TypeInfo.CorElementType(id) == 0x1B) return IntPtr.Size;
            if ((Il2Wasm.Runtime.TypeInfo.Flags(id) & 1) != 0) return Il2Wasm.Runtime.TypeInfo.Size(id);
            if (throwIfNotMarshalable) throw new ArgumentException("Type cannot be marshaled as an unmanaged structure; no meaningful size or offset can be computed.");
            return 4;
        }

        private static int SizeOfHelper(RuntimeType rttype, bool throwIfNotMarshalable)
        {
            return SizeOfHelper(new QCallTypeHandle(ref rttype), throwIfNotMarshalable);
        }

        public static IntPtr GetExceptionPointers()
        {
            throw new PlatformNotSupportedException();
        }

        private sealed class MarshalerInstanceKeyComparer : IEqualityComparer<(Type, string)>
        {
            public bool Equals((Type, string) lhs, (Type, string) rhs)
            {
                return lhs.CompareTo(rhs) == 0;
            }

            public int GetHashCode((Type, string) key)
            {
                return key.GetHashCode();
            }
        }

        private static Dictionary<(Type, string), ICustomMarshaler>? MarshalerInstanceCache;

#pragma warning disable 9080
        private static unsafe void SetInvokeArgs(ref string cookie, IntPtr *params_byref)
        {
            ByReference objRef = ByReference.Create(ref cookie);
            *(ByReference*)params_byref = objRef;
        }
#pragma warning restore 9080

        [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2070:UnrecognizedReflectionPattern",
            Justification = "Implementation detail of MarshalAs.CustomMarshaler")]
        internal static unsafe ICustomMarshaler? GetCustomMarshalerInstance(Type type, string cookie)
        {
            var key = (type, cookie);

            Dictionary<(Type, string), ICustomMarshaler> cache =
                Volatile.Read(ref MarshalerInstanceCache) ??
                Interlocked.CompareExchange(ref MarshalerInstanceCache, new Dictionary<(Type, string), ICustomMarshaler>(new MarshalerInstanceKeyComparer()), null) ??
                MarshalerInstanceCache;

            ICustomMarshaler? result;
            bool gotExistingInstance;
            lock (cache)
                gotExistingInstance = cache.TryGetValue(key, out result);

            if (!gotExistingInstance)
            {
                RuntimeMethodInfo? getInstanceMethod;
                try
                {
                    getInstanceMethod = (RuntimeMethodInfo?)type.GetMethod(
                        "GetInstance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.InvokeMethod,
                        null, new Type[] { typeof(string) }, null
                    );
                }
                catch (AmbiguousMatchException)
                {
                    throw new ApplicationException(SR.Format(SR.CustomMarshaler_MultipleGetInstanceMethods, type.FullName));
                }

                if ((getInstanceMethod == null) ||
                    (getInstanceMethod.ReturnType != typeof(ICustomMarshaler)))
                {
                    throw new ApplicationException(SR.Format(SR.CustomMarshaler_NoGetInstanceMethod, type.FullName));
                }

                if (getInstanceMethod.ContainsGenericParameters)
                {
                    throw new System.TypeLoadException(SR.Format(SR.CustomMarshaler_UnassignedGenericParams, type.FullName));
                }

                Exception? exc;
                try
                {
                    IntPtr byrefStorage = default;
                    IntPtr *pbyrefStorage = &byrefStorage;
                    SetInvokeArgs(ref cookie, pbyrefStorage);
                    result = (ICustomMarshaler?)getInstanceMethod.InternalInvoke(null, pbyrefStorage, out exc);
                }
                catch (Exception e)
                {
                    // FIXME: mscorlib's legacyUnhandledExceptionPolicy is apparently 1,
                    //  so exceptions are thrown instead of being passed through the outparam
                    exc = e;
                    result = null;
                }

                if (exc != null)
                {
                    var edi = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exc);
                    edi.Throw();
                }

                if (result == null)
                    throw new ApplicationException(SR.Format(SR.CustomMarshaler_NullReturnForGetInstance, type.FullName));

                lock (cache)
                    cache[key] = result;
            }

            return result;
        }

        #region PlatformNotSupported

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("GetExceptionCode() may be unavailable in future releases.")]
        public static int GetExceptionCode()
        {
            throw new PlatformNotSupportedException();
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("ReadByte(Object, Int32) may be unavailable in future releases.")]
        [RequiresDynamicCode("Marshalling code for the object might not be available")]
        public static byte ReadByte(object ptr, int ofs)
        {
            throw new PlatformNotSupportedException();
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("ReadInt16(Object, Int32) may be unavailable in future releases.")]
        [RequiresDynamicCode("Marshalling code for the object might not be available")]
        public static short ReadInt16(object ptr, int ofs)
        {
            throw new PlatformNotSupportedException();
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("ReadInt32(Object, Int32) may be unavailable in future releases.")]
        [RequiresDynamicCode("Marshalling code for the object might not be available")]
        public static int ReadInt32(object ptr, int ofs)
        {
            throw new PlatformNotSupportedException();
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("ReadInt64(Object, Int32) may be unavailable in future releases.")]
        [RequiresDynamicCode("Marshalling code for the object might not be available")]
        public static long ReadInt64(object ptr, int ofs)
        {
            throw new PlatformNotSupportedException();
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("WriteByte(Object, Int32, Byte) may be unavailable in future releases.")]
        [RequiresDynamicCode("Marshalling code for the object might not be available")]
        public static void WriteByte(object ptr, int ofs, byte val)
        {
            throw new PlatformNotSupportedException();
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("WriteInt16(Object, Int32, Int16) may be unavailable in future releases.")]
        [RequiresDynamicCode("Marshalling code for the object might not be available")]
        public static void WriteInt16(object ptr, int ofs, short val)
        {
            throw new PlatformNotSupportedException();
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("WriteInt32(Object, Int32, Int32) may be unavailable in future releases.")]
        [RequiresDynamicCode("Marshalling code for the object might not be available")]
        public static void WriteInt32(object ptr, int ofs, int val)
        {
            throw new PlatformNotSupportedException();
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("WriteInt64(Object, Int32, Int64) may be unavailable in future releases.")]
        [RequiresDynamicCode("Marshalling code for the object might not be available")]
        public static void WriteInt64(object ptr, int ofs, long val)
        {
            throw new PlatformNotSupportedException();
        }

        #endregion
    }

    /// <summary>il2wasm: the native layout of a marshalled type, from its marshalling record (the compiler's
    /// IlCompiler.MarshalRecord: the fields in declaration order with their managed offsets, kinds and MarshalAs native
    /// types). Sequential and explicit layouts, Pack and Size, bool (BOOL, 4 bytes, unless a native type says otherwise),
    /// char (1 byte ANSI, 2 otherwise), strings (pointers, or inline for a fixed string), fixed arrays (ByValArray),
    /// nested structures, scalars.</summary>
    internal static unsafe class MarshalLayout
    {
        const string NoLayoutMessage = "Type cannot be marshaled as an unmanaged structure; no meaningful size or offset can be computed.";
        const string ArrayLengthMessage = "Type could not be marshaled because the length of an embedded array instance does not match the declared length in the layout.";
        const int FieldWords = 11;
        const int KBool = 1, KChar = 2, KScalar = 3, KStruct = 4, KString = 5, KFixedArray = 6, KInline = 8, KBase = 9, KDelegate = 10;
        const int LayoutSequential = 1, LayoutExplicit = 2;
        const int CharsetAnsi = 0;
        const int NativeBool = 0x02, NativeI1 = 0x03, NativeU1 = 0x04, NativeI2 = 0x05, NativeU2 = 0x06, NativeI4 = 0x07, NativeU4 = 0x08,
            NativeI8 = 0x09, NativeU8 = 0x0A, NativeR4 = 0x0B, NativeR8 = 0x0C, NativeBStr = 0x13, NativeLPStr = 0x14, NativeLPWStr = 0x15,
            NativeLPTStr = 0x16, NativeFixedSysString = 0x17, NativeFixedArray = 0x1E, NativeSysInt = 0x1F, NativeSysUInt = 0x20,
            NativeVariantBool = 0x25, NativeAnsiBStr = 0x23, NativeTBStr = 0x24;

        /// <summary>The record of a type id (0 when the program's marshalling calls cannot name it).</summary>
        public static int Record(int id) => Il2Wasm.Runtime.TypeInfo.Marshaling(id);

        public static int RecordOrThrow(int id)
        {
            int rec = Record(id);
            if (rec == 0) throw new ArgumentException(NoLayoutMessage);
            return rec;
        }

        static int W(int addr, int word) => Il2Wasm.Runtime.Mem.LoadI32(addr + 4 * word);
        static int FieldAt(int rec, int i) => W(rec, 7) + 4 * FieldWords * i;
        static int FieldCount(int rec) => W(rec, 6);
        static int Align(int x, int a) => x % a == 0 ? x : x + a - x % a;

        /// <summary>A member's native size and alignment (a value of kind `kind`, its managed size `msize`, a fixed array's
        /// `count`, its native type, and the charset of its structure).</summary>
        static (int Size, int Align) Member(int kind, int tid, int native, int msize, int count, int ekind, int enative, int etid, int charset)
        {
            switch (kind)
            {
                case KBool:
                {
                    int s = native switch { NativeI1 or NativeU1 => 1, NativeI2 or NativeU2 or NativeVariantBool => 2, _ => 4 };
                    return (s, s);
                }
                case KChar:
                {
                    int s = native switch { NativeI1 or NativeU1 => 1, NativeI2 or NativeU2 => 2, _ => charset == CharsetAnsi ? 1 : 2 };
                    return (s, s);
                }
                case KDelegate:
                    return (4, 4);
                case KScalar:
                {
                    int s = native switch
                    {
                        NativeI1 or NativeU1 => 1, NativeI2 or NativeU2 => 2, NativeI4 or NativeU4 or NativeR4 or NativeSysInt or NativeSysUInt => 4,
                        NativeI8 or NativeU8 or NativeR8 => 8, _ => msize,
                    };
                    return (s, s);
                }
                case KStruct:
                case KBase:
                {
                    int rec = Record(tid);
                    if (rec == 0) throw new ArgumentException(NoLayoutMessage);
                    var l = Layout(rec, null);
                    return (l.Size, l.Align);
                }
                case KString:
                    if (native == NativeFixedSysString) return (count * (charset == CharsetAnsi ? 1 : 2), 1);
                    return (4, 4);
                case KFixedArray:
                case KInline:
                {
                    var e = Member(ekind, etid, enative, msize, 0, 0, 0, -1, charset);
                    return (count * e.Size, e.Align);
                }
            }
            throw new ArgumentException(NoLayoutMessage);
        }

        /// <summary>A structure's native size and alignment; the native offset of each field into `offsets` when given.</summary>
        public static (int Size, int Align) Layout(int rec, int[]? offsets)
        {
            int layout = W(rec, 1), pack = W(rec, 2), sizeAttr = W(rec, 3), charset = W(rec, 4), n = FieldCount(rec);
            int effPack = pack == 0 ? 8 : pack;
            int off = 0, maxAlign = 1, end = 0;
            for (int i = 0; i < n; i++)
            {
                int f = FieldAt(rec, i);
                var m = Member(W(f, 2), W(f, 3), W(f, 4), W(f, 5), W(f, 6), W(f, 7), W(f, 8), W(f, 9), charset);
                int a = Math.Min(m.Align, effPack);
                if (a < 1) a = 1;
                if (layout == LayoutExplicit) off = W(f, 1) - W(rec, 5);
                else off = Align(off, a);
                if (offsets != null) offsets[i] = off;
                off += m.Size;
                if (layout == LayoutExplicit) end = Math.Max(end, off);
                maxAlign = Math.Max(maxAlign, a);
            }
            int size = layout == LayoutExplicit ? end : Align(off, maxAlign);
            if (sizeAttr > size) size = sizeAttr;
            return (size, maxAlign);
        }

        public static int OffsetOf(int rec, string fieldName, string typeName)
        {
            if (TryOffset(rec, fieldName, false, out int off)) return off;
            throw new ArgumentException("Field passed in is not a marshaled member of the type '" + typeName + "'.", nameof(fieldName));
        }

        /// <summary>A field's native offset by name: a class's inherited fields are found too, but not its private ones.</summary>
        static bool TryOffset(int rec, string name, bool inherited, out int off)
        {
            int n = FieldCount(rec);
            var offs = new int[n];
            Layout(rec, offs);
            for (int i = 0; i < n; i++)
            {
                int f = FieldAt(rec, i);
                if (W(f, 2) == KBase)
                {
                    if (TryOffset(Record(W(f, 3)), name, true, out int inner)) { off = offs[i] + inner; return true; }
                    continue;
                }
                if (inherited && (W(f, 10) & 1) != 0) continue;
                if (Il2Wasm.Runtime.Meta.S(W(f, 0)) == name) { off = offs[i]; return true; }
            }
            off = 0;
            return false;
        }

        static void Put(int addr, int size, int v)
        {
            switch (size)
            {
                case 1: Il2Wasm.Runtime.Mem.StoreU8(addr, v); break;
                case 2: Il2Wasm.Runtime.Mem.StoreU16(addr, v); break;
                case 4: Il2Wasm.Runtime.Mem.StoreI32(addr, v); break;
                case 8: Il2Wasm.Runtime.Mem.StoreI64(addr, v); break;
            }
        }

        static int Get(int addr, int size) => size switch
        {
            1 => Il2Wasm.Runtime.Mem.LoadU8(addr),
            2 => Il2Wasm.Runtime.Mem.LoadU16(addr),
            _ => Il2Wasm.Runtime.Mem.LoadI32(addr),
        };

        /// <summary>A scalar's bytes, little-endian: copied, truncated or zero-extended to the destination's size.</summary>
        static void CopyBits(int dst, int dstSize, int src, int srcSize)
        {
            int n = Math.Min(dstSize, srcSize);
            Il2Wasm.Runtime.Mem.Copy(dst, src, n);
            if (dstSize > n) Il2Wasm.Runtime.Mem.Fill(dst + n, 0, dstSize - n);
        }

        static bool IsUnicode(int native, int charset) => native switch
        {
            NativeLPWStr or NativeBStr or NativeLPTStr or NativeTBStr => true,
            NativeLPStr or NativeAnsiBStr => false,
            _ => charset != CharsetAnsi,
        };

        /// <summary>Writes the managed value at `ms` (of a member kind) into native memory at `nd`.</summary>
        static void ValueToNative(int kind, int tid, int native, int msize, int charset, int ms, int nd)
        {
            int size = Member(kind, tid, native, msize, 0, 0, 0, -1, charset).Size;
            switch (kind)
            {
                case KBool: Put(nd, size, Il2Wasm.Runtime.Mem.LoadU8(ms) != 0 ? 1 : 0); break;
                case KChar: Put(nd, size, Il2Wasm.Runtime.Mem.LoadU16(ms)); break;
                case KScalar: CopyBits(nd, size, ms, msize); break;
                case KStruct: ToNativeFields(ms, Record(tid), nd); break;
                default: throw new ArgumentException(NoLayoutMessage);
            }
        }

        /// <summary>Reads the native value at `ns` (of a member kind) into managed memory at `md`.</summary>
        [Il2Wasm.Runtime.RawStores("fields")]
        static void ValueFromNative(int kind, int tid, int native, int msize, int charset, int ns, int md)
        {
            int size = Member(kind, tid, native, msize, 0, 0, 0, -1, charset).Size;
            switch (kind)
            {
                case KBool: Il2Wasm.Runtime.Mem.StoreU8(md, Get(ns, size) != 0 ? 1 : 0); break;
                // (a one-byte char decodes as UTF-8 does: a byte past ASCII is U+FFFD)
                case KChar: Il2Wasm.Runtime.Mem.StoreU16(md, size == 1 ? (Get(ns, 1) < 0x80 ? Get(ns, 1) : 0xFFFD) : Get(ns, size)); break;
                case KScalar: CopyBits(md, msize, ns, size); break;
                case KStruct: FromNative(ns, Record(tid), md); break;
                default: throw new ArgumentException(NoLayoutMessage);
            }
        }

        /// <summary>The managed structure at `src` (its data, fields at their managed offsets) into native memory at `dst`.</summary>
        public static void ToNative(int src, int rec, int dst)
        {
            // (the structure's bytes are written whole: its padding is zero, as the desktop runtime leaves it)
            Il2Wasm.Runtime.Mem.Fill(dst, 0, Layout(rec, null).Size);
            ToNativeFields(src, rec, dst);
        }

        static void ToNativeFields(int src, int rec, int dst)
        {
            int charset = W(rec, 4), n = FieldCount(rec);
            var offs = new int[n];
            Layout(rec, offs);
            for (int i = 0; i < n; i++)
            {
                int f = FieldAt(rec, i);
                int kind = W(f, 2), tid = W(f, 3), native = W(f, 4), msize = W(f, 5), count = W(f, 6);
                int ms = src + W(f, 1), nd = dst + offs[i];
                switch (kind)
                {
                    case KString:
                        if (native == NativeFixedSysString)
                        {
                            int cs = charset == CharsetAnsi ? 1 : 2;
                            string? s = StringAt(ms);
                            for (int k = 0; k < count; k++)
                            {
                                int c = s != null && k < s.Length ? s[k] : 0;
                                Put(nd + k * cs, cs, c);
                            }
                        }
                        else
                        {
                            string? s = StringAt(ms);
                            Put(nd, 4, s == null ? 0 : IsUnicode(native, charset) ? Marshal.StringToHGlobalUni(s).ToInt32() : Marshal.StringToHGlobalAnsi(s).ToInt32());
                        }
                        break;
                    case KFixedArray:
                    {
                        int esize = Member(W(f, 7), W(f, 9), W(f, 8), msize, 0, 0, 0, -1, charset).Size;
                        int arr = Il2Wasm.Runtime.Mem.LoadI32(ms);
                        if (arr == 0) { Il2Wasm.Runtime.Mem.Fill(nd, 0, count * esize); break; }
                        int len = Il2Wasm.Runtime.Rt.ArrayLength(Il2Wasm.Runtime.Rt.Borrow<object>(arr));
                        if (len < count) throw new ArgumentException(ArrayLengthMessage);
                        int data = arr + 16;
                        for (int k = 0; k < count; k++) ValueToNative(W(f, 7), W(f, 9), W(f, 8), msize, charset, data + k * msize, nd + k * esize);
                        break;
                    }
                    case KInline:
                    {
                        // (native 1: the first element only; the rest of the buffer is left zero)
                        int esize = Member(W(f, 7), W(f, 9), W(f, 8), msize, 0, 0, 0, -1, charset).Size;
                        int take = native == 1 ? Math.Min(1, count) : count;
                        for (int k = 0; k < take; k++) ValueToNative(W(f, 7), W(f, 9), W(f, 8), msize, charset, ms + k * msize, nd + k * esize);
                        break;
                    }
                    case KBase:
                        ToNativeFields(ms, Record(tid), nd);
                        break;
                    case KDelegate:
                        // (a delegate's function pointer: none is made here; a null delegate is a null pointer)
                        if (Il2Wasm.Runtime.Mem.LoadI32(ms) != 0) throw new PlatformNotSupportedException();
                        Put(nd, 4, 0);
                        break;
                    default:
                        ValueToNative(kind, tid, native, msize, charset, ms, nd);
                        break;
                }
            }
        }

        static string? StringAt(int refAddr)
        {
            int s = Il2Wasm.Runtime.Mem.LoadI32(refAddr);
            return s == 0 ? null : Il2Wasm.Runtime.Rt.Borrow<string>(s);
        }

        /// <summary>Native memory at `src` into the managed structure at `dst`. Strings and arrays are made new.</summary>
        [Il2Wasm.Runtime.RawStores("fields")]
        public static void FromNative(int src, int rec, int dst)
        {
            int charset = W(rec, 4), n = FieldCount(rec);
            var offs = new int[n];
            Layout(rec, offs);
            for (int i = 0; i < n; i++)
            {
                int f = FieldAt(rec, i);
                int kind = W(f, 2), tid = W(f, 3), native = W(f, 4), msize = W(f, 5), count = W(f, 6);
                int md = dst + W(f, 1), ns = src + offs[i];
                switch (kind)
                {
                    case KString:
                    {
                        string s;
                        if (native == NativeFixedSysString)
                        {
                            int cs = charset == CharsetAnsi ? 1 : 2;
                            var chars = new char[count];
                            int len = 0;
                            while (len < count && Get(ns + len * cs, cs) != 0) { chars[len] = (char)Get(ns + len * cs, cs); len++; }
                            s = new string(chars, 0, len);
                        }
                        else
                        {
                            int p = Get(ns, 4);
                            if (p == 0) { Unsafe.AsRef<object?>((void*)md) = null; break; }
                            s = IsUnicode(native, charset) ? Marshal.PtrToStringUni(new IntPtr(p))! : Marshal.PtrToStringAnsi(new IntPtr(p))!;
                        }
                        Unsafe.AsRef<object?>((void*)md) = s;
                        break;
                    }
                    case KFixedArray:
                    {
                        int esize = Member(W(f, 7), W(f, 9), W(f, 8), msize, 0, 0, 0, -1, charset).Size;
                        object arr = Il2Wasm.Runtime.Rt.Take<object>(Il2Wasm.Runtime.Rt.AllocArray(count, msize, tid));
                        Unsafe.AsRef<object?>((void*)md) = arr;
                        int data = Il2Wasm.Runtime.Rt.AddressOf(arr) + 16;
                        for (int k = 0; k < count; k++) ValueFromNative(W(f, 7), W(f, 9), W(f, 8), msize, charset, ns + k * esize, data + k * msize);
                        break;
                    }
                    case KInline:
                    {
                        int esize = Member(W(f, 7), W(f, 9), W(f, 8), msize, 0, 0, 0, -1, charset).Size;
                        int take = native == 1 ? Math.Min(1, count) : count;
                        for (int k = 0; k < take; k++) ValueFromNative(W(f, 7), W(f, 9), W(f, 8), msize, charset, ns + k * esize, md + k * msize);
                        for (int k = take; k < count; k++) Il2Wasm.Runtime.Mem.Fill(md + k * msize, 0, msize);
                        break;
                    }
                    case KBase:
                        FromNative(ns, Record(tid), md);
                        break;
                    case KDelegate:
                        if (Get(ns, 4) != 0) throw new PlatformNotSupportedException();
                        Unsafe.AsRef<object?>((void*)md) = null;
                        break;
                    default:
                        ValueFromNative(kind, tid, native, msize, charset, ns, md);
                        break;
                }
            }
        }

        /// <summary>DestroyStructure: frees the native strings a structure's fields point to (and its nested structures').</summary>
        public static void Destroy(int nat, int rec)
        {
            int n = FieldCount(rec);
            var offs = new int[n];
            Layout(rec, offs);
            for (int i = 0; i < n; i++)
            {
                int f = FieldAt(rec, i);
                int kind = W(f, 2), native = W(f, 4);
                if (kind == KString && native != NativeFixedSysString)
                {
                    int p = Get(nat + offs[i], 4);
                    if (p != 0) Marshal.FreeHGlobal(new IntPtr(p));
                }
                else if (kind is KStruct or KBase) Destroy(nat + offs[i], Record(W(f, 3)));
            }
        }
    }
}
