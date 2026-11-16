namespace Cs2Wasm.Runtime
{
    /// <summary>Marks a library member that needs a capability the module does not have (SPEC section 8): a platform
    /// invoke, a file, a socket. The member's body is never emitted; a program that reaches it fails to build, with an
    /// error naming the capability.</summary>
    [System.AttributeUsage(System.AttributeTargets.Method | System.AttributeTargets.Constructor, Inherited = false)]
    public sealed class CapabilityAttribute : System.Attribute
    {
        /// <summary>A boundary member that exists only in builds with this capability ("files": the toolchain's file
        /// system over the host's imports); elsewhere reaching it is a build error, as for [MissingCapability].</summary>
        public CapabilityAttribute(string capability) => Capability = capability;
        public string Capability { get; }
    }

    [System.AttributeUsage(System.AttributeTargets.Method | System.AttributeTargets.Constructor, Inherited = false)]
    public sealed class MissingCapabilityAttribute : System.Attribute
    {
        public MissingCapabilityAttribute(string capability) => Capability = capability;
        public string Capability { get; }
    }
}
