// Attribute.GetCustomAttribute of attribute types the program never names as `new T[]`: the query makes its result as
// an array of the attribute's type (Array.CreateInstance), and the type's Type object is the only thing the program
// has, passed on from a variable. The attribute types must have their array types made at compile time, and the
// types' attributes must be in their reflection records, or the queries find nothing.
using System;

[AttributeUsage(AttributeTargets.Class)]
sealed class TagAttribute : Attribute
{
    public readonly int Value;
    public TagAttribute(int value) { Value = value; }
}

[AttributeUsage(AttributeTargets.Class)]
sealed class NameAttribute : Attribute
{
    public readonly string Name;
    public NameAttribute(string name) { Name = name; }
}

[Tag(7), Name("seven")] sealed class Seven { }
[Tag(11)] sealed class Eleven { }
sealed class Plain { }
static class Program
{
    static int Run(int input)
    {
        Type type = input > 0 ? typeof(Seven) : input == 0 ? typeof(Eleven) : typeof(Plain);
        var tag = (TagAttribute?)Attribute.GetCustomAttribute(type, typeof(TagAttribute));
        var name = (NameAttribute?)Attribute.GetCustomAttribute(type, typeof(NameAttribute));
        int hash = (tag?.Value ?? -1) * 7 + (name?.Name.Length ?? -1);
        return hash + 1000;
    }

    static void Main()
    {
        Console.WriteLine(Run(1));
        Console.WriteLine(Run(0));
        Console.WriteLine(Run(-1));
    }
}
