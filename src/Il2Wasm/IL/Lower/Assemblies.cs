#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using System.Text;
using Il2Wasm.IL.Types;
using Il2Wasm.Wasm;
using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;
using WOp = Il2Wasm.Wasm.Op;

namespace Il2Wasm.IL.Lower;

/// <summary>The reflection records of assemblies (SPEC 6.6), read by the runtime's RuntimeAssembly and CustomAttribute
/// (TypeInfo.AssemblyNames, ManifestResources): each assembly's full name, its custom attributes the program can ask
/// for, and the manifest resources (a stock library's SR messages come from its .resources, as .NET reads them when
/// UseSystemResourceKeys is off).
/// <list type="bullet">
/// <item>The assemblies: the program's first, then every one loaded, by name. A type's assembly is its definition's
/// (an array's, its element's).</item>
/// <item>An assembly attribute is kept when the program can ask for it: its type, or a base of it, is a Type object some
/// code that can run names (typeof, ldtoken: GetCustomAttribute&lt;T&gt;; System.Attribute itself keeps them all). Each
/// kept one has a factory, a function in the table that makes it as its blob says (constructor arguments and named
/// fields and properties of primitive, enum, string and Type values); one whose blob holds anything else is not kept,
/// with a warning. GetCustomAttributes makes an array of the attribute's type, so those array types are made too.</item>
/// <item>The manifest resources of the assemblies a reflectable type is in (ILLink's own are not resources of the
/// program), each kept packed in a passive segment and copied out when a stream reads it.</item>
/// </list></summary>
public sealed partial class IlCompiler
{
    internal static readonly FuncType ThunkType = new([ValType.I32, ValType.I32], [ValType.I32]);
    bool _assembliesWanted, _resourcesWanted;
    EcmaModule? _programModule;
    readonly List<EcmaModule> _assemblyList = [];
    readonly Dictionary<EcmaModule, List<(TypeDesc Type, WasmFunction Factory)>> _assemblyAttributes = new();
    readonly HashSet<(EcmaModule, CustomAttributeHandle)> _attributesDone = [];
    Blob? _assemblyTable, _resourceTable;
    WasmFunction? _resourceInit, _resourceUnpack;
    DataSegment? _resourceSegment;

    WasmFunction ResourceInitFn => _resourceInit ??= Module.AddFunction("rt.resource_init", new FuncType([ValType.I32, ValType.I32, ValType.I32], []));
    WasmFunction ResourceUnpackFn => _resourceUnpack ??= Module.AddFunction("rt.resource_unpack", new FuncType([ValType.I32, ValType.I32, ValType.I32], []));

    int AssemblyIndex(EcmaModule m)
    {
        if (_assemblyList.Count == 0 && _programModule != null) _assemblyList.Add(_programModule);
        int i = _assemblyList.IndexOf(m);
        if (i >= 0) return i;
        _assemblyList.Add(m);
        return _assemblyList.Count - 1;
    }

    int AssemblyIndexOf(TypeDesc t)
    {
        while (t is ParameterizedType p) t = p.ParameterType;
        return t.GetTypeDefinition() is EcmaType et ? AssemblyIndex((EcmaModule)et.Module) : 0;
    }

    /// <summary>The assemblies' attributes the program can ask for, with their factories; true when anything new was
    /// made (its code is to be compiled).</summary>
    bool RequestAssemblyRecords()
    {
        if (!_assembliesWanted) return false;
        bool any = false;
        if (_assemblyList.Count == 0 && _programModule != null) _assemblyList.Add(_programModule);
        foreach (var m in _ctx.Modules.OrderBy(x => x.Assembly.GetName().Name, StringComparer.Ordinal).ToList()) AssemblyIndex(m);
        var attributeType = _ctx.SystemModule.GetType("System"u8, "Attribute"u8, throwIfNotFound: false);
        bool all = attributeType != null && Facts.TypeObjects.Contains(attributeType) || Il2Wasm.IL.Reach.ProgramFacts.Off;
        foreach (var m in _assemblyList.ToList())
        {
            if (!_assemblyAttributes.TryGetValue(m, out var list)) _assemblyAttributes[m] = list = [];
            var md = m.MetadataReader;
            foreach (var h in md.GetAssemblyDefinition().GetCustomAttributes())
            {
                if (_attributesDone.Contains((m, h))) continue;
                var ca = md.GetCustomAttribute(h);
                if (m.GetObject(ca.Constructor, NotFoundBehavior.ReturnNull) is not MethodDesc ctor) { _attributesDone.Add((m, h)); continue; }
                var at = ctor.OwningType;
                bool wanted = all;
                for (var b = at; b != null && !wanted && !b.IsObject; b = b.BaseType) if (Facts.TypeObjects.Contains(b) && b != attributeType) wanted = true;
                if (!wanted) continue;
                _attributesDone.Add((m, h));
                var f = AttributeFactory(m, ca, ctor);
                if (f == null) continue;
                list.Add((at, f));
                any = true;
            }
        }
        return any;
    }

    WasmFunction? AttributeFactory(EcmaModule m, CustomAttribute ca, MethodDesc ctor)
    {
        CustomAttributeValue<TypeDesc> v;
        try { v = ca.DecodeValue(new CustomAttributeTypeProvider(m)); }
        catch (Exception ex) when (ex is BadImageFormatException or TypeSystemException) { Warnings.Add($"the attribute {ctor.OwningType} of {m.Assembly.GetName().Name} cannot be decoded: not kept"); return null; }
        var t = (DefType)ctor.OwningType;
        var f = Module.AddFunction($"attribute {t} of {m.Assembly.GetName().Name}", ThunkType);
        f.TableRoot = true;
        var e = new Emitter(f);
        int o = e.AddLocal(ValType.I32);
        bool ok = true;
        Constructed(t);
        Reflectable(t);
        // (GetCustomAttributes makes an array of the attribute's type, and reads AttributeUsage of it)
        for (TypeDesc? b = t; b != null && !b.IsObject; b = b.BaseType) Constructed(b.MakeArrayType());
        if (_ctx.SystemModule.GetType("System"u8, "AttributeUsageAttribute"u8, throwIfNotFound: false) is { } au) Constructed(au.MakeArrayType());
        CctorCheckForCall(e, ctor);
        e.I32(IlContext.InstanceSize(t)); e.Tid(TypeId(t)); e.Call(Rt.AllocObject); e.Set(o);
        e.Get(o);
        for (int i = 0; i < v.FixedArguments.Length && ok; i++) ok = AttributeValue(e, ctor.Signature[i], v.FixedArguments[i].Value);
        if (ok) e.Call(FunctionFor(ctor));
        foreach (var na in v.NamedArguments)
        {
            if (!ok) break;
            if (na.Kind == CustomAttributeNamedArgumentKind.Field)
            {
                FieldDesc? fd = null;
                for (TypeDesc? b = t; b != null && fd == null; b = b.BaseType) fd = b.GetFields().FirstOrDefault(x => !x.IsStatic && x.GetName() == na.Name);
                if (fd == null || Il2Wasm.IL.Ir.Kinds.Of(fd.FieldType) is Il2Wasm.IL.Ir.VK.Struct) { ok = false; break; }
                e.Get(o);
                ok = AttributeValue(e, fd.FieldType, na.Value);
                if (!ok) break;
                var k = Il2Wasm.IL.Ir.Kinds.Of(fd.FieldType);
                if (k == Il2Wasm.IL.Ir.VK.Ref) e.Call(Rt.Retain);
                int off = IlContext.FieldOffset(fd);
                switch (Il2Wasm.IL.Ir.Kinds.SizeOf(fd.FieldType), k)
                {
                    case (1, _): e.Store(WOp.I32Store8, off); break;
                    case (2, _): e.Store(WOp.I32Store16, off); break;
                    case (_, Il2Wasm.IL.Ir.VK.I64): e.Store(WOp.I64Store, off); break;
                    case (_, Il2Wasm.IL.Ir.VK.F32): e.Store(WOp.F32Store, off); break;
                    case (_, Il2Wasm.IL.Ir.VK.F64): e.Store(WOp.F64Store, off); break;
                    default: e.Store(WOp.I32Store, off); break;
                }
            }
            else
            {
                MethodDesc? setter = null;
                for (TypeDesc? b = t; b != null && setter == null; b = b.BaseType) setter = b.GetMethods().FirstOrDefault(x => !x.Signature.IsStatic && x.Signature.Length == 1 && x.GetName() == "set_" + na.Name);
                if (setter == null) { ok = false; break; }
                e.Get(o);
                ok = AttributeValue(e, setter.Signature[0], na.Value);
                if (ok) e.Call(FunctionFor(setter));
            }
        }
        if (!ok)
        {
            Warnings.Add($"the attribute {t} of {m.Assembly.GetName().Name} has a value the factories do not make (an array or a boxed value): not kept");
            f.Body.Clear(); f.Locals.Clear(); new Emitter(f).I32(0);
            return null;
        }
        e.Get(o);
        return f;
    }

    /// <summary>A constructor argument or named value of the attribute blob, as the parameter takes it.</summary>
    bool AttributeValue(Emitter e, TypeDesc pt, object? value)
    {
        var t = pt.IsEnum ? pt.UnderlyingType : pt;
        switch (t.Category)
        {
            case TypeFlags.Boolean: e.I32(value is true ? 1 : 0); return true;
            case TypeFlags.Char: e.I32(value is char c ? c : 0); return true;
            case TypeFlags.SByte: case TypeFlags.Byte: case TypeFlags.Int16: case TypeFlags.UInt16: case TypeFlags.Int32: case TypeFlags.UInt32:
                e.I32(unchecked((int)Convert.ToInt64(value ?? 0))); return true;
            case TypeFlags.Int64: e.I64(Convert.ToInt64(value ?? 0L)); return true;
            case TypeFlags.UInt64: e.I64(unchecked((long)Convert.ToUInt64(value ?? 0UL))); return true;
            case TypeFlags.Single: e.F32(Convert.ToSingle(value ?? 0f)); return true;
            case TypeFlags.Double: e.F64(Convert.ToDouble(value ?? 0d)); return true;
        }
        if (t.IsString) { if (value is string s) e.I32Addr(StringLiteral(s)); else e.I32(0); return true; }
        if (t is MetadataType { Name: var _ } mt && mt.GetName() == "Type" && mt.GetNamespace() == "System")
        {
            if (value is not TypeDesc td) { e.I32(0); return true; }
            Reflectable(td);
            e.Tid(TypeId(td)); e.Call(FunctionFor(CoreMethod("System", "Type", "FromId")));
            return true;
        }
        if (value == null && !t.IsValueType) { e.I32(0); return true; }
        return false;
    }

    /// <summary>The assembly table ([count, full name..., (attribute count, list)...]; a list is (type id, factory
    /// + 1) pairs) and the manifest resources ([count, (assembly, name, offset, length, packed length)...]).</summary>
    void EmitAssemblyTables(Func<TypeDesc, int> final)
    {
        if (!_assembliesWanted) return;
        if (_assemblyList.Count == 0 && _programModule != null) _assemblyList.Add(_programModule);
        var words = new List<int>();
        var relocs = new List<(int, IAddress)>();
        words.Add(_assemblyList.Count);
        foreach (var m in _assemblyList) { relocs.Add((words.Count * 4, StringLiteral(FullName(m)))); words.Add(0); }
        foreach (var m in _assemblyList)
        {
            var list = _assemblyAttributes.GetValueOrDefault(m) ?? [];
            if (list.Count == 0) { words.Add(0); words.Add(0); continue; }
            var pairs = list.SelectMany(a => new[] { final(a.Type), Module.TableSlot(a.Factory) + 1 }).ToArray();
            var lb = DataBlob([.. pairs.SelectMany(BitConverter.GetBytes)], 4);
            words.Add(list.Count);
            relocs.Add((words.Count * 4, lb)); words.Add(0);
        }
        _assemblyTable = DataBlob([.. words.SelectMany(BitConverter.GetBytes)], 4, relocs);
        if (!_resourcesWanted) return;
        // the resources of the assemblies a type the program can see is in
        var seen = new HashSet<EcmaModule>();
        foreach (var t in _types)
        {
            if (!IsReflectable(t)) continue;
            var x = t;
            while (x is ParameterizedType p) x = p.ParameterType;
            if (x.GetTypeDefinition() is EcmaType et) seen.Add((EcmaModule)et.Module);
        }
        var entries = new List<(string Asm, string Name, byte[] Data)>();
        foreach (var m in _assemblyList.Where(seen.Contains))
        {
            var md = m.MetadataReader;
            foreach (var h in md.ManifestResources)
            {
                var r = md.GetManifestResource(h);
                var name = md.GetString(r.Name);
                if (!r.Implementation.IsNil || name.StartsWith("ILLink.", StringComparison.Ordinal)) continue;
                var dir = m.PEReader.PEHeaders.CorHeader!.ResourcesDirectory;
                var block = m.PEReader.GetSectionData(dir.RelativeVirtualAddress);
                var reader = block.GetReader((int)r.Offset, block.Length - (int)r.Offset);
                int len = reader.ReadInt32();
                entries.Add((m.Assembly.GetName().Name!, name, reader.ReadBytes(len)));
            }
        }
        const int entry = 20;
        var table = new byte[4 + entry * entries.Count];
        BitConverter.GetBytes(entries.Count).CopyTo(table, 0);
        var trel = new List<(int, IAddress)>();
        _resourceSegment = new DataSegment();
        for (int i = 0; i < entries.Count; i++)
        {
            var data = entries[i].Data;
            var packed = data.Length >= 64 ? DataCompression.Compress(data) : data;
            if (packed.Length + 16 >= data.Length) packed = data;
            int at = 4 + entry * i;
            BitConverter.GetBytes(_resourceSegment.Bytes.Count).CopyTo(table, at + 8);
            BitConverter.GetBytes(data.Length).CopyTo(table, at + 12);
            BitConverter.GetBytes(packed.Length).CopyTo(table, at + 16);
            _resourceSegment.Bytes.AddRange(packed);
            trel.Add((at, StringLiteral(entries[i].Asm)));
            trel.Add((at + 4, StringLiteral(entries[i].Name)));
        }
        if (_resourceSegment.Bytes.Count == 0) _resourceSegment.Bytes.Add(0);
        Module.Data.Add(_resourceSegment);
        _resourceTable = DataBlob(table, 4, trel);
        if (_resourceInit != null) { var e = new Emitter(_resourceInit); e.Get(0); e.Get(1); e.Get(2); e.Emit(WOp.MemoryInit, 0, 0, _resourceSegment); }
        if (_resourceUnpack != null) DataCompression.EmitDecoder(new Emitter(_resourceUnpack));
    }

    /// <summary>An assembly's display name, as .NET gives it: name, version, culture, public key token.</summary>
    static string FullName(EcmaModule m)
    {
        var md = m.MetadataReader;
        var def = md.GetAssemblyDefinition();
        var culture = md.GetString(def.Culture);
        var key = md.GetBlobBytes(def.PublicKey);
        string token = "null";
        if (key.Length > 0)
        {
            var hash = System.Security.Cryptography.SHA1.HashData(key);
            token = string.Concat(hash.Skip(hash.Length - 8).Reverse().Select(b => b.ToString("x2")));
        }
        return $"{md.GetString(def.Name)}, Version={def.Version}, Culture={(culture.Length == 0 ? "neutral" : culture)}, PublicKeyToken={token}";
    }
}
