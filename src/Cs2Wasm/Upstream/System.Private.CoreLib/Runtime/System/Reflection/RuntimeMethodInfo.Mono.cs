// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// cs2wasm: Mono's internal calls are implemented over cs2wasm's runtime; those it has no equivalent for yet throw
// NotSupportedException, and those the sandbox lacks are build errors (MissingCapability).

//
// (C) 2001 Ximian, Inc.  http://www.ximian.com
// Copyright (C) 2004-2005 Novell, Inc (http://www.novell.com)
// Copyright (C) 2012 Xamarin Inc (http://www.xamarin.com)
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using InteropServicesCallingConvention = System.Runtime.InteropServices.CallingConvention;

namespace System.Reflection
{

    [Flags]
    internal enum PInvokeAttributes
    {
        NoMangle = 0x0001,

        CharSetMask = 0x0006,
        CharSetNotSpec = 0x0000,
        CharSetAnsi = 0x0002,
        CharSetUnicode = 0x0004,
        CharSetAuto = 0x0006,

        BestFitUseAssem = 0x0000,
        BestFitEnabled = 0x0010,
        BestFitDisabled = 0x0020,
        BestFitMask = 0x0030,

        ThrowOnUnmappableCharUseAssem = 0x0000,
        ThrowOnUnmappableCharEnabled = 0x1000,
        ThrowOnUnmappableCharDisabled = 0x2000,
        ThrowOnUnmappableCharMask = 0x3000,

        SupportsLastError = 0x0040,

        CallConvMask = 0x0700,
        CallConvWinapi = 0x0100,
        CallConvCdecl = 0x0200,
        CallConvStdcall = 0x0300,
        CallConvThiscall = 0x0400,
        CallConvFastcall = 0x0500,

        MaxValue = 0xFFFF,
    }

    internal struct MonoMethodInfo
    {
#pragma warning disable 649
        private Type parent;
        private Type ret;
        internal MethodAttributes attrs;
        internal MethodImplAttributes iattrs;
        private CallingConventions callconv;
#pragma warning restore 649

                private static void get_method_info(IntPtr handle, out MonoMethodInfo info)
        {
            // cs2wasm: from the compiler's reflection record (Boundary/Meta.cs); a constructor's handle is odd
            int h = (int)handle;
            info = default;
            if ((h & 1) != 0)
            {
                int e = h - 1, flags = Cs2Wasm.Runtime.Meta.I(e, 0);
                info.parent = Cs2Wasm.Runtime.Meta.TypeAt(Cs2Wasm.Runtime.Meta.I(e, 7));
                info.ret = typeof(void);
                info.attrs = Cs2Wasm.Runtime.Meta.MethodAttrs(flags) | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
                info.callconv = (flags & Cs2Wasm.Runtime.Meta.FStatic) != 0 ? CallingConventions.Standard : CallingConventions.Standard | CallingConventions.HasThis;
            }
            else
            {
                int flags = Cs2Wasm.Runtime.Meta.I(h, 2);
                info.parent = Cs2Wasm.Runtime.Meta.TypeAt(Cs2Wasm.Runtime.Meta.I(h, 9));
                info.ret = Cs2Wasm.Runtime.Meta.TypeAt(Cs2Wasm.Runtime.Meta.I(h, 1));
                info.attrs = Cs2Wasm.Runtime.Meta.MethodAttrs(flags);
                info.callconv = (flags & Cs2Wasm.Runtime.Meta.FStatic) != 0 ? CallingConventions.Standard : CallingConventions.Standard | CallingConventions.HasThis;
            }
        }

                private static int get_method_attributes(IntPtr handle) => (int)GetMethodInfo(handle).attrs;

        internal static MonoMethodInfo GetMethodInfo(IntPtr handle)
        {
            MonoMethodInfo info;
            get_method_info(handle, out info);
            return info;
        }

        internal static Type GetDeclaringType(IntPtr handle)
        {
            return GetMethodInfo(handle).parent;
        }

        internal static Type GetReturnType(IntPtr handle)
        {
            return GetMethodInfo(handle).ret;
        }

        internal static MethodAttributes GetAttributes(IntPtr handle)
        {
            return (MethodAttributes)get_method_attributes(handle);
        }

        internal static CallingConventions GetCallingConvention(IntPtr handle)
        {
            return GetMethodInfo(handle).callconv;
        }

        internal static MethodImplAttributes GetMethodImplementationFlags(IntPtr handle)
        {
            return GetMethodInfo(handle).iattrs;
        }

                private static ParameterInfo[] get_parameter_info(IntPtr handle, MemberInfo member)
        {
            int h = (int)handle;
            int e = (h & 1) != 0 ? h - 1 : h, at = (h & 1) != 0 ? 2 : 4;
            int n = Cs2Wasm.Runtime.Meta.I(e, at), types = Cs2Wasm.Runtime.Meta.I(e, at + 1), names = Cs2Wasm.Runtime.Meta.I(e, at + 2);
            var r = new ParameterInfo[n];
            for (int i = 0; i < n; i++)
                r[i] = new RuntimeParameterInfo(Cs2Wasm.Runtime.Meta.S(Cs2Wasm.Runtime.Meta.I(names, i)), Cs2Wasm.Runtime.Meta.TypeAt(Cs2Wasm.Runtime.Meta.I(types, i)), i, 0, DBNull.Value, member, null!);
            return r;
        }

        internal static ParameterInfo[] GetParametersInfo(IntPtr handle, MemberInfo member)
        {
            return get_parameter_info(handle, member);
        }

                private static MarshalAsAttribute get_retval_marshal(IntPtr handle) => null!;

        internal static ParameterInfo GetReturnParameterInfo(RuntimeMethodInfo method)
        {
            return RuntimeParameterInfo.New(GetReturnType(method.mhandle), method, get_retval_marshal(method.mhandle));
        }
    }

#region Sync with _MonoReflectionMethod in object-internals.h
    [StructLayout(LayoutKind.Sequential)]
    internal sealed unsafe partial class RuntimeMethodInfo : MethodInfo
    {
#pragma warning disable 649
        internal IntPtr mhandle;
        private string? name;
        private Type? reftype;
#pragma warning restore 649
#endregion
        private string? toString;
        private RuntimeType[]? parameterTypes;
        private MethodBaseInvoker? invoker;

        internal InvocationFlags InvocationFlags
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                InvocationFlags flags = Invoker._invocationFlags;
                Debug.Assert((flags & InvocationFlags.Initialized) == InvocationFlags.Initialized);
                return flags;
            }
        }

        private MethodBaseInvoker Invoker
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                invoker ??= new MethodBaseInvoker(this);
                return invoker;
            }
        }

        public override Module Module
        {
            get
            {
                return GetRuntimeModule();
            }
        }

        public override bool IsCollectible => false;

        private string FormatNameAndSig()
        {
            // Serialization uses ToString to resolve MethodInfo overloads.
            StringBuilder sbName = new StringBuilder(Name);

            if (IsGenericMethod)
                sbName.Append(RuntimeMethodHandle.ConstructInstantiation(this));

            sbName.Append('(');
            RuntimeParameterInfo.FormatParameters(sbName, GetParametersAsSpan(), CallingConvention);
            sbName.Append(')');

            return sbName.ToString();
        }

        public override Delegate CreateDelegate(Type delegateType)
        {
            return Delegate.CreateDelegate(delegateType, this);
        }

        public override Delegate CreateDelegate(Type delegateType, object? target)
        {
            return Delegate.CreateDelegate(delegateType, target, this);
        }

        // copied from CoreCLR's RuntimeMethodInfo
        public override string ToString()
        {
            if (toString == null)
            {
                var sbName = new ValueStringBuilder(MethodNameBufferSize);

                sbName.Append(ReturnType.FormatTypeName());
                sbName.Append(' ');
                sbName.Append(Name);

                if (IsGenericMethod)
                    sbName.Append(RuntimeMethodHandle.ConstructInstantiation(this));

                sbName.Append('(');
                AppendParameters(ref sbName, GetParameterTypes(), CallingConvention);
                sbName.Append(')');

                toString = sbName.ToString();
            }

            return toString;
        }

        internal RuntimeModule GetRuntimeModule()
        {
            return ((RuntimeType)DeclaringType).GetRuntimeModule();
        }

        internal static MethodBase GetMethodFromHandleNoGenericCheck(RuntimeMethodHandle handle)
        {
            return GetMethodFromHandleInternalType_native(handle.Value, IntPtr.Zero, false);
        }

        internal static MethodBase GetMethodFromHandleNoGenericCheck(RuntimeMethodHandle handle, RuntimeTypeHandle reflectedType)
        {
            return GetMethodFromHandleInternalType_native(handle.Value, reflectedType.Value, false);
        }

                [DynamicDependency("#ctor(System.Reflection.ExceptionHandlingClause[],System.Reflection.LocalVariableInfo[],System.Byte[],System.Boolean,System.Int32,System.Int32)", typeof(RuntimeMethodBody))]
        internal static MethodBody GetMethodBodyInternal(IntPtr handle) => throw Cs2Wasm.Runtime.TypeInfo.NotYet("reflection (Mono's GetMethodBodyInternal): cs2wasm's reflection reads the compiler's metadata");

        [RequiresUnreferencedCode("Trimming may change method bodies. For example it can change some instructions, remove branches or local variables.")]
        internal static MethodBody GetMethodBody(IntPtr handle)
        {
            return GetMethodBodyInternal(handle);
        }

        internal static MethodBase GetMethodFromHandleInternalType(IntPtr method_handle, IntPtr type_handle)
        {
            return GetMethodFromHandleInternalType_native(method_handle, type_handle, true);
        }

                private static MethodBase GetMethodFromHandleInternalType_native(IntPtr method_handle, IntPtr type_handle, bool genericCheck)
        {
            int h = (int)method_handle;
            var declaring = MonoMethodInfo.GetDeclaringType(method_handle);
            Type reflected = type_handle != IntPtr.Zero ? Cs2Wasm.Runtime.TypeInfo.Type((int)type_handle) : declaring;
            if ((h & 1) != 0) return new RuntimeConstructorInfo(h, reflected);
            return new RuntimeMethodInfo(h, Cs2Wasm.Runtime.Meta.S(Cs2Wasm.Runtime.Meta.I(h, 0)), reflected);
        }

        internal RuntimeMethodInfo()
        {
        }

        internal RuntimeMethodInfo(RuntimeMethodHandle mhandle)
        {
            this.mhandle = mhandle.Value;
        }

        /// <summary>cs2wasm: a method of the compiler's reflection record (its entry's address).</summary>
        internal RuntimeMethodInfo(int entry, string name, Type reflected)
        {
            mhandle = (IntPtr)entry;
            this.name = name;
            reftype = reflected;
        }

        // cs2wasm: member objects are made per lookup (CoreCLR caches one per member and reflected type), so two of
        // the same entry and reflected type are equal
        public override bool Equals(object? obj) => ReferenceEquals(this, obj) || obj is RuntimeMethodInfo m && m.mhandle == mhandle && ReferenceEquals(m.reftype, reftype);
        public override int GetHashCode() => (int)mhandle;

                internal static string get_name(MethodBase method) => method is RuntimeConstructorInfo c ? ((Cs2Wasm.Runtime.Meta.I((int)c.mhandle - 1, 0) & Cs2Wasm.Runtime.Meta.FStatic) != 0 ? ".cctor" : ".ctor") : Cs2Wasm.Runtime.Meta.S(Cs2Wasm.Runtime.Meta.I((int)((RuntimeMethodInfo)method).mhandle, 0));

                // cs2wasm: the records do not link overrides to the methods they override
                internal static RuntimeMethodInfo get_base_method(RuntimeMethodInfo method, bool definition) => method;

                internal static int get_metadata_token(RuntimeMethodInfo method) => 0x06000000 | ((int)method.mhandle & 0xFFFFFF);

        public override MethodInfo GetBaseDefinition()
        {
            return get_base_method(this, true);
        }

        // TODO: Remove, needed only for MonoCustomAttribute
        internal MethodInfo GetBaseMethod()
        {
            return get_base_method(this, false);
        }

        public override ParameterInfo ReturnParameter
        {
            get
            {
                return MonoMethodInfo.GetReturnParameterInfo(this);
            }
        }

        public override Type ReturnType
        {
            get
            {
                return MonoMethodInfo.GetReturnType(mhandle);
            }
        }
        public override ICustomAttributeProvider ReturnTypeCustomAttributes
        {
            get
            {
                return MonoMethodInfo.GetReturnParameterInfo(this);
            }
        }

        public override int MetadataToken
        {
            get
            {
                return get_metadata_token(this);
            }
        }

        public override MethodImplAttributes GetMethodImplementationFlags()
        {
            return MonoMethodInfo.GetMethodImplementationFlags(mhandle);
        }

        public override ParameterInfo[] GetParameters()
        {
            ParameterInfo[] src = MonoMethodInfo.GetParametersInfo(mhandle, this);
            if (src.Length == 0)
                return src;

            // Have to clone because GetParametersInfo icall returns cached value
            var dest = new ParameterInfo[src.Length];
            Array.FastCopy(ObjectHandleOnStack.Create(ref src), 0, ObjectHandleOnStack.Create(ref dest), 0, src.Length);
            return dest;
        }

        internal override ParameterInfo[] GetParametersInternal()
        {
            return MonoMethodInfo.GetParametersInfo(mhandle, this);
        }

        internal override int GetParametersCount()
        {
            return MonoMethodInfo.GetParametersInfo(mhandle, this).Length;
        }

        internal RuntimeType[] ArgumentTypes
        {
            get
            {
                if (parameterTypes != null)
                {
                    return parameterTypes;
                }

                ParameterInfo[] src = GetParametersInternal();
                RuntimeType[] dest = new RuntimeType[src.Length];
                for (int i = 0; i < dest.Length; ++i)
                {
                    dest[i] = (RuntimeType)src[i].ParameterType;
                }

                Interlocked.CompareExchange(ref parameterTypes, dest, null);
                return dest;
            }
        }

        /*
         * InternalInvoke() receives the parameters correctly converted by the
         * binder to match the types of the method signature.
         * The exc argument is used to capture exceptions thrown by the icall.
         * Exceptions thrown by the called method propagate normally.
         */
                internal object? InternalInvoke(object? obj, IntPtr *args, out Exception? exc)
        {
            exc = null;
            int thunk = Cs2Wasm.Runtime.Meta.I((int)mhandle, 3);
            if (thunk == 0) throw Cs2Wasm.Runtime.TypeInfo.NotYet("invoking " + Name + " (the compiler kept no call for it)");
            return Cs2Wasm.Runtime.TypeInfo.Invoke(thunk, obj, Cs2Wasm.Runtime.Meta.ArgsFrom(args, ArgumentTypes));
        }

        public override RuntimeMethodHandle MethodHandle
        {
            get
            {
                return new RuntimeMethodHandle(mhandle);
            }
        }

        public override MethodAttributes Attributes
        {
            get
            {
                return MonoMethodInfo.GetAttributes(mhandle);
            }
        }

        public override CallingConventions CallingConvention
        {
            get
            {
                return MonoMethodInfo.GetCallingConvention(mhandle);
            }
        }

        public override Type? ReflectedType
        {
            get
            {
                return reftype;
            }
        }
        public override Type DeclaringType
        {
            get
            {
                return MonoMethodInfo.GetDeclaringType(mhandle);
            }
        }
        public override string Name
        {
            get
            {
                if (name != null)
                    return name;
                return get_name(this);
            }
        }

        public override bool IsDefined(Type attributeType, bool inherit)
        {
            return CustomAttribute.IsDefined(this, attributeType, inherit);
        }

        public override object[] GetCustomAttributes(bool inherit)
        {
            return CustomAttribute.GetCustomAttributes(this, inherit);
        }
        public override object[] GetCustomAttributes(Type attributeType, bool inherit)
        {
            return CustomAttribute.GetCustomAttributes(this, attributeType, inherit);
        }

                internal void GetPInvoke(out PInvokeAttributes flags, out string entryPoint, out string dllName) => throw Cs2Wasm.Runtime.TypeInfo.NotYet("reflection (Mono's GetPInvoke): cs2wasm's reflection reads the compiler's metadata");

        internal object[]? GetPseudoCustomAttributes()
        {
            int count = 0;

            /* MS.NET doesn't report MethodImplAttribute */

            MonoMethodInfo info = MonoMethodInfo.GetMethodInfo(mhandle);
            if ((info.iattrs & MethodImplAttributes.PreserveSig) != 0)
                count++;
            if ((info.attrs & MethodAttributes.PinvokeImpl) != 0)
                count++;

            if (count == 0)
                return null;
            object[] attrs = new object[count];
            count = 0;

            if ((info.iattrs & MethodImplAttributes.PreserveSig) != 0)
                attrs[count++] = new PreserveSigAttribute();
            if ((info.attrs & MethodAttributes.PinvokeImpl) != 0)
            {
                attrs[count++] = GetDllImportAttribute();
            }

            return attrs;
        }

        private DllImportAttribute GetDllImportAttribute()
        {
            string entryPoint;
            string? dllName;
            PInvokeAttributes flags;

            GetPInvoke(out flags, out entryPoint, out dllName);

            CharSet charSet = CharSet.None;

            switch (flags & PInvokeAttributes.CharSetMask)
            {
                case PInvokeAttributes.CharSetNotSpec: charSet = CharSet.None; break;
                case PInvokeAttributes.CharSetAnsi: charSet = CharSet.Ansi; break;
                case PInvokeAttributes.CharSetUnicode: charSet = CharSet.Unicode; break;
                case PInvokeAttributes.CharSetAuto: charSet = CharSet.Auto; break;

                // Invalid: default to CharSet.None
                default: break;
            }

            CallingConvention callingConvention = InteropServicesCallingConvention.Cdecl;

            switch (flags & PInvokeAttributes.CallConvMask)
            {
                case PInvokeAttributes.CallConvWinapi: callingConvention = InteropServicesCallingConvention.Winapi; break;
                case PInvokeAttributes.CallConvCdecl: callingConvention = InteropServicesCallingConvention.Cdecl; break;
                case PInvokeAttributes.CallConvStdcall: callingConvention = InteropServicesCallingConvention.StdCall; break;
                case PInvokeAttributes.CallConvThiscall: callingConvention = InteropServicesCallingConvention.ThisCall; break;
                case PInvokeAttributes.CallConvFastcall: callingConvention = InteropServicesCallingConvention.FastCall; break;

                // Invalid: default to CallingConvention.Cdecl
                default: break;
            }

            bool exactSpelling = (flags & PInvokeAttributes.NoMangle) != 0;
            bool setLastError = (flags & PInvokeAttributes.SupportsLastError) != 0;
            bool bestFitMapping = (flags & PInvokeAttributes.BestFitMask) == PInvokeAttributes.BestFitEnabled;
            bool throwOnUnmappableChar = (flags & PInvokeAttributes.ThrowOnUnmappableCharMask) == PInvokeAttributes.ThrowOnUnmappableCharEnabled;
            bool preserveSig = (GetMethodImplementationFlags() & MethodImplAttributes.PreserveSig) != 0;

            return new DllImportAttribute(dllName)
            {
                EntryPoint = entryPoint,
                CharSet = charSet,
                SetLastError = setLastError,
                ExactSpelling = exactSpelling,
                PreserveSig = preserveSig,
                BestFitMapping = bestFitMapping,
                ThrowOnUnmappableChar = throwOnUnmappableChar,
                CallingConvention = callingConvention
            };
        }

        internal CustomAttributeData[]? GetPseudoCustomAttributesData()
        {
            int count = 0;

            /* MS.NET doesn't report MethodImplAttribute */

            MonoMethodInfo info = MonoMethodInfo.GetMethodInfo(mhandle);
            if ((info.iattrs & MethodImplAttributes.PreserveSig) != 0)
                count++;
            if ((info.attrs & MethodAttributes.PinvokeImpl) != 0)
                count++;

            if (count == 0)
                return null;
            CustomAttributeData[] attrsData = new CustomAttributeData[count];
            count = 0;

            if ((info.iattrs & MethodImplAttributes.PreserveSig) != 0)
                attrsData[count++] = new RuntimeCustomAttributeData((typeof(PreserveSigAttribute)).GetConstructor(Type.EmptyTypes)!);
            if ((info.attrs & MethodAttributes.PinvokeImpl) != 0)
                attrsData[count++] = GetDllImportAttributeData()!;

            return attrsData;
        }

        private RuntimeCustomAttributeData? GetDllImportAttributeData()
        {
            if ((Attributes & MethodAttributes.PinvokeImpl) == 0)
                return null;

            string entryPoint;
            string? dllName;
            PInvokeAttributes flags;

            GetPInvoke(out flags, out entryPoint, out dllName);

            CharSet charSet = (flags & PInvokeAttributes.CharSetMask) switch
            {
                PInvokeAttributes.CharSetNotSpec => CharSet.None,
                PInvokeAttributes.CharSetAnsi => CharSet.Ansi,
                PInvokeAttributes.CharSetUnicode => CharSet.Unicode,
                PInvokeAttributes.CharSetAuto => CharSet.Auto,
                // Invalid: default to CharSet.None
                _ => CharSet.None,
            };

            InteropServicesCallingConvention callingConvention = (flags & PInvokeAttributes.CallConvMask) switch
            {
                PInvokeAttributes.CallConvWinapi => InteropServicesCallingConvention.Winapi,
                PInvokeAttributes.CallConvCdecl => InteropServicesCallingConvention.Cdecl,
                PInvokeAttributes.CallConvStdcall => InteropServicesCallingConvention.StdCall,
                PInvokeAttributes.CallConvThiscall => InteropServicesCallingConvention.ThisCall,
                PInvokeAttributes.CallConvFastcall => InteropServicesCallingConvention.FastCall,
                // Invalid: default to CallingConvention.Cdecl
                _ => InteropServicesCallingConvention.Cdecl,
            };

            bool exactSpelling = (flags & PInvokeAttributes.NoMangle) != 0;
            bool setLastError = (flags & PInvokeAttributes.SupportsLastError) != 0;
            bool bestFitMapping = (flags & PInvokeAttributes.BestFitMask) == PInvokeAttributes.BestFitEnabled;
            bool throwOnUnmappableChar = (flags & PInvokeAttributes.ThrowOnUnmappableCharMask) == PInvokeAttributes.ThrowOnUnmappableCharEnabled;
            bool preserveSig = (GetMethodImplementationFlags() & MethodImplAttributes.PreserveSig) != 0;

            var ctorArgs = new CustomAttributeTypedArgument[] {
                new CustomAttributeTypedArgument (typeof(string), dllName),
            };

            Type attrType = typeof(DllImportAttribute);

            var namedArgs = new CustomAttributeNamedArgument[] {
                new CustomAttributeNamedArgument (attrType.GetField ("EntryPoint")!, entryPoint),
                new CustomAttributeNamedArgument (attrType.GetField ("CharSet")!, charSet),
                new CustomAttributeNamedArgument (attrType.GetField ("ExactSpelling")!, exactSpelling),
                new CustomAttributeNamedArgument (attrType.GetField ("SetLastError")!, setLastError),
                new CustomAttributeNamedArgument (attrType.GetField ("PreserveSig")!, preserveSig),
                new CustomAttributeNamedArgument (attrType.GetField ("CallingConvention")!, callingConvention),
                new CustomAttributeNamedArgument (attrType.GetField ("BestFitMapping")!, bestFitMapping),
                new CustomAttributeNamedArgument (attrType.GetField ("ThrowOnUnmappableChar")!, throwOnUnmappableChar)
            };

            return new RuntimeCustomAttributeData(
                attrType.GetConstructor(new[] { typeof(string) })!,
                ctorArgs,
                namedArgs);
        }

        [RequiresUnreferencedCode("If some of the generic arguments are annotated (either with DynamicallyAccessedMembersAttribute, or generic constraints), trimming can't validate that the requirements of those annotations are met.")]
        public override MethodInfo MakeGenericMethod(Type[] methodInstantiation)
        {
            ArgumentNullException.ThrowIfNull(methodInstantiation);

            if (!IsGenericMethodDefinition)
                throw new InvalidOperationException(SR.Format(SR.Arg_NotGenericMethodDefinition, this));

            /*FIXME add GetGenericArgumentsLength() internal vcall to speed this up*/
            if (GetGenericArguments().Length != methodInstantiation.Length)
                throw new ArgumentException(SR.Format(SR.Argument_NotEnoughGenArguments, GetGenericArguments().Length, methodInstantiation.Length));

            bool hasUserType = false;
            foreach (Type type in methodInstantiation)
            {
                if (type == null)
                    throw new ArgumentNullException();
                if (!(type is RuntimeType))
                    hasUserType = true;
            }

            if (hasUserType)
            {
                if (RuntimeFeature.IsDynamicCodeSupported)
                    return new MethodOnTypeBuilderInstantiation(this, methodInstantiation);

                throw new NotSupportedException(SR.PlatformNotSupported_ReflectionEmit);
            }

            MethodInfo ret = MakeGenericMethod_impl(methodInstantiation);
            if (ret == null)
                throw new ArgumentException(SR.Format(SR.Argument_NotEnoughGenArguments, GetGenericArguments().Length, methodInstantiation.Length));
            return ret;
        }

        // cs2wasm: a method entry's generic blob (Compiler/Reflection.cs): 0, a definition [1, n, parameters...,
        // count, instance entries...], or an instance [2, n, arguments..., definition entry]
        int GenericBlob => Cs2Wasm.Runtime.Meta.I((int)mhandle, 10);

        private MethodInfo MakeGenericMethod_impl(Type[] types)
        {
            int g = GenericBlob, n = Cs2Wasm.Runtime.Meta.I(g, 1), count = Cs2Wasm.Runtime.Meta.I(g, 2 + n);
            for (int k = 0; k < count; k++)
            {
                int e = Cs2Wasm.Runtime.Meta.I(g, 3 + n + k), gi = Cs2Wasm.Runtime.Meta.I(e, 10);
                bool same = Cs2Wasm.Runtime.Meta.I(gi, 1) == types.Length;
                for (int a = 0; a < types.Length && same; a++) same = types[a] is RuntimeType rt && Cs2Wasm.Runtime.Meta.I(gi, 2 + a) == rt.TypeId;
                if (same) return new RuntimeMethodInfo(e, name!, reftype!);
            }
            throw new PlatformNotSupportedException("cs2wasm: " + this + " with these type arguments is not a method the program makes (no code exists for it)");
        }

        public override Type[] GetGenericArguments()
        {
            int g = GenericBlob;
            if (g == 0) return Type.EmptyTypes;
            int n = Cs2Wasm.Runtime.Meta.I(g, 1);
            var r = new Type[n];
            for (int i = 0; i < n; i++) r[i] = RuntimeType.FromId(Cs2Wasm.Runtime.Meta.I(g, 2 + i));
            return r;
        }

        private MethodInfo GetGenericMethodDefinition_impl()
        {
            int g = GenericBlob;
            if (g == 0) return null!;
            if (Cs2Wasm.Runtime.Meta.I(g, 0) == 1) return this;
            int de = Cs2Wasm.Runtime.Meta.I(g, 2 + Cs2Wasm.Runtime.Meta.I(g, 1));
            return de == 0 ? null! : new RuntimeMethodInfo(de, name!, reftype!);
        }

        public override MethodInfo GetGenericMethodDefinition()
        {
            MethodInfo res = GetGenericMethodDefinition_impl();
            if (res == null)
                throw new InvalidOperationException();

            return res;
        }

        public override bool IsGenericMethodDefinition => GenericBlob is int g && g != 0 && Cs2Wasm.Runtime.Meta.I(g, 0) == 1;

        public override bool IsGenericMethod => GenericBlob != 0;

        public override bool ContainsGenericParameters
        {
            get
            {
                if (IsGenericMethod)
                {
                    foreach (Type arg in GetGenericArguments())
                        if (arg.ContainsGenericParameters)
                            return true;
                }
                return DeclaringType.ContainsGenericParameters;
            }
        }

        [RequiresUnreferencedCode("Trimming may change method bodies. For example it can change some instructions, remove branches or local variables.")]
        public override MethodBody GetMethodBody()
        {
            return GetMethodBody(mhandle);
        }

        public override IList<CustomAttributeData> GetCustomAttributesData()
        {
            return RuntimeCustomAttributeData.GetCustomAttributesInternal(this);
        }

        public sealed override bool HasSameMetadataDefinitionAs(MemberInfo other) => HasSameMetadataDefinitionAsCore<RuntimeMethodInfo>(other);

        private static bool IsDisallowedAsyncHelper => false;
    }
#region Sync with _MonoReflectionMethod in object-internals.h
    [StructLayout(LayoutKind.Sequential)]
    internal sealed unsafe partial class RuntimeConstructorInfo : ConstructorInfo
    {
#pragma warning disable 649
        internal IntPtr mhandle;
        private string? name;
        private Type? reftype;
#pragma warning restore 649
#endregion
        private string? toString;
        private RuntimeType[]? parameterTypes;
        private MethodBaseInvoker? invoker;

        internal InvocationFlags InvocationFlags
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                InvocationFlags flags = Invoker._invocationFlags;
                Debug.Assert((flags & InvocationFlags.Initialized) == InvocationFlags.Initialized);
                return flags;
            }
        }

        internal MethodBaseInvoker Invoker
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                invoker ??= new MethodBaseInvoker(this);
                return invoker;
            }
        }

        public override Module Module
        {
            get
            {
                return GetRuntimeModule();
            }
        }

        public override bool IsCollectible => false;

        internal RuntimeModule GetRuntimeModule()
        {
            return RuntimeTypeHandle.GetModule((RuntimeType)DeclaringType);
        }

        public override MethodImplAttributes GetMethodImplementationFlags()
        {
            return MonoMethodInfo.GetMethodImplementationFlags(mhandle);
        }

        public override ParameterInfo[] GetParameters()
        {
            return MonoMethodInfo.GetParametersInfo(mhandle, this);
        }

        internal override ParameterInfo[] GetParametersInternal()
        {
            return MonoMethodInfo.GetParametersInfo(mhandle, this);
        }

        internal override int GetParametersCount()
        {
            ParameterInfo[] pi = MonoMethodInfo.GetParametersInfo(mhandle, this);
            return pi == null ? 0 : pi.Length;
        }

        internal RuntimeType[] ArgumentTypes
        {
            get
            {
                if (parameterTypes != null)
                {
                    return parameterTypes;
                }

                ParameterInfo[] src = GetParametersInternal();
                RuntimeType[] dest = new RuntimeType[src.Length];
                for (int i = 0; i < dest.Length; ++i)
                {
                    dest[i] = (RuntimeType)src[i].ParameterType;
                }

                Interlocked.CompareExchange(ref parameterTypes, dest, null);
                return dest;
            }
        }

                internal static void InvokeClassConstructor(QCallTypeHandle type) => throw Cs2Wasm.Runtime.TypeInfo.NotYet("reflection (Mono's InvokeClassConstructor): cs2wasm's reflection reads the compiler's metadata");

        private void InvokeClassConstructor()
        {
            RuntimeType type = (RuntimeType)DeclaringType;
            InvokeClassConstructor(new QCallTypeHandle(ref type));
        }

        /*
         * InternalInvoke() receives the parameters correctly converted by the binder
         * to match the types of the method signature.
         */
                internal object InternalInvoke(object? obj, IntPtr *args, out Exception? exc)
        {
            exc = null;
            if (obj != null) throw Cs2Wasm.Runtime.TypeInfo.NotYet("running a constructor on an existing object");
            int thunk = Cs2Wasm.Runtime.Meta.I((int)mhandle - 1, 1);
            if (thunk == 0) throw Cs2Wasm.Runtime.TypeInfo.NotYet("invoking a constructor of " + DeclaringType + " (the compiler kept no call for it)");
            return Cs2Wasm.Runtime.TypeInfo.Invoke(thunk, null, Cs2Wasm.Runtime.Meta.ArgsFrom(args, ArgumentTypes))!;
        }

        /// <summary>cs2wasm: a constructor of the compiler's reflection record (its entry's address + 1).</summary>
        internal RuntimeConstructorInfo(int handle, Type reflected)
        {
            mhandle = (IntPtr)handle;
            reftype = reflected;
        }

        // cs2wasm: member objects are made per lookup (CoreCLR caches one per member and reflected type), so two of
        // the same entry and reflected type are equal
        public override bool Equals(object? obj) => ReferenceEquals(this, obj) || obj is RuntimeConstructorInfo m && m.mhandle == mhandle && ReferenceEquals(m.reftype, reftype);
        public override int GetHashCode() => (int)mhandle;

        public override RuntimeMethodHandle MethodHandle
        {
            get
            {
                return new RuntimeMethodHandle(mhandle);
            }
        }

        public override MethodAttributes Attributes
        {
            get
            {
                return MonoMethodInfo.GetAttributes(mhandle);
            }
        }

        public override CallingConventions CallingConvention
        {
            get
            {
                return MonoMethodInfo.GetCallingConvention(mhandle);
            }
        }

        public override bool ContainsGenericParameters
        {
            get
            {
                return DeclaringType.ContainsGenericParameters;
            }
        }

        public override Type? ReflectedType
        {
            get
            {
                return reftype;
            }
        }
        public override Type DeclaringType
        {
            get
            {
                return MonoMethodInfo.GetDeclaringType(mhandle);
            }
        }
        public override string Name
        {
            get
            {
                if (name != null)
                    return name;
                return RuntimeMethodInfo.get_name(this);
            }
        }

        public override bool IsDefined(Type attributeType, bool inherit)
        {
            return CustomAttribute.IsDefined(this, attributeType, inherit);
        }

        public override object[] GetCustomAttributes(bool inherit)
        {
            return CustomAttribute.GetCustomAttributes(this, inherit);
        }

        public override object[] GetCustomAttributes(Type attributeType, bool inherit)
        {
            return CustomAttribute.GetCustomAttributes(this, attributeType, inherit);
        }

        [RequiresUnreferencedCode("Trimming may change method bodies. For example it can change some instructions, remove branches or local variables.")]
        public override MethodBody GetMethodBody()
        {
            return RuntimeMethodInfo.GetMethodBody(mhandle);
        }

        // copied from CoreCLR's RuntimeConstructorInfo
        public override string ToString()
        {
            if (toString == null)
            {
                var sbName = new ValueStringBuilder(MethodNameBufferSize);

                // "Void" really doesn't make sense here. But we'll keep it for compat reasons.
                sbName.Append("Void ");

                sbName.Append(Name);

                sbName.Append('(');
                AppendParameters(ref sbName, GetParameterTypes(), CallingConvention);
                sbName.Append(')');

                toString = sbName.ToString();
            }

            return toString;
        }

        public override IList<CustomAttributeData> GetCustomAttributesData()
        {
            return RuntimeCustomAttributeData.GetCustomAttributesInternal(this);
        }

        public sealed override bool HasSameMetadataDefinitionAs(MemberInfo other) => HasSameMetadataDefinitionAsCore<RuntimeConstructorInfo>(other);

        public override int MetadataToken
        {
            get
            {
                return get_metadata_token(this);
            }
        }

                internal static int get_metadata_token(RuntimeConstructorInfo method) => 0x06000000 | ((int)method.mhandle & 0xFFFFFF);
    }
}
