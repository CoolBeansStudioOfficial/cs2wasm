#pragma warning disable CS0436, CS0660, CS0661
// Reflection over the metadata the compiler kept (section 8): records in the data segment (see Compiler/Reflection.cs)
// and thunks (target, object[] args) -> object that call members with boxed arguments.
using System.Collections.Generic;
using System.Reflection;
using Cs2Wasm.Runtime;

namespace Cs2Wasm.Runtime
{
    using System;
    internal static class Reflect
    {
        /// <summary>Calls a thunk by its table index + 1.</summary>
        [Intrinsic] internal static extern object Invoke(int thunk, object target, object[] args);
        internal static int I(int addr, int index) => Mem.LoadI32(addr + 4 * index);
        internal static string S(int addr) => addr == 0 ? null : Rt.Borrow<string>(addr);
        internal static Type TypeAt(int id) => id == -1 ? typeof(void) : Type.FromId(id);

        internal static bool Matches(BindingFlags f, bool isStatic, bool isPublic, bool inherited)
        {
            if (isPublic ? (f & BindingFlags.Public) == 0 : (f & BindingFlags.NonPublic) == 0) return false;
            if (isStatic ? (f & BindingFlags.Static) == 0 : (f & BindingFlags.Instance) == 0) return false;
            if (inherited && (f & BindingFlags.DeclaredOnly) != 0) return false;
            if (inherited && isStatic && (f & BindingFlags.FlattenHierarchy) == 0) return false;
            return true;
        }

        internal static string TypeName(Type t)
        {
            if (t == null) return "";
            return t.IsPrimitive || t == typeof(void) ? t.Name : t.FullName;
        }

        internal static object[] Attributes(int count, int addr, Type filter)
        {
            var list = new List<object>();
            for (int i = 0; i < count; i++)
            {
                var at = Type.FromId(I(addr, 2 * i));
                if (filter != null && !filter.IsAssignableFrom(at)) continue;
                list.Add(Invoke(I(addr, 2 * i + 1), null, null));
            }
            return list.ToArray();
        }
    }
}

namespace System.Reflection
{
    [Flags]
    public enum BindingFlags
    {
        Default = 0, IgnoreCase = 1, DeclaredOnly = 2, Instance = 4, Static = 8, Public = 16, NonPublic = 32, FlattenHierarchy = 64,
        InvokeMethod = 256, CreateInstance = 512, GetField = 1024, SetField = 2048, GetProperty = 4096, SetProperty = 8192,
    }

    [Flags]
    public enum MemberTypes { Constructor = 1, Event = 2, Field = 4, Method = 8, Property = 16, TypeInfo = 32, Custom = 64, NestedType = 128, All = 191 }

    public abstract class MemberInfo
    {
        public abstract string Name { get; }
        public virtual Type DeclaringType => null;
        public virtual MemberTypes MemberType => MemberTypes.Custom;
        internal virtual int AttrCount => 0;
        internal virtual int AttrAddr => 0;
        public virtual object[] GetCustomAttributes(bool inherit) => Reflect.Attributes(AttrCount, AttrAddr, null);
        public virtual object[] GetCustomAttributes(Type attributeType, bool inherit) => Reflect.Attributes(AttrCount, AttrAddr, attributeType);
        public virtual bool IsDefined(Type attributeType, bool inherit) => GetCustomAttributes(attributeType, inherit).Length > 0;
    }

    public sealed class ParameterInfo
    {
        readonly string _name; readonly Type _type; readonly int _pos;
        internal ParameterInfo(string n, Type t, int p) { _name = n; _type = t; _pos = p; }
        public string Name => _name;
        public Type ParameterType => _type;
        public int Position => _pos;
        public override string ToString() => Reflect.TypeName(_type) + " " + _name;
    }

    public abstract class MethodBase : MemberInfo
    {
        internal int _rec, _flags;
        internal Type _decl;
        public override Type DeclaringType => _decl;
        public bool IsStatic => (_flags & 1) != 0;
        public bool IsPublic => (_flags & 2) != 0;
        public bool IsPrivate => (_flags & 32) != 0;
        public bool IsFamily => (_flags & 64) != 0 && (_flags & 128) == 0;
        public bool IsAssembly => (_flags & 128) != 0 && (_flags & 64) == 0;
        public bool IsVirtual => (_flags & 4) != 0;
        public bool IsAbstract => (_flags & 8) != 0;
        public bool IsConstructor => this is ConstructorInfo;
        public abstract ParameterInfo[] GetParameters();
        public object Invoke(object obj, object[] parameters) => InvokeCore(obj, parameters);
        public object Invoke(object obj, BindingFlags invokeAttr, Binder binder, object[] parameters, Globalization.CultureInfo culture) => InvokeCore(obj, parameters);
        internal abstract object InvokeCore(object obj, object[] parameters);

        internal static ParameterInfo[] Params(int n, int types, int names)
        {
            var r = new ParameterInfo[n];
            for (int i = 0; i < n; i++) r[i] = new ParameterInfo(Reflect.S(Reflect.I(names, i)), Type.FromId(Reflect.I(types, i)), i);
            return r;
        }

        internal static object Call(int thunk, object target, object[] args, int expected)
        {
            int n = args == null ? 0 : args.Length;
            if (n != expected) throw new TargetParameterCountException("Parameter count mismatch.");
            if (thunk == 0) throw new NotSupportedException("This member cannot be invoked in cs2wasm (its metadata was kept without code).");
            try { return Reflect.Invoke(thunk, target, args ?? new object[0]); }
            catch (Exception e) { throw new TargetInvocationException(e); }
        }
    }

    public abstract class Binder { }

    public class MethodInfo : MethodBase
    {
        internal MethodInfo() { }
        internal static MethodInfo From(int rec, Type decl)
        {
            var m = new MethodInfo();
            m._rec = rec; m._flags = Reflect.I(rec, 2); m._decl = Type.FromId(Reflect.I(rec, 9));
            return m;
        }
        public override string Name => Reflect.S(Reflect.I(_rec, 0));
        public override MemberTypes MemberType => MemberTypes.Method;
        public virtual Type ReturnType => Reflect.TypeAt(Reflect.I(_rec, 1));
        public override ParameterInfo[] GetParameters() => Params(Reflect.I(_rec, 4), Reflect.I(_rec, 5), Reflect.I(_rec, 6));
        internal override int AttrCount => Reflect.I(_rec, 7);
        internal override int AttrAddr => Reflect.I(_rec, 8);
        internal override object InvokeCore(object obj, object[] parameters)
        {
            if (!IsStatic && obj == null) throw new TargetException("Non-static method requires a target.");
            return Call(Reflect.I(_rec, 3), obj, parameters, Reflect.I(_rec, 4));
        }
        public override string ToString()
        {
            var ps = GetParameters();
            var parts = new string[ps.Length];
            for (int i = 0; i < ps.Length; i++) parts[i] = Reflect.TypeName(ps[i].ParameterType);
            return Reflect.TypeName(ReturnType) + " " + Name + "(" + string.Join(", ", parts) + ")";
        }
        internal string Signature()
        {
            var ps = GetParameters();
            var s = Name + "(";
            for (int i = 0; i < ps.Length; i++) s += ps[i].ParameterType.FullName + ",";
            return s + ")";
        }
    }

    /// <summary>System.Object's public methods, which every type's GetMethods lists after its own.</summary>
    internal sealed class ObjectMethodInfo : MethodInfo
    {
        readonly int _which;
        internal ObjectMethodInfo(int which) { _which = which; _flags = 2 | (which == 0 ? 0 : 4); _decl = typeof(object); }
        public override string Name => _which switch { 0 => "GetType", 1 => "ToString", 2 => "Equals", _ => "GetHashCode" };
        public override Type ReturnType => _which switch { 0 => typeof(Type), 1 => typeof(string), 2 => typeof(bool), _ => typeof(int) };
        public override ParameterInfo[] GetParameters() => _which == 2 ? new[] { new ParameterInfo("obj", typeof(object), 0) } : new ParameterInfo[0];
        internal override object InvokeCore(object obj, object[] parameters)
        {
            if (obj == null) throw new TargetException("Non-static method requires a target.");
            int n = parameters == null ? 0 : parameters.Length;
            if (n != (_which == 2 ? 1 : 0)) throw new TargetParameterCountException("Parameter count mismatch.");
            return _which switch { 0 => obj.GetType(), 1 => obj.ToString(), 2 => obj.Equals(parameters[0]), _ => obj.GetHashCode() };
        }
    }

    public class ConstructorInfo : MethodBase
    {
        internal ConstructorInfo(int rec, Type decl) { _rec = rec; _flags = Reflect.I(rec, 0); _decl = decl; }
        public override string Name => IsStatic ? ".cctor" : ".ctor";
        public override MemberTypes MemberType => MemberTypes.Constructor;
        public override ParameterInfo[] GetParameters() => Params(Reflect.I(_rec, 2), Reflect.I(_rec, 3), Reflect.I(_rec, 4));
        internal override int AttrCount => Reflect.I(_rec, 5);
        internal override int AttrAddr => Reflect.I(_rec, 6);
        public object Invoke(object[] parameters) => Call(Reflect.I(_rec, 1), null, parameters, Reflect.I(_rec, 2));
        internal override object InvokeCore(object obj, object[] parameters) => Call(Reflect.I(_rec, 1), null, parameters, Reflect.I(_rec, 2));
        public override string ToString()
        {
            var ps = GetParameters();
            var parts = new string[ps.Length];
            for (int i = 0; i < ps.Length; i++) parts[i] = Reflect.TypeName(ps[i].ParameterType);
            return "Void " + Name + "(" + string.Join(", ", parts) + ")";
        }
    }

    public class FieldInfo : MemberInfo
    {
        readonly int _rec;
        internal FieldInfo(int rec) { _rec = rec; }
        int Flags => Reflect.I(_rec, 2);
        public override string Name => Reflect.S(Reflect.I(_rec, 0));
        public override MemberTypes MemberType => MemberTypes.Field;
        public Type FieldType => Type.FromId(Reflect.I(_rec, 1));
        public override Type DeclaringType => Type.FromId(Reflect.I(_rec, 4));
        public bool IsStatic => (Flags & 1) != 0;
        public bool IsPublic => (Flags & 2) != 0;
        public bool IsPrivate => (Flags & 32) != 0;
        public bool IsInitOnly => (Flags & 16) != 0;
        internal override int AttrCount => Reflect.I(_rec, 5);
        internal override int AttrAddr => Reflect.I(_rec, 6);
        public object GetValue(object obj)
        {
            if (!IsStatic && obj == null) throw new TargetException("Non-static field requires a target.");
            return Reflect.Invoke(Reflect.I(_rec, 3), obj, null);
        }
        public void SetValue(object obj, object value)
        {
            if (!IsStatic && obj == null) throw new TargetException("Non-static field requires a target.");
            Reflect.Invoke(Reflect.I(_rec, 3), obj, new[] { value });
        }
        public override string ToString() => Reflect.TypeName(FieldType) + " " + Name;
    }

    public class PropertyInfo : MemberInfo
    {
        readonly int _rec;
        internal PropertyInfo(int rec) { _rec = rec; }
        int Flags => Reflect.I(_rec, 2);
        public override string Name => Reflect.S(Reflect.I(_rec, 0));
        public override MemberTypes MemberType => MemberTypes.Property;
        public Type PropertyType => Type.FromId(Reflect.I(_rec, 1));
        public override Type DeclaringType => Type.FromId(Reflect.I(_rec, 5));
        public bool CanRead => Reflect.I(_rec, 3) != 0;
        public bool CanWrite => Reflect.I(_rec, 4) != 0;
        internal bool IsStatic => (Flags & 1) != 0;
        internal bool IsPublicGet => (Flags & 256) != 0;
        internal bool IsPublicSet => (Flags & 512) != 0;
        internal bool AnyPublic => (Flags & (256 | 512)) != 0;
        internal override int AttrCount => Reflect.I(_rec, 6);
        internal override int AttrAddr => Reflect.I(_rec, 7);
        public MethodInfo GetMethod => DeclaringType.FindAccessor("get_" + Name);
        public MethodInfo SetMethod => DeclaringType.FindAccessor("set_" + Name);
        public MethodInfo GetGetMethod() => IsPublicGet ? GetMethod : null;
        public MethodInfo GetSetMethod() => IsPublicSet ? SetMethod : null;
        public ParameterInfo[] GetIndexParameters() => new ParameterInfo[0];
        public object GetValue(object obj)
        {
            if (!CanRead) throw new ArgumentException("Property get method not found.");
            if (!IsStatic && obj == null) throw new TargetException("Non-static method requires a target.");
            return MethodBase.Call(Reflect.I(_rec, 3), obj, null, 0);
        }
        public object GetValue(object obj, object[] index) => GetValue(obj);
        public void SetValue(object obj, object value)
        {
            if (!CanWrite) throw new ArgumentException("Property set method not found.");
            if (!IsStatic && obj == null) throw new TargetException("Non-static method requires a target.");
            MethodBase.Call(Reflect.I(_rec, 4), obj, new[] { value }, 1);
        }
        public void SetValue(object obj, object value, object[] index) => SetValue(obj, value);
        public override string ToString() => Reflect.TypeName(PropertyType) + " " + Name;
    }

    public class TargetInvocationException : ApplicationException
    {
        public TargetInvocationException(Exception inner) : base("Exception has been thrown by the target of an invocation.", inner) { }
        public TargetInvocationException(string message, Exception inner) : base(message, inner) { }
    }
    public class TargetParameterCountException : ApplicationException
    {
        public TargetParameterCountException() : base("Number of parameters specified does not match the expected number.") { }
        public TargetParameterCountException(string message) : base(message) { }
    }
    public class TargetException : ApplicationException
    {
        public TargetException() : base("Non-static method requires a target.") { }
        public TargetException(string message) : base(message) { }
    }
    public sealed class AmbiguousMatchException : SystemException
    {
        public AmbiguousMatchException() : base("Ambiguous match found.") { }
        public AmbiguousMatchException(string message) : base(message) { }
    }

    public static class CustomAttributeExtensions
    {
        public static T GetCustomAttribute<T>(this MemberInfo element) where T : Attribute
        {
            var a = element.GetCustomAttributes(typeof(T), true);
            if (a.Length > 1) throw new AmbiguousMatchException("Multiple custom attributes of the same type '" + typeof(T).FullName + "' found.");
            return a.Length == 0 ? null : (T)a[0];
        }
        public static Attribute GetCustomAttribute(this MemberInfo element, Type attributeType)
        {
            var a = element.GetCustomAttributes(attributeType, true);
            return a.Length == 0 ? null : (Attribute)a[0];
        }
        public static IEnumerable<T> GetCustomAttributes<T>(this MemberInfo element) where T : Attribute
        {
            var list = new List<T>();
            foreach (var a in element.GetCustomAttributes(typeof(T), true)) list.Add((T)a);
            return list;
        }
        public static IEnumerable<Attribute> GetCustomAttributes(this MemberInfo element)
        {
            var list = new List<Attribute>();
            foreach (var a in element.GetCustomAttributes(true)) list.Add((Attribute)a);
            return list;
        }
        public static bool IsDefined(this MemberInfo element, Type attributeType) => element.IsDefined(attributeType, true);
    }
}

namespace System
{
    public class MissingMethodException : MissingMemberException
    {
        public MissingMethodException() : base("Attempted to access a missing method.") { }
        public MissingMethodException(string message) : base(message) { }
    }
    public class MissingMemberException : MemberAccessException
    {
        public MissingMemberException() : base("Attempted to access a missing member.") { }
        public MissingMemberException(string message) : base(message) { }
    }
    public class MemberAccessException : SystemException
    {
        public MemberAccessException() : base("Cannot access member.") { }
        public MemberAccessException(string message) : base(message) { }
    }

    public static class Activator
    {
        public static object CreateInstance(Type type)
        {
            if (type == null) throw new ArgumentNullException("type");
            foreach (var c in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                if (c.GetParameters().Length == 0) return c.Invoke(null);
            throw new MissingMethodException("Cannot dynamically create an instance of type '" + type.FullName + "'. Reason: No parameterless constructor defined.");
        }
        public static object CreateInstance(Type type, params object[] args)
        {
            if (args == null || args.Length == 0) return CreateInstance(type);
            foreach (var c in type.GetConstructors())
            {
                var ps = c.GetParameters();
                if (ps.Length != args.Length) continue;
                bool ok = true;
                for (int i = 0; i < ps.Length && ok; i++) ok = args[i] == null ? !ps[i].ParameterType.IsValueType : ps[i].ParameterType.IsAssignableFrom(args[i].GetType());
                if (ok) return c.Invoke(args);
            }
            throw new MissingMethodException("Constructor on type '" + type.FullName + "' not found.");
        }
        public static T CreateInstance<T>() => (T)CreateInstance(typeof(T));
    }

    public abstract partial class Attribute
    {
        public static Attribute GetCustomAttribute(System.Reflection.MemberInfo element, Type attributeType) => System.Reflection.CustomAttributeExtensions.GetCustomAttribute(element, attributeType);
        public static Attribute[] GetCustomAttributes(System.Reflection.MemberInfo element)
        {
            var o = element.GetCustomAttributes(true);
            var r = new Attribute[o.Length];
            for (int i = 0; i < o.Length; i++) r[i] = (Attribute)o[i];
            return r;
        }
        public static bool IsDefined(System.Reflection.MemberInfo element, Type attributeType) => element.IsDefined(attributeType, true);
    }

    public sealed partial class Type
    {
        int Meta => MetaOf(_id);
        public override MemberTypes MemberType => MemberTypes.TypeInfo;
        internal override int AttrCount => Meta == 0 ? 0 : Reflect.I(Meta, 8);
        internal override int AttrAddr => Meta == 0 ? 0 : Reflect.I(Meta, 9);
        public Type BaseType
        {
            get
            {
                if (Meta != 0) { int b = Reflect.I(Meta, 10); return b == 0 ? null : FromId(b - 1); }
                return IsInterface || this == typeof(object) ? null : IsValueType ? (IsEnum ? typeof(Enum) : typeof(ValueType)) : typeof(object);
            }
        }
        public Type[] GetInterfaces()
        {
            if (Meta == 0) return new Type[0];
            int n = Reflect.I(Meta, 11), a = Reflect.I(Meta, 12);
            var r = new Type[n];
            for (int i = 0; i < n; i++) r[i] = FromId(Reflect.I(a, i));
            return r;
        }
        public bool IsAssignableFrom(Type c)
        {
            if (c == null) return false;
            if (c == this || this == typeof(object)) return true;
            for (var b = c.BaseType; b != null; b = b.BaseType) if (b == this) return true;
            if (IsInterface) foreach (var i in c.GetInterfaces()) if (i == this) return true;
            return false;
        }
        public bool IsInstanceOfType(object o) => o != null && IsAssignableFrom(o.GetType());
        public bool IsSubclassOf(Type c) => c != this && c != null && c.IsAssignableFrom(this) && !c.IsInterface;
        public bool IsPublic => true;
        public bool IsAbstract => false;
        public bool IsSealed => false;

        const BindingFlags DefaultLookup = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

        // ---- methods ----
        public MethodInfo[] GetMethods() => GetMethods(DefaultLookup);
        public MethodInfo[] GetMethods(BindingFlags flags)
        {
            var list = new List<MethodInfo>();
            var seen = new List<string>();
            int m = Meta;
            if (m != 0)
            {
                int n = Reflect.I(m, 4), a = Reflect.I(m, 5);
                for (int i = 0; i < n; i++)
                {
                    var mi = MethodInfo.From(a + 40 * i, this);
                    bool inherited = mi.DeclaringType != this;
                    if (!Reflect.Matches(flags, mi.IsStatic, mi.IsPublic, inherited)) continue;
                    var sig = mi.Signature();
                    if (mi.IsVirtual && seen.Contains(sig)) continue; // an override hides the base method
                    seen.Add(sig);
                    list.Add(mi);
                }
            }
            if (!IsInterface && (flags & BindingFlags.Public) != 0 && (flags & BindingFlags.Instance) != 0 && (flags & BindingFlags.DeclaredOnly) == 0)
                for (int w = 0; w < 4; w++)
                {
                    var om = new ObjectMethodInfo(w);
                    if (w != 0 && seen.Contains(om.Signature())) continue;
                    list.Add(om);
                }
            return list.ToArray();
        }
        public MethodInfo GetMethod(string name) => GetMethod(name, DefaultLookup);
        public MethodInfo GetMethod(string name, BindingFlags flags)
        {
            if (name == null) throw new ArgumentNullException("name");
            MethodInfo found = null;
            foreach (var m in GetMethods(flags))
            {
                if (m.Name != name) continue;
                if (found != null && found.DeclaringType == m.DeclaringType) throw new AmbiguousMatchException("Ambiguous match found for '" + FullName + " " + m.Name + "'.");
                found ??= m;
            }
            return found;
        }
        public MethodInfo GetMethod(string name, Type[] types)
        {
            foreach (var m in GetMethods(DefaultLookup))
            {
                if (m.Name != name) continue;
                var ps = m.GetParameters();
                if (ps.Length != types.Length) continue;
                bool ok = true;
                for (int i = 0; i < ps.Length && ok; i++) ok = ps[i].ParameterType == types[i];
                if (ok) return m;
            }
            return null;
        }
        internal MethodInfo FindAccessor(string name) => GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

        // ---- constructors ----
        public ConstructorInfo[] GetConstructors() => GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        public ConstructorInfo[] GetConstructors(BindingFlags flags)
        {
            var list = new List<ConstructorInfo>();
            int m = Meta;
            if (m != 0)
            {
                int n = Reflect.I(m, 6), a = Reflect.I(m, 7);
                for (int i = 0; i < n; i++)
                {
                    var c = new ConstructorInfo(a + 28 * i, this);
                    if (Reflect.Matches(flags, c.IsStatic, c.IsPublic, false)) list.Add(c);
                }
            }
            return list.ToArray();
        }
        public ConstructorInfo GetConstructor(Type[] types)
        {
            foreach (var c in GetConstructors())
            {
                var ps = c.GetParameters();
                if (ps.Length != types.Length) continue;
                bool ok = true;
                for (int i = 0; i < ps.Length && ok; i++) ok = ps[i].ParameterType == types[i];
                if (ok) return c;
            }
            return null;
        }

        // ---- fields ----
        public FieldInfo[] GetFields() => GetFields(DefaultLookup);
        public FieldInfo[] GetFields(BindingFlags flags)
        {
            var list = new List<FieldInfo>();
            int m = Meta;
            if (m != 0)
            {
                int n = Reflect.I(m, 0), a = Reflect.I(m, 1);
                for (int i = 0; i < n; i++)
                {
                    var f = new FieldInfo(a + 28 * i);
                    bool inherited = f.DeclaringType != this;
                    if (inherited && f.IsPrivate) continue;
                    if (Reflect.Matches(flags, f.IsStatic, f.IsPublic, inherited)) list.Add(f);
                }
            }
            return list.ToArray();
        }
        public FieldInfo GetField(string name) => GetField(name, DefaultLookup);
        public FieldInfo GetField(string name, BindingFlags flags)
        {
            foreach (var f in GetFields(flags)) if (f.Name == name) return f;
            return null;
        }

        // ---- properties ----
        public PropertyInfo[] GetProperties() => GetProperties(DefaultLookup);
        public PropertyInfo[] GetProperties(BindingFlags flags)
        {
            var list = new List<PropertyInfo>();
            var seen = new List<string>();
            int m = Meta;
            if (m != 0)
            {
                int n = Reflect.I(m, 2), a = Reflect.I(m, 3);
                for (int i = 0; i < n; i++)
                {
                    var p = new PropertyInfo(a + 32 * i);
                    bool inherited = p.DeclaringType != this;
                    if (!Reflect.Matches(flags, p.IsStatic, p.AnyPublic, inherited)) continue;
                    if (seen.Contains(p.Name)) continue;
                    seen.Add(p.Name);
                    list.Add(p);
                }
            }
            return list.ToArray();
        }
        public PropertyInfo GetProperty(string name) => GetProperty(name, DefaultLookup);
        public PropertyInfo GetProperty(string name, BindingFlags flags)
        {
            if (name == null) throw new ArgumentNullException("name");
            foreach (var p in GetProperties(flags)) if (p.Name == name) return p;
            return null;
        }

        public MemberInfo[] GetMembers() => GetMembers(DefaultLookup);
        public MemberInfo[] GetMembers(BindingFlags flags)
        {
            var list = new List<MemberInfo>();
            list.AddRange(GetMethods(flags));
            list.AddRange(GetConstructors(flags));
            list.AddRange(GetProperties(flags));
            list.AddRange(GetFields(flags));
            return list.ToArray();
        }
    }
}
