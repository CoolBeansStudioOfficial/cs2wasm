// A span or a `ref` keeps the object it points into, as under .NET: over a temporary (`Make().AsSpan()`), over an
// array of a field, an element, a static or a property that is replaced while the span is in use, over a local
// that is assigned again or whose block has ended, returned from a function over its own local or temporary, by
// `ref`, in a ref struct, through `out`, a generic method, a delegate. Each case allocates and frees in between
// (Churn), so memory let go too early is used again: until 2026-11-23, 34 of these 38 read freed memory.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Node { public Node? Next; public int Id; public Node(int id) { Id = id; } }
sealed class Holder { public Node[] Arr = Program.Make(3); public string Text = "text" + Program.Make(1).Length; public Node[][] Rows = { Program.Make(2), Program.Make(2) }; public Node[] Prop => Arr; public Node[] Fresh => Program.Make(3); }
struct SHolder { public Node[] Arr; }

ref struct Wrap { public Span<Node> S; public ReadOnlySpan<Node> T; public Wrap(Node[] a) { S = a; T = a.AsSpan(1); } public Wrap(Node[] a, Node[] b) { S = a; T = b; } }
delegate Span<Node> Maker();
static class Program
{
    static Span<Node> Own() { var a = Make(3); return a; }
    static Span<Node> Own2() { var a = Make(3); var s = a.AsSpan(); return s.Slice(1); }
    static ReadOnlySpan<Node> Tmp() => Make(3);
    static ref Node R(Holder h) => ref h.Arr[1];
    static ref Node E() => ref Make(3)[0];
    static Wrap Two() { var a = Make(2); return new Wrap(a, Make(4)); }
    static void Get(out Span<Node> s) { s = Make(3); }
    static Span<T> First<T>(T[] a) => a.AsSpan(0, 1);
    static Span<Node> Items(Holder h) => h.Arr;
    [MethodImpl(MethodImplOptions.NoInlining)] public static Node[] Make(int n) { var a = new Node[n]; for (int i = 0; i < n; i++) a[i] = new Node(i + 1); return a; }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Churn() { for (int i = 0; i < 50; i++) { var t = new Node[3]; t[0] = new Node(1000 + i); var t2 = new Node[2]; var t4 = new Node[4]; var s = new string('x', 4 + i % 5); } }
    static Holder s_h = new Holder();
    static Node[] s_arr = Make(3);
    [MethodImpl(MethodImplOptions.NoInlining)] static void Replace(Holder h) { h.Arr = Make(3); h.Text = "other" + h.Arr.Length; h.Rows[0] = Make(2); s_arr = Make(3); }
    [MethodImpl(MethodImplOptions.NoInlining)] static int Sum(ReadOnlySpan<Node> s) { int t = 0; foreach (var n in s) t += n.Id; return t; }

    static void Say(string name, object? value) => Console.WriteLine(name + ": " + value);

    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_a_temp()
    {
        var h = new Holder();
        var sp = Make(3).AsSpan(); Churn(); Say("a_temp", Sum(sp));
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_b_foreach_temp()
    {
        var h = new Holder();
        int sum = 0; foreach (var n in Make(3).AsSpan()) { Churn(); sum += n.Id; } Say("b_foreach_temp", sum);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_c_implicit()
    {
        var h = new Holder();
        ReadOnlySpan<Node> ro = Make(2); Churn(); Say("c_implicit", Sum(ro));
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_d_ctor()
    {
        var h = new Holder();
        var sp = new Span<Node>(Make(4)); Churn(); Say("d_ctor", Sum(sp));
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_e_local_reassigned()
    {
        var h = new Holder();
        var a = Make(3); var sp = a.AsSpan(); a = Make(3); Churn(); Say("e_local_reassigned", Sum(sp));
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_f_local_inner_scope()
    {
        var h = new Holder();
        Span<Node> sp; { var a = Make(3); sp = a; } Churn(); Say("f_local_inner_scope", Sum(sp));
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_g_field_replaced()
    {
        var h = new Holder();
        var sp = h.Arr.AsSpan(); Replace(h); Churn(); Say("g_field_replaced", Sum(sp));
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_h_field_implicit()
    {
        var h = new Holder();
        ReadOnlySpan<Node> sp = h.Arr; Replace(h); Churn(); Say("h_field_implicit", Sum(sp));
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_i_string_field()
    {
        var h = new Holder();
        var sp = h.Text.AsSpan(); Replace(h); Churn(); Say("i_string_field", sp.ToString());
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_j_element()
    {
        var h = new Holder();
        var sp = h.Rows[0].AsSpan(); Replace(h); Churn(); Say("j_element", Sum(sp));
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_k_static()
    {
        var h = new Holder();
        var sp = s_arr.AsSpan(); Replace(h); Churn(); Say("k_static", Sum(sp));
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_l_property()
    {
        var h = new Holder();
        var sp = h.Prop.AsSpan(); Replace(h); Churn(); Say("l_property", Sum(sp));
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_m_fresh_property()
    {
        var h = new Holder();
        var sp = h.Fresh.AsSpan(1); Churn(); Say("m_fresh_property", Sum(sp));
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_n_string_temp()
    {
        var h = new Holder();
        var sp = ("ab" + h.Arr.Length).AsSpan(); Churn(); Say("n_string_temp", sp.ToString());
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_o_ref_local()
    {
        var h = new Holder();
        ref Node r = ref h.Arr[1]; Replace(h); Churn(); Say("o_ref_local", r.Id);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_p_ref_temp()
    {
        var h = new Holder();
        ref Node r = ref Make(3)[1]; Churn(); Say("p_ref_temp", r.Id);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_q_memory_span()
    {
        var h = new Holder();
        var sp = Make(5).AsMemory().Span; Churn(); Say("q_memory_span", Sum(sp));
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_r_slice_of_temp()
    {
        var h = new Holder();
        var sp = Make(5)[1..3].AsSpan(); Churn(); Say("r_slice_of_temp", Sum(sp));
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_s_struct_field()
    {
        var h = new Holder();
        var sh = new SHolder { Arr = Make(3) }; var sp = sh.Arr.AsSpan(); sh.Arr = Make(3); Churn(); Say("s_struct_field", Sum(sp));
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_t_foreach_ref()
    {
        var h = new Holder();
        int sum = 0; foreach (ref var n in Make(3).AsSpan()) { Churn(); sum += n.Id; } Say("t_foreach_ref", sum);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_u_arg_temp()
    {
        var h = new Holder();
        Say("u_arg_temp", Sum(Make(3)));
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_v_param()
    {
        var h = new Holder();
        Say("v_param", Local(h)); static int Local(Holder x) { var sp = x.Arr.AsSpan(); x.Arr = Make(1); Churn(); return Sum(sp); }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_w_list_toarray()
    {
        var h = new Holder();
        var l = new List<Node>(Make(3)); var sp = l.ToArray().AsSpan(); Churn(); Say("w_list_toarray", Sum(sp));
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_ra_own()
    {
        var h = new Holder();
        var sp = Own(); Churn(); Say("ra_own", Sum(sp));
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_rb_own2()
    {
        var h = new Holder();
        var sp = Own2(); Churn(); Say("rb_own2", Sum(sp));
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_rc_tmp()
    {
        var h = new Holder();
        var sp = Tmp(); Churn(); Say("rc_tmp", Sum(sp));
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_rd_ref()
    {
        var h = new Holder();
        ref Node r = ref R(h); Replace(h); Churn(); Say("rd_ref", r.Id);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_re_ref_temp()
    {
        var h = new Holder();
        ref Node r = ref E(); Churn(); Say("re_ref_temp", r.Id);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_rf_two()
    {
        var h = new Holder();
        var w = Two(); Churn(); Say("rf_two", Sum(w.S) + " " + Sum(w.T));
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_rg_out()
    {
        var h = new Holder();
        Get(out var sp); Churn(); Say("rg_out", Sum(sp));
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_rh_generic()
    {
        var h = new Holder();
        var sp = First(Make(3)); Churn(); Say("rh_generic", Sum(sp));
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_ri_items()
    {
        var h = new Holder();
        var sp = Items(h); Replace(h); Churn(); Say("ri_items", Sum(sp));
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_rj_lambda()
    {
        var h = new Holder();
        Maker d = () => Make(3); var sp = d(); Churn(); Say("rj_lambda", Sum(sp));
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_rk_wrap()
    {
        var h = new Holder();
        var w = new Wrap(h.Arr); Replace(h); Churn(); Say("rk_wrap", Sum(w.S) + " " + Sum(w.T));
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_rl_chain()
    {
        var h = new Holder();
        Say("rl_chain", Sum(Own().Slice(1)) + Sum(Tmp()) + Own2()[0].Id);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_rm_loop()
    {
        var h = new Holder();
        int t = 0; for (int i = 0; i < 20; i++) { var sp = Own(); Churn(); t += Sum(sp) + Sum(Items(h)); Replace(h); } Say("rm_loop", t);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_rn_passed()
    {
        var h = new Holder();
        Say("rn_passed", Deep(Own(), 3)); static int Deep(Span<Node> s, int n) { Churn(); return n == 0 ? Sum(s) : Deep(s.Slice(0, s.Length), n - 1); }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Case_x_utf8()
    {
        var h = new Holder();
        var sp = System.Text.Encoding.UTF8.GetBytes("hello" + h.Arr.Length).AsSpan(); Churn(); Say("x_utf8", sp.Length + " " + sp[0]);
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        for (int round = 0; round < 2; round++)
        {
            Case_a_temp();
            Case_b_foreach_temp();
            Case_c_implicit();
            Case_d_ctor();
            Case_e_local_reassigned();
            Case_f_local_inner_scope();
            Case_g_field_replaced();
            Case_h_field_implicit();
            Case_i_string_field();
            Case_j_element();
            Case_k_static();
            Case_l_property();
            Case_m_fresh_property();
            Case_n_string_temp();
            Case_o_ref_local();
            Case_p_ref_temp();
            Case_q_memory_span();
            Case_r_slice_of_temp();
            Case_s_struct_field();
            Case_t_foreach_ref();
            Case_u_arg_temp();
            Case_v_param();
            Case_w_list_toarray();
            Case_ra_own();
            Case_rb_own2();
            Case_rc_tmp();
            Case_rd_ref();
            Case_re_ref_temp();
            Case_rf_two();
            Case_rg_out();
            Case_rh_generic();
            Case_ri_items();
            Case_rj_lambda();
            Case_rk_wrap();
            Case_rl_chain();
            Case_rm_loop();
            Case_rn_passed();
            Case_x_utf8();
        }
    }
}
