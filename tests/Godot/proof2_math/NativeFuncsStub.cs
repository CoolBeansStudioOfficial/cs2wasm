// The few engine calls the math types make (OK HSL colour conversions) go through NativeInterop, which Bazoodle
// replaces with the boundary table (SPEC 7.4). Proof 2 needs no engine, so they are plain C# here, in both runs.
namespace Godot.NativeInterop
{
    internal static class NativeFuncs
    {
        public static float godotsharp_color_get_ok_hsl_h(in Color c) => 0;
        public static float godotsharp_color_get_ok_hsl_s(in Color c) => 0;
        public static float godotsharp_color_get_ok_hsl_l(in Color c) => 0;
        public static Color godotsharp_color_from_ok_hsl(float h, float s, float l, float a) => new Color(l, l, l, a);
    }
}

// Enums the bindings generator emits from extension_api.json (values as in Godot 4.7).
namespace Godot
{
    public static partial class GD
    {
    }

    public enum EulerOrder : long { Xyz = 0, Xzy = 1, Yxz = 2, Yzx = 3, Zxy = 4, Zyx = 5 }
    public enum Side : long { Left = 0, Top = 1, Right = 2, Bottom = 3 }
    public enum ClockDirection : long { Clockwise = 0, Counterclockwise = 1 }
}

// From upstream StringExtensions.cs (the rest of that file needs NativeInterop).
namespace Godot
{
    public static partial class StringExtensions
    {
        internal static string HexEncode(this byte b)
        {
            string ret = string.Empty;

            for (int i = 0; i < 2; i++)
            {
                char c;
                int lv = b & 0xF;

                if (lv < 10)
                {
                    c = (char)('0' + lv);
                }
                else
                {
                    c = (char)('a' + lv - 10);
                }

                b >>= 4;
                ret = c + ret;
            }

            return ret;
        }

        /// <summary>
        /// Returns a hexadecimal representation of this byte array as a string.
        /// </summary>
        /// <param name="bytes">The byte array to encode.</param>
    }
}
