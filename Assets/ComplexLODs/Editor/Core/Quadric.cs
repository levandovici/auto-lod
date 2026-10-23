using UnityEngine;

namespace ComplexLODs.Core
{
    /// <summary>
    /// Symmetric 4x4 quadric matrix used by the Garland-Heckbert error metric.
    /// Stored as 10 doubles. A quadric Q evaluated at homogeneous point
    /// v = (x, y, z, 1) gives the squared error cost of placing a vertex at v.
    /// </summary>
    internal struct Quadric
    {
        public double m00, m01, m02, m03;
        public double m11, m12, m13;
        public double m22, m23;
        public double m33;

        /// <summary>
        /// Builds the fundamental error quadric of the plane (a, b, c, d)
        /// where a*x + b*y + c*z + d = 0 and (a,b,c) is expected normalized.
        /// </summary>
        public static Quadric FromPlane(double a, double b, double c, double d, double weight)
        {
            Quadric q;
            q.m00 = weight * a * a;
            q.m01 = weight * a * b;
            q.m02 = weight * a * c;
            q.m03 = weight * a * d;
            q.m11 = weight * b * b;
            q.m12 = weight * b * c;
            q.m13 = weight * b * d;
            q.m22 = weight * c * c;
            q.m23 = weight * c * d;
            q.m33 = weight * d * d;
            return q;
        }

        public void Add(ref Quadric o)
        {
            m00 += o.m00; m01 += o.m01; m02 += o.m02; m03 += o.m03;
            m11 += o.m11; m12 += o.m12; m13 += o.m13;
            m22 += o.m22; m23 += o.m23;
            m33 += o.m33;
        }

        public void AddScaled(ref Quadric o, double s)
        {
            m00 += o.m00 * s; m01 += o.m01 * s; m02 += o.m02 * s; m03 += o.m03 * s;
            m11 += o.m11 * s; m12 += o.m12 * s; m13 += o.m13 * s;
            m22 += o.m22 * s; m23 += o.m23 * s;
            m33 += o.m33 * s;
        }

        public void Scale(double s)
        {
            m00 *= s; m01 *= s; m02 *= s; m03 *= s;
            m11 *= s; m12 *= s; m13 *= s;
            m22 *= s; m23 *= s;
            m33 *= s;
        }

        /// <summary>
        /// Probabilistic triangle quadric (Trettner &amp; Kobbelt 2020,
        /// "Fast and Robust QEF Minimization using Probabilistic Quadrics"),
        /// area-normalised and with feature-relative noise.
        ///
        /// The raw Lindstrom quadric weights a face by area^2, which makes fine
        /// detail (door frames, trims) numerically invisible next to big flat
        /// walls. We divide by 2|n| so the deterministic term becomes the
        /// standard area^1-weighted plane quadric (area * n-hat n-hat^T).
        ///
        /// Sigma is derived per triangle as relSigma * mean edge length, so
        /// regularisation is uniform across scales: tiny triangles keep near-
        /// classic behaviour, skinny/degenerate ones get properly conditioned.
        /// </summary>
        public static Quadric FromProbabilisticTriangle(Vector3 p, Vector3 q, Vector3 r, double relSigma)
        {
            double px = p.x, py = p.y, pz = p.z;
            double qx = q.x, qy = q.y, qz = q.z;
            double rx = r.x, ry = r.y, rz = r.z;

            // n = u x v = 2 * area * normal (also equals p x q + q x r + r x p)
            double ux = qx - px, uy = qy - py, uz = qz - pz;
            double vx = rx - px, vy = ry - py, vz = rz - pz;
            double nx = uy * vz - uz * vy;
            double ny = uz * vx - ux * vz;
            double nz = ux * vy - uy * vx;
            double nLen = System.Math.Sqrt(nx * nx + ny * ny + nz * nz);

            // D = |p q r| (scalar triple product) - needed for the linear term
            double d = px * (qy * rz - qz * ry) - py * (qx * rz - qz * rx) + pz * (qx * ry - qy * rx);

            // edges e0 = p-q, e1 = q-r, e2 = r-p
            double ex = px - qx, ey = py - qy, ez = pz - qz;
            double fx = qx - rx, fy = qy - ry, fz = qz - rz;
            double gx = rx - px, gy = ry - py, gz = rz - pz;

            double S = ex * ex + ey * ey + ez * ez
                     + fx * fx + fy * fy + fz * fz
                     + gx * gx + gy * gy + gz * gz;

            // sigma = relSigma * mean edge length
            double meanEdge = (System.Math.Sqrt(ex * ex + ey * ey + ez * ez)
                             + System.Math.Sqrt(fx * fx + fy * fy + fz * fz)
                             + System.Math.Sqrt(gx * gx + gy * gy + gz * gz)) / 3.0;
            double sigma2 = relSigma * relSigma * meanEdge * meanEdge;
            double s4 = sigma2 * sigma2;
            double reg = sigma2 * S + 6.0 * s4;

            // A = n n^T + sigma2 * sum_e (|e|^2 I - e e^T) + 6 sigma^4 I
            Quadric o;
            o.m00 = nx * nx + reg - sigma2 * (ex * ex + fx * fx + gx * gx);
            o.m11 = ny * ny + reg - sigma2 * (ey * ey + fy * fy + gy * gy);
            o.m22 = nz * nz + reg - sigma2 * (ez * ez + fz * fz + gz * gz);
            o.m01 = nx * ny - sigma2 * (ex * ey + fx * fy + gx * gy);
            o.m02 = nx * nz - sigma2 * (ex * ez + fx * fz + gx * gz);
            o.m12 = ny * nz - sigma2 * (ey * ez + fy * fz + gy * gz);

            // b = n*D + sigma2 * ( (q-p) x (p x q) + (r-q) x (q x r) + (p-r) x (r x p) )
            //     + 2 sigma^4 (p + q + r)
            double pqx = py * qz - pz * qy, pqy = pz * qx - px * qz, pqz = px * qy - py * qx;
            double qrx = qy * rz - qz * ry, qry = qz * rx - qx * rz, qrz = qx * ry - qy * rx;
            double rpx = ry * pz - rz * py, rpy = rz * px - rx * pz, rpz = rx * py - ry * px;

            double t1x = (qy - py) * pqz - (qz - pz) * pqy;
            double t1y = (qz - pz) * pqx - (qx - px) * pqz;
            double t1z = (qx - px) * pqy - (qy - py) * pqx;
            double t2x = (ry - qy) * qrz - (rz - qz) * qry;
            double t2y = (rz - qz) * qrx - (rx - qx) * qrz;
            double t2z = (rx - qx) * qry - (ry - qy) * qrx;
            double t3x = (py - ry) * rpz - (pz - rz) * rpy;
            double t3y = (pz - rz) * rpx - (px - rx) * rpz;
            double t3z = (px - rx) * rpy - (py - ry) * rpx;

            double bx = nx * d + sigma2 * (t1x + t2x + t3x) + 2.0 * s4 * (px + qx + rx);
            double by = ny * d + sigma2 * (t1y + t2y + t3y) + 2.0 * s4 * (py + qy + ry);
            double bz = nz * d + sigma2 * (t1z + t2z + t3z) + 2.0 * s4 * (pz + qz + rz);

            o.m03 = -bx;
            o.m13 = -by;
            o.m23 = -bz;
            o.m33 = d * d;

            // Area normalisation: divide by 2|n| (=4*area) so the nn^T term
            // becomes area * n-hat n-hat^T - classic area^1 weighting.
            double scale = nLen > 1e-12 ? 1.0 / (2.0 * nLen) : 0.0;
            o.Scale(scale);
            return o;
        }

        public double Eval(Vector3 v)
        {
            double x = v.x, y = v.y, z = v.z;
            return
                m00 * x * x + 2.0 * m01 * x * y + 2.0 * m02 * x * z + 2.0 * m03 * x +
                m11 * y * y + 2.0 * m12 * y * z + 2.0 * m13 * y +
                m22 * z * z + 2.0 * m23 * z +
                m33;
        }

        /// <summary>
        /// Solves for the optimal collapse position (minimum of the quadric).
        /// Returns false when the 3x3 system is singular.
        /// </summary>
        public bool SolveOptimal(out Vector3 result)
        {
            // Solve A * p = -b with
            // A = [m00 m01 m02; m01 m11 m12; m02 m12 m22], b = (m03, m13, m23)
            double a00 = m00, a01 = m01, a02 = m02;
            double a10 = m01, a11 = m11, a12 = m12;
            double a20 = m02, a21 = m12, a22 = m22;
            double bx = -m03, by = -m13, bz = -m23;

            double det =
                a00 * (a11 * a22 - a12 * a21) -
                a01 * (a10 * a22 - a12 * a20) +
                a02 * (a10 * a21 - a11 * a20);

            if (System.Math.Abs(det) < 1e-12)
            {
                result = Vector3.zero;
                return false;
            }

            double inv = 1.0 / det;
            double x = (bx * (a11 * a22 - a12 * a21) - a01 * (by * a22 - a12 * bz) + a02 * (by * a21 - a11 * bz)) * inv;
            double y = (a00 * (by * a22 - a12 * bz) - bx * (a10 * a22 - a12 * a20) + a02 * (a10 * bz - by * a20)) * inv;
            double z = (a00 * (a11 * bz - by * a21) - a01 * (a10 * bz - by * a20) + bx * (a10 * a21 - a11 * a20)) * inv;

            result = new Vector3((float)x, (float)y, (float)z);
            return true;
        }
    }
}
