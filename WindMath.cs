using System;

namespace MairaSimHub.WindPlugin
{
    // Math helpers for the MAIRA Typhoon Wind SimHub plugin.
    // Ported from MAIRA's MathZ class — only the portions needed here.
    //
    // NOTE: MathF is not available in .NET Framework 4.8.
    //       All float math is performed by casting System.Math results.
    internal static class WindMath
    {
        // -----------------------------------------------------------------------
        // InterpolateHermite
        //
        // Catmull-Rom / Hermite spline interpolation between v1 and v2.
        // v0 and v3 are the neighbouring control points used to compute tangents.
        // t is the interpolation parameter in [0, 1] where 0 = v1 and 1 = v2.
        //
        // Ported directly from MathZ.InterpolateHermite in the MAIRA source.
        // -----------------------------------------------------------------------
        public static float InterpolateHermite(float v0, float v1, float v2, float v3, float t)
        {
            var a = 2.0f * v1;
            var b = v2 - v0;
            var c = 2.0f * v0 - 5.0f * v1 + 4.0f * v2 - v3;
            var d = -v0 + 3.0f * v1 - 3.0f * v2 + v3;

            return 0.5f * (a + b * t + c * t * t + d * t * t * t);
        }
    }
}
