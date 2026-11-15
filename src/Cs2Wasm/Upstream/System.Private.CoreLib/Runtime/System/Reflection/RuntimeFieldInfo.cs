// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// cs2wasm: Mono's internal calls are implemented over cs2wasm's runtime; those it has no equivalent for yet throw
// NotSupportedException, and those the sandbox lacks are build errors (MissingCapability).

//
// Copyright (C) 2004 Novell, Inc (http://www.novell.com)
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
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Reflection
{
    // Note that in CoreCLR, RtFieldInfo derives from RuntimeFieldInfo.
    internal abstract class RtFieldInfo : FieldInfo
    {
        internal abstract object UnsafeGetValue(object obj);
        internal abstract void UnsafeSetValue(object obj, object value, BindingFlags invokeAttr, Binder binder, CultureInfo culture);
        internal abstract void CheckConsistency(object target);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal sealed class RuntimeFieldInfo : RtFieldInfo
    {
#pragma warning disable 649
        internal IntPtr klass;
        internal RuntimeFieldHandle fhandle;
        private string? name;
        private Type? type;
        private FieldAttributes attrs;
#pragma warning restore 649
        private Type? reftype;

        internal RuntimeFieldInfo() { }

        /// <summary>cs2wasm: a field of the compiler's reflection record (its entry's address).</summary>
        internal RuntimeFieldInfo(int entry, Type reflected)
        {
            fhandle = new RuntimeFieldHandle((IntPtr)entry);
            name = Cs2Wasm.Runtime.Meta.S(Cs2Wasm.Runtime.Meta.I(entry, 0));
            attrs = Cs2Wasm.Runtime.Meta.FieldAttrs(Cs2Wasm.Runtime.Meta.I(entry, 2));
            reftype = reflected;
        }

        int Entry => (int)fhandle.Value;

        // cs2wasm: member objects are made per lookup (CoreCLR caches one per member and reflected type), so two of
        // the same entry and reflected type are equal
        public override bool Equals(object? obj) => ReferenceEquals(this, obj) || obj is RuntimeFieldInfo f && f.Entry == Entry && ReferenceEquals(f.reftype, reftype);
        public override int GetHashCode() => Entry;

        public override Module Module
        {
            get
            {
                return GetRuntimeModule();
            }
        }

        internal RuntimeType GetDeclaringTypeInternal()
        {
            return (RuntimeType)DeclaringType!;
        }

        internal RuntimeModule GetRuntimeModule()
        {
            return GetDeclaringTypeInternal().GetRuntimeModule();
        }

        public override bool IsCollectible => false;

                internal override object UnsafeGetValue(object obj) => GetValueInternal(obj)!;

        internal override void CheckConsistency(object target)
        {
            // only test instance fields
            if ((Attributes & FieldAttributes.Static) != FieldAttributes.Static)
            {
                if (!DeclaringType!.IsInstanceOfType(target))
                {
                    if (target == null)
                    {
                        throw new TargetException(SR.RFLCT_Targ_StatFldReqTarg);
                    }
                    else
                    {
                        throw new ArgumentException(
                            SR.Format(SR.Arg_FieldDeclTarget,
                                Name, DeclaringType, target.GetType()));
                    }
                }
            }
        }

        [DebuggerStepThrough]
        [DebuggerHidden]
        internal override void UnsafeSetValue(object? obj, object? value, BindingFlags invokeAttr, Binder? binder, CultureInfo? culture)
        {
            bool isClassInitialized = false;
            RuntimeFieldHandle.SetValue(this, obj, value, null, Attributes, null, ref isClassInitialized);
        }

        [DebuggerStepThrough]
        [DebuggerHidden]
        public override void SetValueDirect(TypedReference obj, object value)
        {
            if (obj.IsNull)
                throw new ArgumentException(SR.Arg_TypedReference_Null);

            unsafe
            {
                // Passing TypedReference by reference is easier to make correct in native code
                RuntimeFieldHandle.SetValueDirect(this, (RuntimeType)FieldType, &obj, value, (RuntimeType?)DeclaringType);
            }
        }

        [DebuggerStepThrough]
        [DebuggerHidden]
        public override object GetValueDirect(TypedReference obj)
        {
            if (obj.IsNull)
                throw new ArgumentException(SR.Arg_TypedReference_Null);

            unsafe
            {
                // Passing TypedReference by reference is easier to make correct in native code
                return RuntimeFieldHandle.GetValueDirect(this, (RuntimeType)FieldType, &obj, (RuntimeType?)DeclaringType);
            }
        }

        public override FieldAttributes Attributes
        {
            get
            {
                return attrs;
            }
        }
        public override RuntimeFieldHandle FieldHandle
        {
            get
            {
                return fhandle;
            }
        }

                private Type ResolveType() => Cs2Wasm.Runtime.Meta.TypeAt(Cs2Wasm.Runtime.Meta.I(Entry, 1));

        public override Type FieldType => type ??= ResolveType();

                private Type GetParentType(bool declaring) => declaring || reftype == null ? Cs2Wasm.Runtime.Meta.TypeAt(Cs2Wasm.Runtime.Meta.I(Entry, 4)) : reftype;

        public override Type ReflectedType
        {
            get
            {
                return GetParentType(false);
            }
        }
        public override Type? DeclaringType
        {
            get
            {
                Type parentType = GetParentType(true);
                return parentType.Name != "<Module>" ? parentType : null;
            }
        }
        public override string Name
        {
            get
            {
                return name!;
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

                internal override int GetFieldOffset() => throw Cs2Wasm.Runtime.TypeInfo.NotYet("reflection (Mono's GetFieldOffset): cs2wasm's reflection reads the compiler's metadata");

                private object? GetValueInternal(object? obj)
        {
            int thunk = Cs2Wasm.Runtime.Meta.I(Entry, 3);
            if (thunk == 0) throw Cs2Wasm.Runtime.TypeInfo.NotYet("reading " + Name + " (the compiler kept no access for it)");
            return Cs2Wasm.Runtime.TypeInfo.Invoke(thunk, obj, null);
        }

        public override object? GetValue(object? obj)
        {
            if (!IsStatic)
            {
                if (obj == null)
                    throw new TargetException(SR.RFLCT_Targ_StatFldReqTarg);
                if (!DeclaringType!.IsAssignableFrom(obj.GetType()))
                    throw new ArgumentException(SR.Format(SR.Arg_FieldDeclTarget, Name, DeclaringType, obj.GetType()), nameof(obj));
            }

            if (!IsLiteral)
                CheckGeneric();

            return GetValueInternal(obj);
        }

        public override string ToString()
        {
            return $"{FieldType.FormatTypeName()} {name}";
        }

                private static void SetValueInternal(FieldInfo fi, object? obj, object? value)
        {
            int thunk = Cs2Wasm.Runtime.Meta.I(((RuntimeFieldInfo)fi).Entry, 3);
            if (thunk == 0) throw Cs2Wasm.Runtime.TypeInfo.NotYet("writing " + fi.Name + " (the compiler kept no access for it)");
            Cs2Wasm.Runtime.TypeInfo.Invoke(thunk, obj, new object?[] { value });
        }

        public override void SetValue(object? obj, object? val, BindingFlags invokeAttr, Binder? binder, CultureInfo? culture)
        {
            if (!IsStatic)
            {
                if (obj == null)
                    throw new TargetException(SR.RFLCT_Targ_StatFldReqTarg);
                if (!DeclaringType!.IsAssignableFrom(obj.GetType()))
                    throw new ArgumentException(SR.Format(SR.Arg_FieldDeclTarget, Name, DeclaringType, obj.GetType()), nameof(obj));
            }
            if (IsLiteral)
                throw new FieldAccessException(SR.Acc_ReadOnly);

            binder ??= Type.DefaultBinder;
            CheckGeneric();
            if (val != null)
            {
                RuntimeType fieldType = (RuntimeType)FieldType;

                if (!ReferenceEquals(val.GetType(), fieldType))
                {
                    fieldType.CheckValue(ref val, binder, culture, invokeAttr);
                }
            }

            SetValueInternal(this, obj, val);
        }

        internal RuntimeFieldInfo Clone(string newName)
        {
            RuntimeFieldInfo field = new RuntimeFieldInfo();
            field.name = newName;
            field.type = type;
            field.attrs = attrs;
            field.klass = klass;
            field.fhandle = fhandle;
            return field;
        }

                public override object GetRawConstantValue() => throw Cs2Wasm.Runtime.TypeInfo.NotYet("reflection (Mono's GetRawConstantValue): cs2wasm's reflection reads the compiler's metadata");

        public override IList<CustomAttributeData> GetCustomAttributesData()
        {
            return RuntimeCustomAttributeData.GetCustomAttributesInternal(this);
        }

        private void CheckGeneric()
        {
            Type? declaringType = DeclaringType;
            if (declaringType != null && declaringType.ContainsGenericParameters)
                throw new InvalidOperationException(SR.Arg_UnboundGenField);
        }

        public sealed override bool HasSameMetadataDefinitionAs(MemberInfo other) => HasSameMetadataDefinitionAsCore<RuntimeFieldInfo>(other);

        public override int MetadataToken
        {
            get
            {
                return get_metadata_token(this);
            }
        }

                internal static int get_metadata_token(RuntimeFieldInfo monoField) => Cs2Wasm.Runtime.Meta.I(monoField.Entry, 7);

                private Type[] GetTypeModifiers(bool optional, int genericArgumentPosition = -1) => throw Cs2Wasm.Runtime.TypeInfo.NotYet("reflection (Mono's GetTypeModifiers): cs2wasm's reflection reads the compiler's metadata");

        public override Type[] GetOptionalCustomModifiers() => GetCustomModifiers(true);

        public override Type[] GetRequiredCustomModifiers() => GetCustomModifiers(false);

        private Type[] GetCustomModifiers(bool optional) => GetTypeModifiers(optional) ?? Type.EmptyTypes;

        internal Type[] GetCustomModifiersFromModifiedType(bool optional, int genericArgumentPosition) => GetTypeModifiers(optional, genericArgumentPosition) ?? Type.EmptyTypes;

        public override Type GetModifiedFieldType() => ModifiedType.Create(FieldType, this);
    }
}
