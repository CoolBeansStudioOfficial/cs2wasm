// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// il2wasm: Mono's internal calls are implemented over il2wasm's runtime; those it has no equivalent for yet throw
// NotSupportedException, and those the sandbox lacks are build errors (MissingCapability).

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Reflection.Emit
{
    [StructLayout(LayoutKind.Sequential)]
    public sealed partial class DynamicMethod : MethodInfo
    {
#region Sync with MonoReflectionDynamicMethod in object-internals.h
        private RuntimeMethodHandle _mhandle;
        private RuntimeType _returnType;
        private RuntimeType[] _parameterTypes;
        private Module _module;
        private bool _skipVisibility;
        private bool _restrictedSkipVisibility;
        private bool _initLocals;
        private RuntimeILGenerator? _ilGenerator;
        private int _nrefs;
        private object?[]? _refs;
        private IntPtr _referencedBy;
        private RuntimeType? _typeOwner;
#endregion

        private string _name;
        private MethodAttributes _attributes;
        private CallingConventions _callingConvention;
        private RuntimeParameterInfo[]? _parameters;
        private Delegate? _deleg;
        private RuntimeMethodInfo? _method;
        private bool _creating;
        private DynamicILInfo? _dynamicILInfo;

        private object? _methodHandle; // unused

        public sealed override Delegate CreateDelegate(Type delegateType)
        {
            ArgumentNullException.ThrowIfNull(delegateType);
            if (_deleg is not null)
                return _deleg;

            CreateDynMethod();

            return _deleg = Delegate.CreateDelegate(delegateType, null, this);
        }

        public sealed override Delegate CreateDelegate(Type delegateType, object? target)
        {
            ArgumentNullException.ThrowIfNull(delegateType);

            CreateDynMethod();

            /* Can't cache the delegate since it is different for each target */
            return Delegate.CreateDelegate(delegateType, target, this);
        }

        public DynamicILInfo GetDynamicILInfo() => _dynamicILInfo ??= new DynamicILInfo(this);

        public ILGenerator GetILGenerator(int streamSize) => GetILGeneratorInternal(streamSize);

        internal RuntimeILGenerator GetRuntimeILGenerator() => GetILGeneratorInternal(64);

        private RuntimeILGenerator GetILGeneratorInternal(int streamSize) =>
            _ilGenerator ??= new RuntimeILGenerator(Module, new DynamicMethodTokenGenerator(this), streamSize);

        public override object? Invoke(object? obj, BindingFlags invokeAttr, Binder? binder, object?[]? parameters, CultureInfo? culture)
        {
            if ((CallingConvention & CallingConventions.VarArgs) == CallingConventions.VarArgs)
                throw new NotSupportedException(SR.NotSupported_CallToVarArg);

            CreateDynMethod();
            // il2wasm: the arguments checked and converted as for any method, then the interpreter
            var ps = GetParametersAsSpan();
            int n = parameters?.Length ?? 0;
            if (n != ps.Length) throw new TargetParameterCountException(SR.Arg_ParmCnt);
            var args = n == 0 ? Array.Empty<object?>() : new object?[n];
            for (int i = 0; i < n; i++)
            {
                var pt = (RuntimeType)(ps[i].ParameterType.IsByRef ? ps[i].ParameterType.GetElementType()! : ps[i].ParameterType);
                object? v = parameters![i];
                if (v == Type.Missing) v = ps[i].HasDefaultValue ? ps[i].DefaultValue : throw new ArgumentException(SR.Arg_VarMissNull, nameof(parameters));
                if (v == null) { if (pt.IsValueType && Nullable.GetUnderlyingType(pt) == null) v = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(pt); }
                else if (!pt.IsInstanceOfType(v))
                {
                    if (binder != null && (invokeAttr & BindingFlags.ExactBinding) == 0) v = binder.ChangeType(v, pt, culture);
                    else throw new ArgumentException(SR.Format(SR.Arg_ObjObjEx, v.GetType(), pt));
                }
                args[i] = v;
            }
            object? r;
            if ((invokeAttr & BindingFlags.DoNotWrapExceptions) != 0) r = _interpreter!.Run(args);
            else
            {
                try { r = _interpreter!.Run(args); }
                catch (Exception e) { throw new TargetInvocationException(e); }
            }
            if (parameters != null)
                for (int i = 0; i < n; i++)
                    if (ps[i].ParameterType.IsByRef) parameters[i] = args[i];
            return r;
        }

        internal RuntimeMethodInfo GetRuntimeMethodInfo()
        {
            _method ??= new RuntimeMethodInfo(_mhandle);
            return _method;
        }

        // il2wasm: the method's IL runs on the interpreter (DynamicMethod.Interpreter.cs)
        private static void create_dynamic_method(DynamicMethod m, string name, MethodAttributes attributes, CallingConventions callingConvention)
        {
            m._interpreter = new IlInterpreter(m, m._ilGenerator!, m._refs, m._parameterTypes, m._returnType ?? typeof(void));
        }

        private void CreateDynMethod()
        {
            // Clearing of ilgen in create_dynamic_method is not yet synchronized for multiple threads
            lock (this)
            {
                if (_interpreter == null)
                {
                    if (_ilGenerator == null || _ilGenerator.ILOffset == 0)
                        throw new InvalidOperationException(SR.Format(SR.InvalidOperation_BadEmptyMethodBody, Name));

                    _ilGenerator.label_fixup(this);

                    // Have to create all DynamicMethods referenced by this one
                    try
                    {
                        // Used to avoid cycles
                        _creating = true;
                        if (_refs != null)
                        {
                            for (int i = 0; i < _refs.Length; ++i)
                            {
                                if (_refs[i] is DynamicMethod m)
                                {
                                    if (!m._creating)
                                        m.CreateDynMethod();
                                }
                            }
                        }
                    }
                    finally
                    {
                        _creating = false;
                    }
                    create_dynamic_method(this, Name, Attributes, CallingConvention);
                    _ilGenerator = null;
                }
            }
        }

        private int AddRef(object reference)
        {
            _refs ??= new object?[4];
            if (_nrefs >= _refs.Length - 1)
            {
                object[] new_refs = new object[_refs.Length * 2];
                Array.Copy(_refs, new_refs, _refs.Length);
                _refs = new_refs;
            }
            _refs[_nrefs] = reference;
            /* Reserved by the runtime */
            _refs[_nrefs + 1] = null;
            _nrefs += 2;
            return _nrefs - 1;
        }

        internal override int GetParametersCount() => GetParametersAsSpan().Length;

        private sealed class DynamicMethodTokenGenerator : ITokenGenerator
        {
            private readonly DynamicMethod _m;

            public DynamicMethodTokenGenerator(DynamicMethod m)
            {
                _m = m;
            }

            public int GetToken(string str)
            {
                return _m.AddRef(str);
            }

            public int GetToken(MethodBase method, Type[] opt_paratypes)
            {
                throw new InvalidOperationException();
            }

            public int GetToken(MemberInfo member, bool create_open_instance)
            {
                return _m.AddRef(member);
            }

            public int GetToken(SignatureHelper helper)
            {
                return _m.AddRef(helper);
            }
        }
    }
}
