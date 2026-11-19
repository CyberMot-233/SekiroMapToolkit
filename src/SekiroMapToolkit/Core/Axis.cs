using System;
using Vector3 = System.Numerics.Vector3;

namespace SekiroMapToolkit.Core
{
    // Sekiro/MSB space is Y-up right handed; Blender is Z-up right handed.
    // C = rotate +90 deg about X :  (x,y,z) -> (x,-z,y)
    static class Axis
    {
        public static Vector3 ToBlender(Vector3 v) => new Vector3(v.X, -v.Z, v.Y);
        public static Vector3 ToBlenderNormal(Vector3 n) => new Vector3(n.X, -n.Z, n.Y);

        // 3x3 in Blender space for the MSB part rotation (degrees, Unity Euler) * scale
        public static double[,] PartMatrix(Vector3 rotDeg, Vector3 scale)
        {
            double rx = rotDeg.X * Math.PI / 180.0;
            double ry = rotDeg.Y * Math.PI / 180.0;
            double rz = rotDeg.Z * Math.PI / 180.0;
            var RxM = RotX(rx); var RyM = RotY(ry); var RzM = RotZ(rz);
            // Unity Quaternion.Euler(x,y,z) => R = Ry * Rx * Rz
            var Rd = Mul(RyM, Mul(RxM, RzM));
            var C = new double[,] { { 1, 0, 0 }, { 0, 0, -1 }, { 0, 1, 0 } };
            var Ct = Transpose(C);
            var M = Mul(C, Mul(Rd, Ct));
            // apply scale (Blender axes: x<-sx, y<-sz, z<-sy)
            var s = new double[,] { { scale.X, 0, 0 }, { 0, scale.Z, 0 }, { 0, 0, scale.Y } };
            return Mul(M, s);
        }

        public static System.Numerics.Quaternion QuaternionFrom(double[,] m)
        {
            double m00 = m[0, 0], m01 = m[0, 1], m02 = m[0, 2];
            double m10 = m[1, 0], m11 = m[1, 1], m12 = m[1, 2];
            double m20 = m[2, 0], m21 = m[2, 1], m22 = m[2, 2];
            // remove scale
            double sx = Math.Sqrt(m00 * m00 + m10 * m10 + m20 * m20);
            double sy = Math.Sqrt(m01 * m01 + m11 * m11 + m21 * m21);
            double sz = Math.Sqrt(m02 * m02 + m12 * m12 + m22 * m22);
            if (sx > 1e-9) { m00 /= sx; m10 /= sx; m20 /= sx; }
            if (sy > 1e-9) { m01 /= sy; m11 /= sy; m21 /= sy; }
            if (sz > 1e-9) { m02 /= sz; m12 /= sz; m22 /= sz; }

            double tr = m00 + m11 + m22;
            double w, x, y, z;
            if (tr > 0)
            {
                double s2 = Math.Sqrt(tr + 1.0) * 2;
                w = 0.25 * s2; x = (m21 - m12) / s2; y = (m02 - m20) / s2; z = (m10 - m01) / s2;
            }
            else if (m00 > m11 && m00 > m22)
            {
                double s2 = Math.Sqrt(1.0 + m00 - m11 - m22) * 2;
                w = (m21 - m12) / s2; x = 0.25 * s2; y = (m01 + m10) / s2; z = (m02 + m20) / s2;
            }
            else if (m11 > m22)
            {
                double s2 = Math.Sqrt(1.0 + m11 - m00 - m22) * 2;
                w = (m02 - m20) / s2; x = (m01 + m10) / s2; y = 0.25 * s2; z = (m12 + m21) / s2;
            }
            else
            {
                double s2 = Math.Sqrt(1.0 + m22 - m00 - m11) * 2;
                w = (m10 - m01) / s2; x = (m02 + m20) / s2; y = (m12 + m21) / s2; z = 0.25 * s2;
            }
            var q = new System.Numerics.Quaternion((float)x, (float)y, (float)z, (float)w);
            return System.Numerics.Quaternion.Normalize(q);
        }

        public static System.Numerics.Quaternion QuaternionFromEuler(Vector3 rotDeg)
            => QuaternionFrom(PartMatrix(rotDeg, Vector3.One));

        static double[,] RotX(double a) { double c = Math.Cos(a), s = Math.Sin(a); return new double[,] { { 1, 0, 0 }, { 0, c, -s }, { 0, s, c } }; }
        static double[,] RotY(double a) { double c = Math.Cos(a), s = Math.Sin(a); return new double[,] { { c, 0, s }, { 0, 1, 0 }, { -s, 0, c } }; }
        static double[,] RotZ(double a) { double c = Math.Cos(a), s = Math.Sin(a); return new double[,] { { c, -s, 0 }, { s, c, 0 }, { 0, 0, 1 } }; }

        static double[,] Mul(double[,] a, double[,] b)
        {
            var r = new double[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    double s = 0;
                    for (int k = 0; k < 3; k++) s += a[i, k] * b[k, j];
                    r[i, j] = s;
                }
            return r;
        }

        static double[,] Transpose(double[,] a) => new double[,]
        {
            { a[0, 0], a[1, 0], a[2, 0] },
            { a[0, 1], a[1, 1], a[2, 1] },
            { a[0, 2], a[1, 2], a[2, 2] }
        };
    }
}