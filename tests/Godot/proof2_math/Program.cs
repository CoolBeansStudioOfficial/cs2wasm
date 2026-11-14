// Proof 2 (SPEC section 10): upstream GodotSharp math types, unmodified, with a test program; output equals .NET.
using System;
using System.Runtime.InteropServices;
using Godot;

static class Program
{
    static string F(real_t v) => v.ToString("0.0000");

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var a = new Vector2(3, 4);
        var b = new Vector2(-1, 2.5f);
        Console.WriteLine(a + " " + b + " " + (a + b) + " " + (a * 2) + " " + (a - b) / 2);
        Console.WriteLine(F(a.Length()) + " " + a.Normalized() + " " + F(a.Dot(b)) + " " + F(a.Cross(b)) + " " + F(a.AngleTo(b)));
        Console.WriteLine(a.Lerp(b, 0.25f) + " " + a.Rotated(Mathf.Pi / 3) + " " + a.Clamp(Vector2.Zero, Vector2.One) + " " + a.Snapped(new Vector2(0.7f, 0.7f)));
        Console.WriteLine(a.MoveToward(b, 1.5f) + " " + a.Reflect(Vector2.Up) + " " + a.Bounce(Vector2.Right) + " " + a.Slide(Vector2.Down));
        Console.WriteLine((a == new Vector2(3, 4)) + " " + a.IsEqualApprox(new Vector2(3.000001f, 4)) + " " + a.Aspect() + " " + a.Abs().Floor());
        var v3 = new Vector3(1, 2, 3);
        var w3 = new Vector3(-2, 0.5f, 4);
        Console.WriteLine(v3.Cross(w3) + " " + F(v3.Dot(w3)) + " " + v3.Normalized() + " " + v3.Lerp(w3, 0.5f) + " " + v3.Slerp(w3, 0.3f));
        Console.WriteLine(v3.Rotated(Vector3.Up, 0.7f) + " " + v3.Project(w3) + " " + F(v3.DistanceTo(w3)) + " " + v3.MaxAxisIndex() + " " + v3.Sign());
        var basis = new Basis(Vector3.Up, Mathf.DegToRad(30));
        Console.WriteLine(basis);
        Console.WriteLine(basis * v3 + " " + basis.Inverse() * (basis * v3) + " " + F(basis.Determinant()));
        var q = new Quaternion(Vector3.Right, 0.4f);
        Console.WriteLine(q + " " + q.Normalized() + " " + (q * new Quaternion(Vector3.Up, 1.1f)) + " " + q.GetEuler());
        Console.WriteLine(q.Slerp(Quaternion.Identity, 0.25f) + " " + new Basis(q).GetRotationQuaternion());
        var t = new Transform3D(basis, new Vector3(5, 0, -2));
        Console.WriteLine(t);
        Console.WriteLine(t * v3 + " " + t.AffineInverse() * (t * v3) + " " + t.Translated(Vector3.One).Origin + " " + t.Rotated(Vector3.Right, 0.3f));
        Console.WriteLine(t.LookingAt(new Vector3(0, 0, -10), Vector3.Up).Basis.Z);
        var t2 = new Transform2D(0.5f, new Vector2(10, 20));
        Console.WriteLine(t2 + " " + t2 * a + " " + t2.AffineInverse() * (t2 * a) + " " + F(t2.Rotation));
        var c = new Color(0.2f, 0.4f, 0.6f, 0.8f);
        Console.WriteLine(c + " " + c.ToHtml() + " " + c.Lerp(Colors.Red, 0.5f) + " " + c.Inverted() + " " + c.Lightened(0.2f));
        Console.WriteLine(Color.FromHtml("#ff8000") + " " + Color.FromHsv(0.5f, 0.5f, 0.5f) + " " + F(c.H) + " " + F(c.S) + " " + F(c.V) + " " + c.ToRgba32());
        Console.WriteLine(Colors.CornflowerBlue + " " + c.Blend(new Color(1, 1, 1, 0.5f)));
        var box = new Aabb(new Vector3(0, 0, 0), new Vector3(2, 2, 2));
        Console.WriteLine(box.Intersects(new Aabb(new Vector3(1, 1, 1), Vector3.One)) + " " + box.Expand(new Vector3(5, -1, 0)) + " " + box.GetCenter() + " " + box.HasPoint(Vector3.One));
        var r = new Rect2(0, 0, 10, 5);
        Console.WriteLine(r.Intersection(new Rect2(5, 2, 10, 10)) + " " + r.Merge(new Rect2(-3, -3, 1, 1)) + " " + r.HasPoint(new Vector2(3, 3)) + " " + r.Grow(1));
        var plane = new Plane(Vector3.Up, 2);
        Console.WriteLine(plane + " " + F(plane.DistanceTo(v3)) + " " + plane.Project(v3) + " " + plane.IsPointOver(v3));
        var proj = Projection.CreatePerspective(70, 16f / 9, 0.1f, 100, false);
        Console.WriteLine(proj.X + " " + proj.Y + " " + proj.Z + " " + proj.W);
        Console.WriteLine(proj.Inverse().X + " " + F(proj.Determinant()));
        Console.WriteLine(Mathf.Wrap(7.5f, 0, 2) + " " + Mathf.PingPong(5.5f, 2) + " " + Mathf.SmoothStep(0, 1, 0.3f) + " " + Mathf.LerpAngle(0.1f, 6.0f, 0.5f) + " " + Mathf.Snapped(17.3f, 5) + " " + Mathf.PosMod(-7, 3));
        Console.WriteLine(new Vector2I(7, -3) / 2 + " " + new Vector3I(1, 2, 3) * new Vector3I(2, 2, 2) + " " + new Vector4(1, 2, 3, 4).Normalized() + " " + new Rect2I(1, 1, 4, 4).Area);
        Console.WriteLine(a.GetHashCode() == new Vector2(3, 4).GetHashCode());
    }
}
