using System;
using System.Collections.Generic;
using System.Numerics;
using SoulsFormats;

namespace SekiroMapToolkit.Core
{
    /// <summary>
    /// 把一条动画在任意帧解成骨骼矩阵，并据此给网格做蒙皮。
    ///
    /// 关键点（踩过坑，记清楚）：
    /// 1. 动画是按「轨」存的，轨通过 TrackToBone 落到 hkx 骨骼上。
    /// 2. hkx 的 referencePose **全是单位阵**，它不带绑定姿势。
    ///    真正的绑定姿势在 FLVER 的 Nodes 里 —— 那 400 个节点才是模型的空间基准。
    /// 3. FLVER 顶点存的是「骨骼局部空间」的坐标，不是模型空间。
    ///    要先用绑定矩阵把它们抬到模型空间，再叠动画。
    /// 4. System.Numerics 是行向量约定（v' = v * M），矩阵相乘顺序别写反。
    ///
    /// 所以一个顶点最终的变换是：
    ///     v_model = v_local * Σ( w_i * ( bind_i * anim_i ) )
    /// 其中 bind_i = FLVER 节点 i 的全局绑定矩阵，
    ///     anim_i = 绑定姿势逆 × 当前姿势（把绑定位形掰到当前位形）。
    /// </summary>
    class AnimPose
    {
        public DecodedAnimation Anim;
        public DecodedSkeleton Skeleton;

        /// <summary>当前帧（0 起，可以是小数）。</summary>
        public float Frame;

        /// <summary>每根骨骼的当前世界矩阵（含绑定姿势基准，长度 = 骨骼数）。</summary>
        public Matrix4x4[] World = Array.Empty<Matrix4x4>();

        /// <summary>每根骨骼的世界位置（给骨骼线框用）。</summary>
        public Vector3[] WorldPos = Array.Empty<Vector3>();

        /// <summary>每根骨骼的「局部顶点 → 当前模型空间」总变换 = bind × delta。</summary>
        public Matrix4x4[] Skin = Array.Empty<Matrix4x4>();

        /// <summary>每根骨骼的绑定矩阵（FLVER 节点全局）。</summary>
        public Matrix4x4[] Bind = Array.Empty<Matrix4x4>();

        int _bindHits, _bindTotal;

        /// <summary>FLVER 里对上的骨骼数（诊断用）。</summary>
        public int BindHits => _bindHits;

        /// <summary>骨骼总数（诊断用）。</summary>
        public int BindTotal => _bindTotal;

        public float MaxFrame => Anim == null ? 0f : Math.Max(1f, Anim.FrameCount - 1);

        public float Duration => Anim == null ? 0f : Anim.Duration;

        /// <summary>装载动画与骨架。绑定姿势要在之后单独调 BuildBind。</summary>
        public void Build(DecodedAnimation anim, DecodedSkeleton skel)
        {
            Anim = anim;
            Skeleton = skel;
            Frame = 0f;
            int nb = skel?.BoneCount ?? 0;
            if (World.Length != nb)
            {
                World = new Matrix4x4[nb];
                WorldPos = new Vector3[nb];
                Skin = new Matrix4x4[nb];
                Bind = new Matrix4x4[nb];
                for (int i = 0; i < nb; i++) { Bind[i] = Matrix4x4.Identity; World[i] = Matrix4x4.Identity; Skin[i] = Matrix4x4.Identity; }
            }
            Evaluate();
        }

        /// <summary>
        /// 从 FLVER 的 Nodes 建绑定姿势。
        /// 名字对不上的骨骼退回单位阵（不影响整体，只是那根骨不参与变形）。
        /// </summary>
        public void BuildBind(FLVER2 flver)
        {
            if (Skeleton == null || flver?.Nodes == null) return;
            int nb = Skeleton.BoneCount;
            if (nb == 0) return;

            int nc = flver.Nodes.Count;
            var fg = new Matrix4x4[nc];
            for (int i = 0; i < nc; i++)
            {
                // 不要自己拼欧拉角 —— FLVER.Node.ComputeLocalTransform() 是官方实现，
                // 它内部处理了只狼的旋转顺序和坐标约定，照抄就行。
                var local = flver.Nodes[i].ComputeLocalTransform();
                int p = flver.Nodes[i].ParentIndex;
                fg[i] = (p >= 0 && p < nc && p != i) ? local * fg[p] : local;
            }

            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < nc; i++)
            {
                var nm = flver.Nodes[i].Name;
                if (!string.IsNullOrEmpty(nm) && !map.ContainsKey(nm)) map[nm] = i;
            }

            if (Bind.Length != nb)
            {
                Bind = new Matrix4x4[nb];
                Skin = new Matrix4x4[nb];
                for (int i = 0; i < nb; i++) { Bind[i] = Matrix4x4.Identity; Skin[i] = Matrix4x4.Identity; }
            }

            int hit = 0;
            for (int b = 0; b < nb; b++)
            {
                var nm = (Skeleton.BoneNames != null && b < Skeleton.BoneNames.Length)
                    ? Skeleton.BoneNames[b] : null;
                if (nm != null && map.TryGetValue(nm, out int fi)) { Bind[b] = fg[fi]; hit++; }
                else Bind[b] = Matrix4x4.Identity;
            }

            _bindHits = hit;
            _bindTotal = nb;
            Evaluate();
        }

        /// <summary>按当前 Frame 重算所有矩阵。</summary>
        public void Evaluate()
        {
            if (Skeleton == null) return;
            int nb = Skeleton.BoneCount;
            if (nb == 0) return;
            if (World.Length != nb) Build(Anim, Skeleton);

            // 1) 取这一帧每根骨骼的局部变换
            var locals = new DecodedTransform[nb];
            if (Anim != null && Anim.Frames != null && Anim.Frames.Count > 0 && Anim.TrackToBone != null)
                SampleFrame(Anim, Frame, locals);
            else
                for (int i = 0; i < nb; i++) locals[i] = IdentityT();

            // 2) 沿父链累乘出「姿势世界矩阵」（不含绑定基准）
            var poseWorld = new Matrix4x4[nb];
            var done = new bool[nb];
            for (int i = 0; i < nb; i++)
            {
                if (done[i]) continue;
                var chain = new List<int>();
                int cur = i, guard = 0;
                while (cur >= 0 && cur < nb && !done[cur] && guard++ < nb + 4)
                {
                    chain.Add(cur);
                    int p = cur < Skeleton.Parents.Length ? Skeleton.Parents[cur] : -1;
                    if (p == cur) break;
                    cur = p;
                }
                for (int k = chain.Count - 1; k >= 0; k--)
                {
                    int b = chain[k];
                    var local = LocalMatrix(locals[b]);
                    int p = b < Skeleton.Parents.Length ? Skeleton.Parents[b] : -1;
                    poseWorld[b] = (p >= 0 && p < nb && p != b && done[p]) ? local * poseWorld[p] : local;
                    done[b] = true;
                }
            }

            // 3) 合成：bind × (bind⁻¹ × pose) —— 让绑定位形落在模型空间，
            //    再叠上动画相对绑定姿势的变化量。
            for (int b = 0; b < nb; b++)
            {
                var bind = (Bind != null && b < Bind.Length) ? Bind[b] : Matrix4x4.Identity;

                if (Matrix4x4.Invert(bind, out var bindInv))
                    World[b] = bindInv * poseWorld[b];   // 相对绑定姿势的增量
                else
                    World[b] = poseWorld[b];

                // 顶点在骨骼局部空间 → 先增量 → 再抬回模型空间
                Skin[b] = World[b] * bind;
                WorldPos[b] = Skin[b].Translation;
            }
        }

        static DecodedTransform IdentityT() => new DecodedTransform
        {
            Translation = Vector3.Zero,
            Rotation = Quaternion.Identity,
            Scale = Vector3.One,
        };

        /// <summary>把帧 f 的轨道值采样进 locals。</summary>
        static void SampleFrame(DecodedAnimation anim, float f, DecodedTransform[] locals)
        {
            int nb = locals.Length;
            for (int i = 0; i < nb; i++) locals[i] = IdentityT();

            int fc = anim.Frames.Count;
            if (fc == 0) return;
            if (f < 0f) f = 0f;
            if (f > fc - 1) f = fc - 1;

            int i0 = (int)MathF.Floor(f);
            int i1 = i0 + 1;
            if (i1 >= fc) i1 = fc - 1;
            float t = f - i0;
            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;

            var f0 = anim.Frames[i0];
            var f1 = anim.Frames[i1];
            if (f0 == null) return;

            int tracks = anim.TrackToBone.Length;
            if (f1 == null) f1 = f0;
            tracks = Math.Min(tracks, Math.Min(f0.Length, f1.Length));

            for (int ti = 0; ti < tracks; ti++)
            {
                int bone = anim.TrackToBone[ti];
                if (bone < 0 || bone >= nb) continue;
                var a = f0[ti];
                var b = f1[ti];
                locals[bone] = new DecodedTransform
                {
                    Translation = Vector3.Lerp(a.Translation, b.Translation, t),
                    Rotation = Quaternion.Normalize(Quaternion.Slerp(a.Rotation, b.Rotation, t)),
                    Scale = Vector3.Lerp(a.Scale, b.Scale, t),
                };
            }
        }

        static Matrix4x4 LocalMatrix(DecodedTransform t)
        {
            var s = t.Scale;
            if (MathF.Abs(s.X) < 1e-7f) s.X = 1e-7f;
            if (MathF.Abs(s.Y) < 1e-7f) s.Y = 1e-7f;
            if (MathF.Abs(s.Z) < 1e-7f) s.Z = 1e-7f;

            return Matrix4x4.CreateScale(s)
                 * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(t.Rotation))
                 * Matrix4x4.CreateTranslation(t.Translation);
        }

        /// <summary>把一个网格按当前蒙皮矩阵变形，输出新的顶点数组（不改原始数据）。</summary>
        public Vector3[] SkinMesh(ChrMesh mesh, Vector3[] buffer)
        {
            int n = mesh.Positions.Count;
            if (buffer == null || buffer.Length != n) buffer = new Vector3[n];
            if (mesh.Weights == null || mesh.Weights.Count != n)
            {
                for (int i = 0; i < n; i++) buffer[i] = mesh.Positions[i];
                return buffer;
            }

            for (int i = 0; i < n; i++)
            {
                var p = mesh.Positions[i];
                var w = mesh.Weights[i];
                var acc = Vector3.Zero;
                float total = 0f;

                Accum(p, w.B0, w.W0, ref acc, ref total);
                Accum(p, w.B1, w.W1, ref acc, ref total);
                Accum(p, w.B2, w.W2, ref acc, ref total);
                Accum(p, w.B3, w.W3, ref acc, ref total);

                buffer[i] = total > 1e-5f ? acc / total : p;
            }
            return buffer;
        }

        void Accum(Vector3 p, int bone, float weight, ref Vector3 acc, ref float total)
        {
            if (weight <= 1e-6f) return;
            if (bone < 0 || bone >= Skin.Length) return;
            total += weight;
            acc += Vector3.Transform(p, Skin[bone]) * weight;
        }

        /// <summary>把 ChrMesh 转成渲染用的 RenderMesh（顶点已变形）。</summary>
        public RenderMesh ToRenderMesh(ChrMesh mesh, Vector3[] buffer, Vector3 color)
        {
            var pos = SkinMesh(mesh, buffer);
            return new RenderMesh
            {
                Name = mesh.Name,
                Positions = pos,
                Normals = mesh.Normals?.ToArray() ?? Array.Empty<Vector3>(),
                Uvs = mesh.Uvs?.ToArray() ?? Array.Empty<Vector2>(),
                Indices = mesh.Indices?.ToArray() ?? Array.Empty<int>(),
                Color = color,
            };
        }
    }
}
