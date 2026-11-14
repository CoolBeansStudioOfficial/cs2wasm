// ValueTuple: tuple types and literals compile to these structs.
#pragma warning disable CS0436
using System.Collections.Generic;

namespace System
{
    public struct ValueTuple
    {
        public override string ToString() => "()";
        public static ValueTuple<T1, T2> Create<T1, T2>(T1 item1, T2 item2) => new ValueTuple<T1, T2>(item1, item2);
        public static ValueTuple<T1, T2, T3> Create<T1, T2, T3>(T1 item1, T2 item2, T3 item3) => new ValueTuple<T1, T2, T3>(item1, item2, item3);
    }

    public struct ValueTuple<T1> : IEquatable<ValueTuple<T1>>, IComparable<ValueTuple<T1>>
    {
        public T1 Item1;
        public ValueTuple(T1 item1) { Item1 = item1; }
        public bool Equals(ValueTuple<T1> other) => EqualityComparer<T1>.Default.Equals(Item1, other.Item1);
        public override bool Equals(object obj) => obj is ValueTuple<T1> t && Equals(t);
        public override int GetHashCode() { int h = 0; h = h * 31 + (Item1 == null ? 0 : EqualityComparer<T1>.Default.GetHashCode(Item1)); return h; }
        public int CompareTo(ValueTuple<T1> other) { int c; c = Comparer<T1>.Default.Compare(Item1, other.Item1); if (c != 0) return c; return 0; }
        public override string ToString() => "(" + (Item1 == null ? "" : Item1.ToString()) + ")";
    }

    public struct ValueTuple<T1, T2> : IEquatable<ValueTuple<T1, T2>>, IComparable<ValueTuple<T1, T2>>
    {
        public T1 Item1;
        public T2 Item2;
        public ValueTuple(T1 item1, T2 item2) { Item1 = item1; Item2 = item2; }
        public bool Equals(ValueTuple<T1, T2> other) => EqualityComparer<T1>.Default.Equals(Item1, other.Item1) && EqualityComparer<T2>.Default.Equals(Item2, other.Item2);
        public override bool Equals(object obj) => obj is ValueTuple<T1, T2> t && Equals(t);
        public override int GetHashCode() { int h = 0; h = h * 31 + (Item1 == null ? 0 : EqualityComparer<T1>.Default.GetHashCode(Item1)); h = h * 31 + (Item2 == null ? 0 : EqualityComparer<T2>.Default.GetHashCode(Item2)); return h; }
        public int CompareTo(ValueTuple<T1, T2> other) { int c; c = Comparer<T1>.Default.Compare(Item1, other.Item1); if (c != 0) return c; c = Comparer<T2>.Default.Compare(Item2, other.Item2); if (c != 0) return c; return 0; }
        public override string ToString() => "(" + (Item1 == null ? "" : Item1.ToString()) + ", " + (Item2 == null ? "" : Item2.ToString()) + ")";
    }

    public struct ValueTuple<T1, T2, T3> : IEquatable<ValueTuple<T1, T2, T3>>, IComparable<ValueTuple<T1, T2, T3>>
    {
        public T1 Item1;
        public T2 Item2;
        public T3 Item3;
        public ValueTuple(T1 item1, T2 item2, T3 item3) { Item1 = item1; Item2 = item2; Item3 = item3; }
        public bool Equals(ValueTuple<T1, T2, T3> other) => EqualityComparer<T1>.Default.Equals(Item1, other.Item1) && EqualityComparer<T2>.Default.Equals(Item2, other.Item2) && EqualityComparer<T3>.Default.Equals(Item3, other.Item3);
        public override bool Equals(object obj) => obj is ValueTuple<T1, T2, T3> t && Equals(t);
        public override int GetHashCode() { int h = 0; h = h * 31 + (Item1 == null ? 0 : EqualityComparer<T1>.Default.GetHashCode(Item1)); h = h * 31 + (Item2 == null ? 0 : EqualityComparer<T2>.Default.GetHashCode(Item2)); h = h * 31 + (Item3 == null ? 0 : EqualityComparer<T3>.Default.GetHashCode(Item3)); return h; }
        public int CompareTo(ValueTuple<T1, T2, T3> other) { int c; c = Comparer<T1>.Default.Compare(Item1, other.Item1); if (c != 0) return c; c = Comparer<T2>.Default.Compare(Item2, other.Item2); if (c != 0) return c; c = Comparer<T3>.Default.Compare(Item3, other.Item3); if (c != 0) return c; return 0; }
        public override string ToString() => "(" + (Item1 == null ? "" : Item1.ToString()) + ", " + (Item2 == null ? "" : Item2.ToString()) + ", " + (Item3 == null ? "" : Item3.ToString()) + ")";
    }

    public struct ValueTuple<T1, T2, T3, T4> : IEquatable<ValueTuple<T1, T2, T3, T4>>, IComparable<ValueTuple<T1, T2, T3, T4>>
    {
        public T1 Item1;
        public T2 Item2;
        public T3 Item3;
        public T4 Item4;
        public ValueTuple(T1 item1, T2 item2, T3 item3, T4 item4) { Item1 = item1; Item2 = item2; Item3 = item3; Item4 = item4; }
        public bool Equals(ValueTuple<T1, T2, T3, T4> other) => EqualityComparer<T1>.Default.Equals(Item1, other.Item1) && EqualityComparer<T2>.Default.Equals(Item2, other.Item2) && EqualityComparer<T3>.Default.Equals(Item3, other.Item3) && EqualityComparer<T4>.Default.Equals(Item4, other.Item4);
        public override bool Equals(object obj) => obj is ValueTuple<T1, T2, T3, T4> t && Equals(t);
        public override int GetHashCode() { int h = 0; h = h * 31 + (Item1 == null ? 0 : EqualityComparer<T1>.Default.GetHashCode(Item1)); h = h * 31 + (Item2 == null ? 0 : EqualityComparer<T2>.Default.GetHashCode(Item2)); h = h * 31 + (Item3 == null ? 0 : EqualityComparer<T3>.Default.GetHashCode(Item3)); h = h * 31 + (Item4 == null ? 0 : EqualityComparer<T4>.Default.GetHashCode(Item4)); return h; }
        public int CompareTo(ValueTuple<T1, T2, T3, T4> other) { int c; c = Comparer<T1>.Default.Compare(Item1, other.Item1); if (c != 0) return c; c = Comparer<T2>.Default.Compare(Item2, other.Item2); if (c != 0) return c; c = Comparer<T3>.Default.Compare(Item3, other.Item3); if (c != 0) return c; c = Comparer<T4>.Default.Compare(Item4, other.Item4); if (c != 0) return c; return 0; }
        public override string ToString() => "(" + (Item1 == null ? "" : Item1.ToString()) + ", " + (Item2 == null ? "" : Item2.ToString()) + ", " + (Item3 == null ? "" : Item3.ToString()) + ", " + (Item4 == null ? "" : Item4.ToString()) + ")";
    }

    public struct ValueTuple<T1, T2, T3, T4, T5> : IEquatable<ValueTuple<T1, T2, T3, T4, T5>>, IComparable<ValueTuple<T1, T2, T3, T4, T5>>
    {
        public T1 Item1;
        public T2 Item2;
        public T3 Item3;
        public T4 Item4;
        public T5 Item5;
        public ValueTuple(T1 item1, T2 item2, T3 item3, T4 item4, T5 item5) { Item1 = item1; Item2 = item2; Item3 = item3; Item4 = item4; Item5 = item5; }
        public bool Equals(ValueTuple<T1, T2, T3, T4, T5> other) => EqualityComparer<T1>.Default.Equals(Item1, other.Item1) && EqualityComparer<T2>.Default.Equals(Item2, other.Item2) && EqualityComparer<T3>.Default.Equals(Item3, other.Item3) && EqualityComparer<T4>.Default.Equals(Item4, other.Item4) && EqualityComparer<T5>.Default.Equals(Item5, other.Item5);
        public override bool Equals(object obj) => obj is ValueTuple<T1, T2, T3, T4, T5> t && Equals(t);
        public override int GetHashCode() { int h = 0; h = h * 31 + (Item1 == null ? 0 : EqualityComparer<T1>.Default.GetHashCode(Item1)); h = h * 31 + (Item2 == null ? 0 : EqualityComparer<T2>.Default.GetHashCode(Item2)); h = h * 31 + (Item3 == null ? 0 : EqualityComparer<T3>.Default.GetHashCode(Item3)); h = h * 31 + (Item4 == null ? 0 : EqualityComparer<T4>.Default.GetHashCode(Item4)); h = h * 31 + (Item5 == null ? 0 : EqualityComparer<T5>.Default.GetHashCode(Item5)); return h; }
        public int CompareTo(ValueTuple<T1, T2, T3, T4, T5> other) { int c; c = Comparer<T1>.Default.Compare(Item1, other.Item1); if (c != 0) return c; c = Comparer<T2>.Default.Compare(Item2, other.Item2); if (c != 0) return c; c = Comparer<T3>.Default.Compare(Item3, other.Item3); if (c != 0) return c; c = Comparer<T4>.Default.Compare(Item4, other.Item4); if (c != 0) return c; c = Comparer<T5>.Default.Compare(Item5, other.Item5); if (c != 0) return c; return 0; }
        public override string ToString() => "(" + (Item1 == null ? "" : Item1.ToString()) + ", " + (Item2 == null ? "" : Item2.ToString()) + ", " + (Item3 == null ? "" : Item3.ToString()) + ", " + (Item4 == null ? "" : Item4.ToString()) + ", " + (Item5 == null ? "" : Item5.ToString()) + ")";
    }

    public struct ValueTuple<T1, T2, T3, T4, T5, T6> : IEquatable<ValueTuple<T1, T2, T3, T4, T5, T6>>, IComparable<ValueTuple<T1, T2, T3, T4, T5, T6>>
    {
        public T1 Item1;
        public T2 Item2;
        public T3 Item3;
        public T4 Item4;
        public T5 Item5;
        public T6 Item6;
        public ValueTuple(T1 item1, T2 item2, T3 item3, T4 item4, T5 item5, T6 item6) { Item1 = item1; Item2 = item2; Item3 = item3; Item4 = item4; Item5 = item5; Item6 = item6; }
        public bool Equals(ValueTuple<T1, T2, T3, T4, T5, T6> other) => EqualityComparer<T1>.Default.Equals(Item1, other.Item1) && EqualityComparer<T2>.Default.Equals(Item2, other.Item2) && EqualityComparer<T3>.Default.Equals(Item3, other.Item3) && EqualityComparer<T4>.Default.Equals(Item4, other.Item4) && EqualityComparer<T5>.Default.Equals(Item5, other.Item5) && EqualityComparer<T6>.Default.Equals(Item6, other.Item6);
        public override bool Equals(object obj) => obj is ValueTuple<T1, T2, T3, T4, T5, T6> t && Equals(t);
        public override int GetHashCode() { int h = 0; h = h * 31 + (Item1 == null ? 0 : EqualityComparer<T1>.Default.GetHashCode(Item1)); h = h * 31 + (Item2 == null ? 0 : EqualityComparer<T2>.Default.GetHashCode(Item2)); h = h * 31 + (Item3 == null ? 0 : EqualityComparer<T3>.Default.GetHashCode(Item3)); h = h * 31 + (Item4 == null ? 0 : EqualityComparer<T4>.Default.GetHashCode(Item4)); h = h * 31 + (Item5 == null ? 0 : EqualityComparer<T5>.Default.GetHashCode(Item5)); h = h * 31 + (Item6 == null ? 0 : EqualityComparer<T6>.Default.GetHashCode(Item6)); return h; }
        public int CompareTo(ValueTuple<T1, T2, T3, T4, T5, T6> other) { int c; c = Comparer<T1>.Default.Compare(Item1, other.Item1); if (c != 0) return c; c = Comparer<T2>.Default.Compare(Item2, other.Item2); if (c != 0) return c; c = Comparer<T3>.Default.Compare(Item3, other.Item3); if (c != 0) return c; c = Comparer<T4>.Default.Compare(Item4, other.Item4); if (c != 0) return c; c = Comparer<T5>.Default.Compare(Item5, other.Item5); if (c != 0) return c; c = Comparer<T6>.Default.Compare(Item6, other.Item6); if (c != 0) return c; return 0; }
        public override string ToString() => "(" + (Item1 == null ? "" : Item1.ToString()) + ", " + (Item2 == null ? "" : Item2.ToString()) + ", " + (Item3 == null ? "" : Item3.ToString()) + ", " + (Item4 == null ? "" : Item4.ToString()) + ", " + (Item5 == null ? "" : Item5.ToString()) + ", " + (Item6 == null ? "" : Item6.ToString()) + ")";
    }

    public struct ValueTuple<T1, T2, T3, T4, T5, T6, T7> : IEquatable<ValueTuple<T1, T2, T3, T4, T5, T6, T7>>, IComparable<ValueTuple<T1, T2, T3, T4, T5, T6, T7>>
    {
        public T1 Item1;
        public T2 Item2;
        public T3 Item3;
        public T4 Item4;
        public T5 Item5;
        public T6 Item6;
        public T7 Item7;
        public ValueTuple(T1 item1, T2 item2, T3 item3, T4 item4, T5 item5, T6 item6, T7 item7) { Item1 = item1; Item2 = item2; Item3 = item3; Item4 = item4; Item5 = item5; Item6 = item6; Item7 = item7; }
        public bool Equals(ValueTuple<T1, T2, T3, T4, T5, T6, T7> other) => EqualityComparer<T1>.Default.Equals(Item1, other.Item1) && EqualityComparer<T2>.Default.Equals(Item2, other.Item2) && EqualityComparer<T3>.Default.Equals(Item3, other.Item3) && EqualityComparer<T4>.Default.Equals(Item4, other.Item4) && EqualityComparer<T5>.Default.Equals(Item5, other.Item5) && EqualityComparer<T6>.Default.Equals(Item6, other.Item6) && EqualityComparer<T7>.Default.Equals(Item7, other.Item7);
        public override bool Equals(object obj) => obj is ValueTuple<T1, T2, T3, T4, T5, T6, T7> t && Equals(t);
        public override int GetHashCode() { int h = 0; h = h * 31 + (Item1 == null ? 0 : EqualityComparer<T1>.Default.GetHashCode(Item1)); h = h * 31 + (Item2 == null ? 0 : EqualityComparer<T2>.Default.GetHashCode(Item2)); h = h * 31 + (Item3 == null ? 0 : EqualityComparer<T3>.Default.GetHashCode(Item3)); h = h * 31 + (Item4 == null ? 0 : EqualityComparer<T4>.Default.GetHashCode(Item4)); h = h * 31 + (Item5 == null ? 0 : EqualityComparer<T5>.Default.GetHashCode(Item5)); h = h * 31 + (Item6 == null ? 0 : EqualityComparer<T6>.Default.GetHashCode(Item6)); h = h * 31 + (Item7 == null ? 0 : EqualityComparer<T7>.Default.GetHashCode(Item7)); return h; }
        public int CompareTo(ValueTuple<T1, T2, T3, T4, T5, T6, T7> other) { int c; c = Comparer<T1>.Default.Compare(Item1, other.Item1); if (c != 0) return c; c = Comparer<T2>.Default.Compare(Item2, other.Item2); if (c != 0) return c; c = Comparer<T3>.Default.Compare(Item3, other.Item3); if (c != 0) return c; c = Comparer<T4>.Default.Compare(Item4, other.Item4); if (c != 0) return c; c = Comparer<T5>.Default.Compare(Item5, other.Item5); if (c != 0) return c; c = Comparer<T6>.Default.Compare(Item6, other.Item6); if (c != 0) return c; c = Comparer<T7>.Default.Compare(Item7, other.Item7); if (c != 0) return c; return 0; }
        public override string ToString() => "(" + (Item1 == null ? "" : Item1.ToString()) + ", " + (Item2 == null ? "" : Item2.ToString()) + ", " + (Item3 == null ? "" : Item3.ToString()) + ", " + (Item4 == null ? "" : Item4.ToString()) + ", " + (Item5 == null ? "" : Item5.ToString()) + ", " + (Item6 == null ? "" : Item6.ToString()) + ", " + (Item7 == null ? "" : Item7.ToString()) + ")";
    }

}