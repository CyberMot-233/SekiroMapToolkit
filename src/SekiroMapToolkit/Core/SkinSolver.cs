using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using SoulsFormats;

namespace SekiroMapToolkit.Core
{
    /// <summary>
    /// 正确的只狼蒙皮求解器（算法照抄 DSAnimStudio，不自己推导）。
    ///
    /// ============================================================
    /// 出处（GitHub Meowmaritus/DSAnimStudio，master 分支，源码已逐行核对）
    ///   DSAnimStudioNETCore/NewAnimSkeleton_FLVER.cs :136  CopyBoneToShaderMatrices(int i)
    ///   DSAnimStudioNETCore/NewBone.cs               :207  NewBone(FLVER.Node, List&lt;FLVER.Node&gt;)
    ///   DSAnimStudioNETCore/NewAnimSkeleton.cs       :168  CalculateFKFromLocalTransforms()
    ///   DSAnimStudioNETCore/NewAnimSkeleton.cs       :96   MapToOtherSkeleton()
    ///   DSAnimStudioNETCore/NewAnimSkeleton.cs       :551  InitWalkTree()
    ///   DSASContentSource/Shaders/FlverShader.fx     :633  SkinVert()
    /// ============================================================
    ///
    /// 一句话（NewAnimSkeleton_FLVER.cs:139 + FlverShader.fx:641-645）：
    ///
    ///     skinMatrix[n] = inverse( ReferenceFKMatrix[n] ) * FKMatrix[n]
    ///     v_out         = Σ w_i * ( v_in * skinMatrix[boneIdx_i] ) / Σ w_i
    ///
    /// 其中 n 是 **FLVER 节点索引**，不是 hkx 骨骼索引 —— 这是本工程之前散架的直接原因。
    ///
    /// 为什么是节点索引：DSAnimStudio 的 FLVER 骨架是拿 **全部 FLVER.Nodes** 建的
    /// （NewAnimSkeleton_FLVER.cs:85 `LoadFLVERSkeleton(Model, List<FLVER.Node> flverBones)`，
    /// 循环 `for (i < flverBones.Count)`，一个节点一根骨），而顶点里的 BoneIndices
    /// 就是这个列表的下标（FlverSubmeshRenderer.cs:436，本 FLVER 版本 0x2001A > 0x2000D，
    /// 走的是"直接用 vert.BoneIndices"分支，不经过 mesh.BoneIndices 重映射）。
    /// 只狼 c1010 的顶点索引实测最大 109，且 100% 落在 flags 含 Bone 的节点上，完全不越界，
    /// 正是节点索引。hkx 只有 127 根骨，且顺序跟节点顺序**不同**
    /// （hkx bone[2]=L_Foot_Target1 → FLVER node 359，而 node 2=Pelvis），
    /// 拿 hkx 索引直接喂给顶点就是全错位 —— 这就是"模型散成碎片"的根因。
    ///
    /// 坐标系：XNA/MonoGame 的 Matrix 是行向量约定 v' = v * M，和 System.Numerics 一致
    /// （DSAnimStudio 的 ToXna() 只是逐元素拷贝、不转置，见 XnaExtensions.cs:356），
    /// 所以乘法顺序可以逐字照抄，不做任何转置。
    ///
    /// 注意：本工程 ChrMesh.Positions 已被 Axis.ToBlender 转成 Blender 的 Z-up
    /// （(x,y,z) → (x,-z,y)），而所有矩阵都在 FLVER 的 Y-up 模型空间。
    /// Skin() 内部先转回 FLVER 空间蒙皮，再转回 Blender 空间输出，
    /// 保证返回值与输入的 ChrMesh.Positions 处于同一套坐标。
    /// </summary>
    class SkinSolver
    {
        const float Eps = 1e-8f;

        // ---------------------------------------------------------------- 数据

        readonly FLVER2 _flver;
        readonly DecodedSkeleton _skel;

        /// <summary>FLVER 节点数（本角色 400）—— 蒙皮矩阵的长度。</summary>
        public int NodeCount => _flver?.Nodes?.Count ?? 0;

        /// <summary>hkx 骨骼数（本角色 127）。</summary>
        public int BoneCount => _skel?.BoneCount ?? 0;

        /// <summary>FLVER 节点索引 → hkx 骨骼索引；-1 = 这个节点没有同名骨骼。</summary>
        readonly int[] _nodeToBone;

        /// <summary>hkx 骨骼索引 → FLVER 节点索引；-1 = 没对上。</summary>
        readonly int[] _boneToNode;

        /// <summary>FLVER 每个节点的局部绑定矩阵（scale*rotX*rotZ*rotY*trans）。</summary>
        readonly Matrix4x4[] _nodeLocal;

        /// <summary>FLVER 每个节点的全局绑定矩阵（沿 ParentIndex 父链累乘）。</summary>
        readonly Matrix4x4[] _nodeGlobal;

        /// <summary>
        /// 绑定姿势 ReferenceFKMatrix，按 **FLVER 节点索引**。长度 = NodeCount。
        /// </summary>
        public Matrix4x4[] Bind { get; }

        /// <summary>hkx 骨骼名命中 FLVER 节点的数量（本角色 127/127）。</summary>
        public int BindHits { get; private set; }

        /// <summary>当前帧姿势全局矩阵 FKMatrix，按 **FLVER 节点索引**。</summary>
        public Matrix4x4[] Pose { get; private set; }

        /// <summary>当前帧蒙皮矩阵 = inverse(Bind) * Pose，按 **FLVER 节点索引**，FLVER 模型空间。</summary>
        public Matrix4x4[] SkinMatrices { get; private set; }

        /// <summary>每根 hkx 骨骼当前世界位置，已转到 Blender Z-up（跟 ChrMesh.Positions 同坐标系）。</summary>
        public Vector3[] BoneWorldPositions { get; private set; }

        /// <summary>FLVER 节点的父索引（= 骨骼层级）。</summary>
        readonly int[] _parents;

        /// <summary>拓扑序：父永远排在子前面。</summary>
        readonly int[] _topo;

        /// <summary>当前帧号。</summary>
        public float Frame { get; private set; }

        /// <summary>当前动画。</summary>
        public DecodedAnimation Anim { get; private set; }

        /// <summary>
        /// 没有动画轨道的节点怎么处理。
        /// true（默认）= 保持绑定姿势下相对父节点的位形（布料/挂件跟着父骨刚性地走）。
        /// false       = 单位 TRS（DSAnimStudio 里 NewBone.LocalTransform 的初值）。
        /// </summary>
        public bool BindPoseForUntrackedNodes = true;

        // ---------------------------------------------------------------- 构造

        public SkinSolver(FLVER2 flver, DecodedSkeleton skel)
        {
            _flver = flver;
            _skel = skel;

            int nn = flver?.Nodes?.Count ?? 0;
            int nb = skel?.BoneCount ?? 0;

            Bind = new Matrix4x4[nn];
            Pose = new Matrix4x4[nn];
            SkinMatrices = new Matrix4x4[nn];
            _nodeLocal = new Matrix4x4[nn];
            _nodeGlobal = new Matrix4x4[nn];
            _nodeToBone = new int[nn];
            _boneToNode = new int[nb];
            _parents = new int[nn];
            BoneWorldPositions = new Vector3[nb];

            for (int i = 0; i < nn; i++)
            {
                Bind[i] = Matrix4x4.Identity;
                Pose[i] = Matrix4x4.Identity;
                SkinMatrices[i] = Matrix4x4.Identity;
                _nodeLocal[i] = Matrix4x4.Identity;
                _nodeGlobal[i] = Matrix4x4.Identity;
                _nodeToBone[i] = -1;
                _parents[i] = flver.Nodes[i].ParentIndex;
            }
            for (int b = 0; b < nb; b++) _boneToNode[b] = -1;

            // ---- 1) FLVER 节点绑定矩阵 -------------------------------------
            // 逐字照抄 NewBone.cs:207-229 的 GetBoneMatrix(FLVER.Node)：
            //
            //     do {
            //         result *= Matrix.CreateScale(scale);
            //         result *= Matrix.CreateRotationX(rot.X);
            //         result *= Matrix.CreateRotationZ(rot.Z);
            //         result *= Matrix.CreateRotationY(rot.Y);
            //         result *= Matrix.CreateTranslation(trans);
            //         parentBone = boneList[parentBone.ParentIndex];   // 往根走
            //     } while (parentBone != null);
            //
            // 行向量约定下「自 * 父 * 祖父 * …」就是全局矩阵。
            // 旋转顺序是 X→Z→Y，**不是** Unity 的 Z→X→Y，也不是 Y→X→Z。
            // 自己用 Quaternion.CreateFromYawPitchRoll / CreateFromEuler 会差一个次序，
            // 症状是整具身体被拧成麻花。
            // SoulsFormats 的 FLVER.Node.ComputeLocalTransform() 就是这段的官方等价实现，
            // 直接调它，不要手拼。
            for (int i = 0; i < nn; i++)
                _nodeLocal[i] = flver.Nodes[i].ComputeLocalTransform();

            // 沿父链累乘。注意节点的 ParentIndex 不保证父在子前面，所以要按拓扑序算。
            var nodeTopo = BuildTopoOrder(_parents, nn);
            for (int k = 0; k < nodeTopo.Length; k++)
            {
                int i = nodeTopo[k];
                int p = _parents[i];
                _nodeGlobal[i] = (p >= 0 && p < nn && p != i)
                    ? _nodeLocal[i] * _nodeGlobal[p]
                    : _nodeLocal[i];
                Bind[i] = _nodeGlobal[i];
            }

            // ---- 2) 节点名 ↔ 骨骼名 ----------------------------------------
            // 照抄 NewAnimSkeleton.cs:113：
            //     int otherBoneIndex = otherSkel.Bones.FindIndex(b => b.Name == hkxBoneName);
            // 严格 ordinal 比较（区分大小写），不剥任何前缀。
            // 只狼的 "[cloth]" / "[omit]" 是骨骼名的一部分，FLVER 节点表里本来也带方括号，
            // 直接比就对了（实测 127/127 全中）。剥前缀反而会把两边都弄坏。
            // 万一某个包大小写不一致，再退一步用 OrdinalIgnoreCase 兜底。
            var exact = new Dictionary<string, int>(StringComparer.Ordinal);
            var loose = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < nn; i++)
            {
                var nm = flver.Nodes[i].Name;
                if (string.IsNullOrEmpty(nm)) continue;
                if (!exact.ContainsKey(nm)) exact[nm] = i;
                if (!loose.ContainsKey(nm)) loose[nm] = i;
            }

            int hits = 0;
            for (int b = 0; b < nb; b++)
            {
                var nm = (skel.BoneNames != null && b < skel.BoneNames.Length) ? skel.BoneNames[b] : null;
                if (string.IsNullOrEmpty(nm)) continue;

                int fi;
                if (!exact.TryGetValue(nm, out fi) && !loose.TryGetValue(nm, out fi))
                    continue;

                _boneToNode[b] = fi;
                _nodeToBone[fi] = b;
                hits++;
            }
            BindHits = hits;

            // 骨骼父链按节点父链走：hkx 的 parentIndices 和 FLVER 的 ParentIndex
            // 对能对上的骨是一致的，但 [omit] 的 nub 骨在 FLVER 里常常是 parent=-1/Disabled。
            // 既然蒙皮按节点索引，姿势也必须沿**节点**父链累乘，否则子骨会甩到原点。
            _topo = nodeTopo;
        }

        // ---------------------------------------------------------------- 拓扑

        static int[] BuildTopoOrder(int[] parents, int n)
        {
            var order = new int[n];
            var state = new byte[n];   // 0=未访问 1=在栈上 2=已输出
            int w = 0;
            var stack = new List<int>(n);

            for (int start = 0; start < n; start++)
            {
                if (state[start] != 0) continue;

                stack.Clear();
                int cur = start, guard = 0;
                while (cur >= 0 && cur < n && state[cur] == 0 && guard++ < n + 8)
                {
                    state[cur] = 1;
                    stack.Add(cur);
                    int p = parents[cur];
                    if (p < 0 || p >= n || p == cur) break;
                    cur = p;
                }

                for (int k = stack.Count - 1; k >= 0; k--)
                {
                    int b = stack[k];
                    if (state[b] == 2) continue;
                    state[b] = 2;
                    order[w++] = b;
                }
            }

            for (int i = 0; i < n; i++)
                if (state[i] != 2) order[w++] = i;

            // 极端坏数据（环）时退回索引序：父链可能算错，但不会越界
            if (w != n)
                for (int i = 0; i < n; i++) order[i] = i;

            return order;
        }

        // ---------------------------------------------------------------- 每帧

        /// <summary>
        /// 设置当前帧（0 起，可小数），内部完成采样 + 合成。
        ///
        /// 严格对应 DSAnimStudio 的三步：
        ///
        /// a) 采样局部 TRS（NewHavokAnimation.cs:210 GetTransformOnFrameByBone）
        ///    轨道 t 的数据属于 hkx 骨骼 TrackToBone[t]，再按名字落到 FLVER 节点上。
        ///    本角色 121 帧 / 109 轨 / 127 骨 / 400 节点：有 18 根 [cloth] 骨没轨道。
        ///
        /// b) 沿父链累乘（NewAnimSkeleton.cs:168 CalculateFKFromLocalTransforms）
        ///      currentMatrix  = L_i
        ///      currentMatrix *= parentTransformation        // 行向量：L_i * parentFK
        ///      FKMatrix_i     = finalMat
        ///    DSAnimStudio 另外维护一条 scaleMatrix 单独累乘
        ///    （finalMat = scaleMatrix * currentMatrix），那是为了处理骨骼带非 1 缩放；
        ///    只狼角色缩放恒为 1，GetMatrixScale() 出来是单位阵，这里省掉，等价。
        ///
        /// c) 蒙皮矩阵（NewAnimSkeleton_FLVER.cs:136 CopyBoneToShaderMatrices）
        ///      inputMatrix = Matrix.Invert(bone.ReferenceFKMatrix) * bone.FKMatrix
        ///    逐字照抄，就这一个式子。
        /// </summary>
        public void SetFrame(DecodedAnimation anim, float frame)
        {
            Anim = anim;
            Frame = frame;

            int nn = NodeCount;
            int nb = BoneCount;

            // ---- a) 采样到节点局部 TRS ----
            var locals = new DecodedTransform[nn];
            var tracked = new bool[nn];

            for (int i = 0; i < nn; i++)
                locals[i] = new DecodedTransform { Translation = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.One };

            bool hasAnim = anim != null && anim.Frames != null && anim.Frames.Count > 0
                           && anim.TrackToBone != null && anim.TrackToBone.Length > 0;

            if (hasAnim)
                SampleTracks(anim, frame, locals, tracked);

            // 没动画轨道的节点：见 BindPoseForUntrackedNodes
            for (int i = 0; i < nn; i++)
            {
                if (tracked[i]) continue;
                locals[i] = BindPoseForUntrackedNodes
                    ? DecomposeLocal(i)
                    : new DecodedTransform { Translation = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.One };
            }

            // ---- b) 沿节点父链累乘 ----
            for (int k = 0; k < _topo.Length; k++)
            {
                int i = _topo[k];
                var local = LocalMatrix(locals[i]);
                int p = _parents[i];
                Pose[i] = (p >= 0 && p < nn && p != i) ? local * Pose[p] : local;
            }

            // ---- c) 蒙皮矩阵 ----
            for (int i = 0; i < nn; i++)
            {
                var bind = Bind[i];
                SkinMatrices[i] = Matrix4x4.Invert(bind, out var bindInv)
                    ? bindInv * Pose[i]
                    : Pose[i];      // 退化 bind（Disabled 节点可能是单位阵）不可逆时原样返回
            }

            // 骨骼世界位置（只对 hkx 有的骨算，画线框用）
            for (int b = 0; b < nb; b++)
            {
                int n = _boneToNode[b];
                BoneWorldPositions[b] = n >= 0 ? Axis.ToBlender(Pose[n].Translation) : Vector3.Zero;
            }
        }

        /// <summary>
        /// 节点 i 在绑定姿势下相对父节点的局部 TRS。
        ///
        /// 直接从 LocalTransform 分解会有问题：Disabled 的 nub 节点常常是单位矩阵，
        /// 分解出来 scale=0 会让整条父链塌成一个点。
        /// 所以这里用 bind 全局矩阵反推相对父的位形，父链层级怎么变都成立。
        /// </summary>
        DecodedTransform DecomposeLocal(int i)
        {
            int p = _parents[i];
            Matrix4x4 local;

            if (p >= 0 && p < NodeCount && p != i)
                local = Matrix4x4.Invert(Bind[p], out var pinv) ? Bind[i] * pinv : _nodeLocal[i];
            else
                local = Bind[i];

            if (!Matrix4x4.Decompose(local, out var s, out var r, out var t))
                return new DecodedTransform { Translation = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.One };

            if (MathF.Abs(s.X) < 1e-6f) s.X = 1f;
            if (MathF.Abs(s.Y) < 1e-6f) s.Y = 1f;
            if (MathF.Abs(s.Z) < 1e-6f) s.Z = 1f;

            var q = r;
            float ql = q.Length();
            q = ql < Eps ? Quaternion.Identity : Quaternion.Normalize(q);

            return new DecodedTransform { Translation = t, Rotation = q, Scale = s };
        }

        /// <summary>
        /// 把 anim 的第 frame 帧采样进节点的局部 TRS。
        ///
        /// 帧号不取整：i0=floor(f)、i1=i0+1、t=f-i0；
        /// 位移/缩放线性插值，旋转 Slerp。
        /// 这与 DSAnimStudio 用 CurrentFrame = CurrentTime / FrameDuration 后交给
        /// 采样器插值等价。
        /// </summary>
        void SampleTracks(DecodedAnimation anim, float frame, DecodedTransform[] locals, bool[] tracked)
        {
            int fc = anim.Frames.Count;
            if (fc == 0) return;

            float f = frame;
            if (f < 0f) f = 0f;
            if (f > fc - 1) f = fc - 1;

            int i0 = (int)MathF.Floor(f);
            if (i0 < 0) i0 = 0;
            if (i0 > fc - 1) i0 = fc - 1;
            int i1 = i0 + 1;
            if (i1 > fc - 1) i1 = fc - 1;
            float t = f - i0;

            var fa = anim.Frames[i0];
            var fb = anim.Frames[i1] ?? fa;
            if (fa == null) return;

            int nt = anim.TrackToBone.Length;
            if (nt > fa.Length) nt = fa.Length;
            if (nt > fb.Length) nt = fb.Length;

            for (int ti = 0; ti < nt; ti++)
            {
                int bone = anim.TrackToBone[ti];
                if (bone < 0 || bone >= _boneToNode.Length) continue;

                int node = _boneToNode[bone];
                if (node < 0 || node >= locals.Length) continue;   // 该骨在 FLVER 里没有同名节点

                var a = fa[ti];
                var b = fb[ti];
                locals[node] = new DecodedTransform
                {
                    Translation = Vector3.Lerp(a.Translation, b.Translation, t),
                    Rotation = Quaternion.Normalize(Quaternion.Slerp(a.Rotation, b.Rotation, t)),
                    Scale = Vector3.Lerp(a.Scale, b.Scale, t),
                };
                tracked[node] = true;
            }
        }

        /// <summary>
        /// 局部 TRS → Matrix4x4。对应 NewBlendableTransform.GetMatrix()：
        ///   Scale * Rotation * Translation（行向量约定）。
        /// </summary>
        static Matrix4x4 LocalMatrix(DecodedTransform t)
        {
            var s = t.Scale;
            if (MathF.Abs(s.X) < Eps) s.X = 1f;
            if (MathF.Abs(s.Y) < Eps) s.Y = 1f;
            if (MathF.Abs(s.Z) < Eps) s.Z = 1f;

            var q = t.Rotation;
            float ql = q.Length();
            q = ql < Eps ? Quaternion.Identity : Quaternion.Normalize(q);

            return Matrix4x4.CreateScale(s)
                 * Matrix4x4.CreateFromQuaternion(q)
                 * Matrix4x4.CreateTranslation(t.Translation);
        }

        // ---------------------------------------------------------------- 蒙皮

        /// <summary>
        /// 把 ChrMesh 的顶点变换到当前姿势。
        ///
        /// 照抄 FlverShader.fx:633-648 的 SkinVert：
        ///
        ///     totalBoneWeight = w0 + w1 + w2 + w3;
        ///     if (totalBoneWeight == 0) return v;                    // 无权重 → 原样返回
        ///     v = (v*M0*w0 + v*M1*w1 + v*M2*w2 + v*M3*w3) / totalBoneWeight;
        ///
        /// 三个必须照做的细节：
        /// 1. 权重和在这里重新归一化（除以 totalBoneWeight）。FS 的权重和是 0.988~1.0，
        ///    不归一化会让整体轻微缩短，别省这一步。
        /// 2. v 是 FLVER 原始顶点坐标，**已经在绑定姿势的模型空间**，只乘 skinMatrix，
        ///    不再乘 bind。多乘一次 bind 就是"模型呈放射状炸开"的直接原因。
        /// 3. 顶点里的骨骼索引是 **FLVER 节点索引**（本文件 0..109，全部落在 Bone flag 节点上），
        ///    直接索引 SkinMatrices 即可，不需要经过 mesh.BoneIndices 重映射
        ///    （那个表在本文件里是空的，count=0）。
        ///
        /// 返回值坐标系与 mesh.Positions 相同（Blender Z-up）。
        /// </summary>
        public Vector3[] Skin(ChrMesh mesh, Vector3[] buffer)
        {
            int n = mesh?.Positions?.Count ?? 0;
            if (buffer == null || buffer.Length != n) buffer = new Vector3[n];
            if (n == 0) return buffer;

            bool hasW = mesh.Weights != null && mesh.Weights.Count == n;
            int nn = NodeCount;
            var skin = SkinMatrices;

            // 顶点多时按行并行（一心 14 万顶点，单线程约 130 ms，24 核能压到个位数）
            // 一心单个网格平均只有 5000 顶点，阈值必须压低，否则全部落到单线程
            if (n >= 1024)
            {
                int bands = Math.Clamp(Environment.ProcessorCount, 1, 32);
                int rows = (n + bands - 1) / bands;
                System.Threading.Tasks.Parallel.For(0, bands, bi =>
                {
                    int s0 = bi * rows;
                    int s1 = Math.Min(n, s0 + rows);
                    SkinRange(mesh, buffer, s0, s1, hasW, nn, skin);
                });
                return buffer;
            }

            SkinRange(mesh, buffer, 0, n, hasW, nn, skin);
            return buffer;
        }

        void SkinRange(ChrMesh mesh, Vector3[] buffer, int from, int to,
                       bool hasW, int nn, Matrix4x4[] skin)
        {
            for (int i = from; i < to; i++)
            {
                var bs = mesh.Positions[i];
                if (!hasW) { buffer[i] = bs; continue; }

                var w = mesh.Weights[i];
                float tw = w.W0 + w.W1 + w.W2 + w.W3;
                if (tw <= Eps) { buffer[i] = bs; continue; }

                // Blender Z-up → FLVER Y-up（Axis.ToBlender 的逆：(x,y,z) → (x,z,-y)）
                var p = new Vector3(bs.X, bs.Z, -bs.Y);

                var acc = Vector3.Zero;
                acc += Blend(p, w.B0, w.W0, skin, nn);
                acc += Blend(p, w.B1, w.W1, skin, nn);
                acc += Blend(p, w.B2, w.W2, skin, nn);
                acc += Blend(p, w.B3, w.W3, skin, nn);
                acc /= tw;

                // FLVER Y-up → Blender Z-up
                buffer[i] = new Vector3(acc.X, -acc.Z, acc.Y);
            }
        }

        static Vector3 Blend(Vector3 p, int node, float weight, Matrix4x4[] skin, int nn)
        {
            if (weight == 0f) return Vector3.Zero;
            // 非法索引退回"不变换"，对应 DSAnimStudio 的 Matrix.Identity
            if (node < 0 || node >= nn) return p * weight;
            return Vector3.Transform(p, skin[node]) * weight;
        }

        /// <summary>把 ChrMesh 转成渲染用的 RenderMesh（顶点已变形，Blender Z-up）。</summary>
        public RenderMesh ToRenderMesh(ChrMesh mesh, Vector3[] buffer, Vector3 color)
        {
            var pos = Skin(mesh, buffer);
            // 法线/UV/索引在动画里不会变，直接复用底层数组的缓存副本，
            // 千万别每帧 ToArray()——一心 60 万索引，复制一次就是 150 ms。
            return new RenderMesh
            {
                Name = mesh.Name,
                Positions = pos,
                Normals = MeshArrays.Normals(mesh),
                Uvs = MeshArrays.Uvs(mesh),
                Indices = MeshArrays.Indices(mesh),
                Color = color,
            };
        }

        /// <summary>诊断：每个 hkx 骨骼 → FLVER 节点映射情况。</summary>
        public string[] DebugBindReport()
        {
            int nb = BoneCount;
            var r = new string[nb];
            for (int b = 0; b < nb; b++)
            {
                var nm = (_skel.BoneNames != null && b < _skel.BoneNames.Length) ? _skel.BoneNames[b] : "?";
                int fi = _boneToNode[b];
                r[b] = fi >= 0 ? (b + " " + nm + " -> node " + fi) : (b + " " + nm + " -> MISS");
            }
            return r;
        }
    }
}
