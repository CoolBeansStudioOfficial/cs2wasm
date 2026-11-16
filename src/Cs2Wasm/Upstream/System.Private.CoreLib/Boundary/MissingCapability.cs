namespace Cs2Wasm.Runtime
{
    /// <summary>Marks a library member that needs a capability the module does not have (SPEC section 8): a platform
    /// invoke, a file, a socket. The member's body is never emitted; a program that reaches it fails to build, with an
    /// error naming the capability.</summary>
    [System.AttributeUsage(System.AttributeTargets.Method | System.AttributeTargets.Constructor, Inherited = false)]
    public sealed class MissingCapabilityAttribute : System.Attribute
    {
        public MissingCapabilityAttribute(string capability) => Capability = capability;
        public string Capability { get; }
    }
}
