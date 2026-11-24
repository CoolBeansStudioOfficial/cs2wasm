#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;

namespace Il2Wasm.IL.Types;

/// <summary>il2wasm's type system context over NativeAOT's (TypeSystem/): the program's assemblies, loaded by simple
/// name from the files given, for a wasm32 target (pointers of 4 bytes, SPEC 3), with NativeAOT's metadata layout,
/// virtual and interface resolution and canonical forms (__Canon) for shared generic code.</summary>
public sealed class IlContext : MetadataTypeSystemContext
{
    readonly Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, EcmaModule> _modules = new(StringComparer.OrdinalIgnoreCase);
    readonly MetadataFieldLayoutAlgorithm _layout = new Il2WasmFieldLayout();
    readonly MetadataRuntimeInterfacesAlgorithm _interfaces = new();
    readonly MetadataVirtualMethodAlgorithm _virtuals = new();
    RuntimeInterfacesAlgorithm? _arrayInterfaces;

    /// <summary>The object header il2wasm's runtime uses: count, type id, hash code (12 bytes).</summary>
    public const int ObjectHeader = 12;

    public IlContext() : base(new TargetDetails(TargetArchitecture.Wasm32, TargetOS.Browser, TargetAbi.Unknown)) { }

    /// <summary>Makes the assemblies at these paths loadable (by their file's simple name); the first named
    /// <paramref name="coreLib"/> is the system module.</summary>
    public void AddAssemblies(IEnumerable<string> paths)
    {
        foreach (var p in paths) _paths[Path.GetFileNameWithoutExtension(p)] = p;
    }

    public EcmaModule Load(string simpleName, bool throwIfNotFound = true)
    {
        if (_modules.TryGetValue(simpleName, out var m)) return m;
        if (!_paths.TryGetValue(simpleName, out var path))
        {
            if (throwIfNotFound) throw new FileNotFoundException($"assembly '{simpleName}' is not among the inputs");
            return null!;
        }
        var pe = new PEReader(File.OpenRead(path));
        m = EcmaModule.Create(this, pe, containingAssembly: null);
        _modules[simpleName] = m;
        return m;
    }

    /// <summary>The file an assembly was loaded from (by its simple name), or null.</summary>
    public string? PathOf(string simpleName) => _paths.TryGetValue(simpleName, out var p) ? p : null;

    public void SetCoreLib(string simpleName = "System.Private.CoreLib") => SetSystemModule(Load(simpleName));

    public override ModuleDesc ResolveAssembly(AssemblyNameInfo name, bool throwIfNotFound) =>
        Load(name.Name!, throwIfNotFound);

    public override FieldLayoutAlgorithm GetLayoutAlgorithmForType(DefType type) => _layout;

    // (a T[]'s interfaces: NativeAOT's CoreLib declares them on Array<T>; the IL CoreLib (Mono's) has no Array<T>, and
    // its arrays implement Array's interfaces and IEnumerable<T>, ICollection<T>, IList<T>, IReadOnlyList<T> and
    // IReadOnlyCollection<T> of their element type, as the CLR's do)
    protected override RuntimeInterfacesAlgorithm GetRuntimeInterfacesAlgorithmForNonPointerArrayType(ArrayType type) =>
        _arrayInterfaces ??= SystemModule.GetType("System"u8, "Array`1"u8, throwIfNotFound: false) is MetadataType arrayOfT
            ? new ArrayOfTRuntimeInterfacesAlgorithm(arrayOfT) : new SimpleArrayOfTRuntimeInterfacesAlgorithm(SystemModule);

    protected override RuntimeInterfacesAlgorithm GetRuntimeInterfacesAlgorithmForDefType(DefType type) => _interfaces;

    public override VirtualMethodAlgorithm GetVirtualMethodAlgorithmForType(TypeDesc type) => _virtuals;

    protected internal override Instantiation ConvertInstantiationToCanonForm(Instantiation instantiation, CanonicalFormKind kind, out bool changed) =>
        StandardCanonicalizationAlgorithm.ConvertInstantiationToCanonForm(instantiation, kind, out changed);

    protected internal override TypeDesc ConvertToCanon(TypeDesc typeToConvert, CanonicalFormKind kind) =>
        StandardCanonicalizationAlgorithm.ConvertToCanon(typeToConvert, kind);

    protected internal override TypeDesc ConvertToCanon(TypeDesc typeToConvert, ref CanonicalFormKind kind) =>
        StandardCanonicalizationAlgorithm.ConvertToCanon(typeToConvert, kind);

    protected internal override bool ComputeHasGCStaticBase(FieldDesc field)
    {
        if (field.IsThreadStatic) return true;
        var ft = field.FieldType;
        return ft.IsValueType ? ((DefType)ft).ContainsGCPointers : ft.IsGCPointer;
    }

    public override bool SupportsCanon => true;
    public override bool SupportsUniversalCanon => false;

    /// <summary>The offset of an instance field in il2wasm's layout: from the object's address for a class (System.Object
    /// is laid out as the 12-byte header, so a class's fields follow it), from the start of the value for a struct.</summary>
    public static int FieldOffset(FieldDesc f) => f.Offset.AsInt;

    /// <summary>The bytes an object of this class takes, header included; a value type's size.</summary>
    public static int InstanceSize(DefType t) => t.IsValueType ? t.InstanceFieldSize.AsInt : t.InstanceByteCount.AsInt;
}

/// <summary>NativeAOT's metadata field layout, with statics in plain blocks (no MethodTable pointer before the GC
/// statics: il2wasm's runtime finds references in statics from the compiler's tables).</summary>
sealed class Il2WasmFieldLayout : MetadataFieldLayoutAlgorithm
{
    protected override void PrepareRuntimeSpecificStaticFieldLayout(TypeSystemContext context, ref ComputedStaticFieldLayout layout) { }
    protected override void FinalizeRuntimeSpecificStaticFieldLayout(TypeSystemContext context, ref ComputedStaticFieldLayout layout) { }

    /// <summary>System.Object is il2wasm's object header (count, type id, hash: 12 bytes), and every class's fields
    /// follow it, as NativeAOT's follow the MethodTable pointer. A field Object declares (NativeAOT's CoreLib has the
    /// MethodTable pointer) is the type id's word.</summary>
    public override ComputedInstanceFieldLayout ComputeInstanceLayout(DefType defType, InstanceLayoutKind layoutKind)
    {
        if (!defType.IsObject) return base.ComputeInstanceLayout(defType, layoutKind);
        var four = new LayoutInt(4);
        var fields = new List<FieldAndOffset>();
        foreach (var f in defType.GetFields()) if (!f.IsStatic) fields.Add(new FieldAndOffset(f, four));
        return new ComputedInstanceFieldLayout
        {
            ByteCountUnaligned = new LayoutInt(IlContext.ObjectHeader), ByteCountAlignment = four,
            FieldSize = new LayoutInt(IlContext.ObjectHeader), FieldAlignment = four,
            LayoutAbiStable = true, IsAutoLayoutOrHasAutoLayoutFields = true, Offsets = fields.ToArray(),
        };
    }

    protected override ComputedInstanceFieldLayout ComputeInstanceFieldLayout(MetadataType type, int numInstanceFields)
    {
        var lm = type.GetClassLayout();
        // (a class with layout over a base without it: auto, as CoreCLR lays it out)
        if (!type.IsValueType && type.BaseType is MetadataType { IsObject: false, IsAutoLayout: true })
            return ComputeAutoFieldLayout(type, numInstanceFields, lm);
        return lm.Kind switch
        {
            MetadataLayoutKind.Explicit => ComputeExplicitFieldLayout(type, numInstanceFields, lm),
            MetadataLayoutKind.Sequential => ComputeSequentialFieldLayout(type, numInstanceFields, lm),
            MetadataLayoutKind.CStruct => ComputeCStructFieldLayout(type, numInstanceFields),
            MetadataLayoutKind.CUnion => ComputeCUnionFieldLayout(type, numInstanceFields),
            _ when type.IsEnum => ComputeSequentialFieldLayout(type, numInstanceFields, lm),
            _ => ComputeAutoFieldLayout(type, numInstanceFields, lm),
        };
    }
}
