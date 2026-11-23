using Microsoft.CodeAnalysis;

namespace Il2Wasm;

/// <summary>(Version 5.) What the analyses cannot follow at an unsure site.</summary>
public enum UnsureKind
{
    /// <summary>A delegate is invoked whose target is not known there (a field's, a parameter's, a call's delegate,
    /// an event's handlers): what runs, and what it reads and writes, is not in the call graph from here.</summary>
    UnknownDelegate,
    /// <summary>Reflection: a member reached by its name, a method invoked, a value read or written, an object made
    /// from a type value; or reflection the compiler itself cannot follow (its warning CW0201).</summary>
    Reflection,
    /// <summary>A `dynamic` operation: which member it reaches is decided at run time.</summary>
    Dynamic,
    /// <summary>A member, or an entry of the collection it holds, used through a reference (a `ref`, `out` or `in`
    /// argument, a `ref` local, a `ref` return or assignment): read or written where no hook follows.</summary>
    Reference,
    /// <summary>A collection a member holds leaves the place where its entries are followed: handed to the program's
    /// own method, stored in another place, returned, given out as a span or memory over its entries, or reached
    /// through a local that is not always the member.</summary>
    CollectionEscape,
    /// <summary>The address of a member or an entry is taken, or memory is reinterpreted (`Unsafe`, `MemoryMarshal`).</summary>
    Unsafe,
}

/// <summary>(Version 5.) A spot in a reachable source method that the analyses cannot be sure about (section 7.1:
/// aliases, unresolved delegates, reflection): a plugin that must see every read and write treats what it touches as
/// unknown. <see cref="Member"/> is the member concerned, when there is one.</summary>
public sealed record UnsureSite(UnsureKind Kind, IMethodSymbol Method, IOperation Operation, string Reason, ISymbol? Member = null)
{
    public Location Location => Operation.Syntax.GetLocation();
}
