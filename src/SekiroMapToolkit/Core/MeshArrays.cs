using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace SekiroMapToolkit.Core
{
    /// <summary>
    /// ChrMesh 的法线 / UV / 索引在整段动画里都是常量，
    /// 缓存成数组供渲染器每帧直接引用，避免反复复制。
    /// </summary>
    internal static class MeshArrays
    {
        static readonly ConditionalWeakTable<ChrMesh, Vector3[]> NrmCache = new();
        static readonly ConditionalWeakTable<ChrMesh, Vector2[]> UvCache = new();
        static readonly ConditionalWeakTable<ChrMesh, int[]> IdxCache = new();

        public static Vector3[] Normals(ChrMesh m)
        {
            if (m.Normals == null) return Array.Empty<Vector3>();
            if (NrmCache.TryGetValue(m, out var a)) return a;
            a = m.Normals.ToArray();
            NrmCache.Add(m, a);
            return a;
        }

        public static Vector2[] Uvs(ChrMesh m)
        {
            if (m.Uvs == null) return Array.Empty<Vector2>();
            if (UvCache.TryGetValue(m, out var a)) return a;
            a = m.Uvs.ToArray();
            UvCache.Add(m, a);
            return a;
        }

        public static int[] Indices(ChrMesh m)
        {
            if (m.Indices == null) return Array.Empty<int>();
            if (IdxCache.TryGetValue(m, out var a)) return a;
            a = m.Indices.ToArray();
            IdxCache.Add(m, a);
            return a;
        }

        public static void Clear()
        {
            // ConditionalWeakTable 没有 Clear，只能靠 GC。
        }
    }
}
