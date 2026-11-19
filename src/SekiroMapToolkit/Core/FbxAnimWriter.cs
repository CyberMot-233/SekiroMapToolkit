using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

namespace SekiroMapToolkit.Core
{
    /// <summary>
    /// 二进制 FBX 7.4 写出器：骨架 + 逐帧动画。
    ///
    /// 为什么自己写：
    ///   · Blender 4.x 的 FBX 导入只认二进制，ASCII 一律拒绝；
    ///   · AssimpNet 4.1 的 build 里没有 FBX 导出器（只有 collada/obj/gltf 那些）。
    /// 所以按公开的 FBX 二进制结构直接写，生成的文件 Blender / Maya / Unity / UE 都能吃。
    ///
    /// 格式要点（踩过的坑）：
    ///   · 节点头 13 字节：EndOffset(u32) + NumProps(u32) + PropListLen(u32) + NameLen(u8)
    ///   · EndOffset 是**相对整个文件起点**的绝对偏移，不是相对节点起点、也不是相对父节点；
    ///   · 有子节点的节点，子区必须以 13 字节全零记录收尾；
    ///   · 顶层所有节点之后再来一个全零记录，然后才是 footer。
    /// 因为 EndOffset 要绝对偏移，必须写成一条流并回填，不能拿临时流先量长度。
    ///
    /// 坐标系：Y 轴向上（只狼原生），单位 1 = 1 米；Blender 导入时自动转 Z-up。
    /// 旋转：XYZ 欧拉，逐帧做连续性展开，避免 180° 翻转。
    /// </summary>
    static class FbxAnimWriter
    {
        // FBX 时间单位：每秒 46186158000
        const double KTimePerSecond = 46186158000d;

        const long CurveIdBase = 4000000L;

        // ============================================================ 节点模型

        sealed class Node
        {
            public string Name;
            public readonly List<Prop> Props = new List<Prop>();
            public readonly List<Node> Children = new List<Node>();

            public Node(string name) { Name = name; }

            public Node Add(string name)
            {
                var n = new Node(name);
                Children.Add(n);
                return n;
            }

            public Node Add(string name, params Prop[] props)
            {
                var n = new Node(name);
                if (props != null) n.Props.AddRange(props);
                Children.Add(n);
                return n;
            }

            public Node Prop(Prop p) { Props.Add(p); return this; }
        }

        enum PType : byte
        {
            Bool = (byte)'C',
            Int = (byte)'I',
            Float = (byte)'F',
            Double = (byte)'D',
            Long = (byte)'L',
            FloatArray = (byte)'f',
            LongArray = (byte)'l',
            IntArray = (byte)'i',
            DoubleArray = (byte)'d',
            String = (byte)'S',
        }

        struct Prop
        {
            public PType Type;
            public object Value;
        }

        static Prop P(string v) => new Prop { Type = PType.String, Value = v };
        static Prop P(int v) => new Prop { Type = PType.Int, Value = v };
        static Prop P(long v) => new Prop { Type = PType.Long, Value = v };
        static Prop P(double v) => new Prop { Type = PType.Double, Value = v };
        static Prop P(bool v) => new Prop { Type = PType.Bool, Value = v };
        static Prop PArr(float[] v) => new Prop { Type = PType.FloatArray, Value = v };
        static Prop PArr(long[] v) => new Prop { Type = PType.LongArray, Value = v };
        static Prop PInts(int[] v) => new Prop { Type = PType.IntArray, Value = v };
        static Prop PArr(double[] v) => new Prop { Type = PType.DoubleArray, Value = v };

        static Node P70(Node parent, string name, string type, string label, string flags, params Prop[] values)
        {
            var n = parent.Add("P");
            n.Prop(P(name));
            n.Prop(P(type));
            n.Prop(P(label));
            n.Prop(P(flags));
            if (values != null) n.Props.AddRange(values);
            return n;
        }

        // ============================================================ 对外入口

        /// <summary>写一个「骨架 + 动画」的二进制 FBX。</summary>
        public static void Write(DecodedAnimation anim, DecodedSkeleton skel, string path)
        {
            Write(anim, skel, null, path);
        }

        /// <summary>写一个「骨架 + 动画 + 蒙皮网格」的二进制 FBX。</summary>
        public static void Write(DecodedAnimation anim, DecodedSkeleton skel, List<ChrMesh> meshes, string path)
        {
            if (anim == null || !anim.Ok) throw new InvalidOperationException("动画没有解出帧数据");

            int boneCount = skel != null && skel.BoneCount > 0 ? skel.BoneCount : anim.TrackCount;
            var names = new string[boneCount];
            var parents = new int[boneCount];
            for (int i = 0; i < boneCount; i++)
            {
                if (skel != null && i < skel.BoneCount)
                {
                    names[i] = string.IsNullOrEmpty(skel.BoneNames[i]) ? "bone" + i : skel.BoneNames[i];
                    parents[i] = i < skel.Parents.Length ? skel.Parents[i] : -1;
                }
                else
                {
                    names[i] = "track" + i;
                    parents[i] = i == 0 ? -1 : 0;
                }
            }
            names = MakeUnique(names);
            _namesCache = names;
            _bindGlobals = BuildBindGlobals(skel, boneCount, parents);

            // 轨道 → 骨骼
            var trackOfBone = new int[boneCount];
            for (int b = 0; b < boneCount; b++) trackOfBone[b] = -1;
            if (anim.TrackToBone.Length == 0)
            {
                for (int b = 0; b < boneCount && b < anim.TrackCount; b++) trackOfBone[b] = b;
            }
            else
            {
                for (int t = 0; t < anim.TrackToBone.Length && t < anim.TrackCount; t++)
                {
                    int b = anim.TrackToBone[t];
                    if (b >= 0 && b < boneCount) trackOfBone[b] = t;
                }
            }

            int frames = anim.Frames.Count;
            var trs = new DecodedTransform[boneCount][];
            for (int b = 0; b < boneCount; b++)
            {
                trs[b] = new DecodedTransform[frames];
                var rest = RestTransform(skel, b);
                int t = trackOfBone[b];
                for (int f = 0; f < frames; f++)
                    trs[b][f] = (t >= 0 && t < anim.Frames[f].Length) ? anim.Frames[f][t] : rest;
            }

            var euler = BuildEuler(trs, boneCount, frames);
            var keyTimes = new long[frames];
            for (int f = 0; f < frames; f++)
                keyTimes[f] = (long)Math.Round(f * anim.FrameDuration * KTimePerSecond);

            var animated = new List<int>();
            for (int b = 0; b < boneCount; b++)
                if (trackOfBone[b] >= 0 && frames > 1) animated.Add(b);

            // 先把「写哪些曲线」定下来，写入和连接共用同一张表
            var curvePlan = new List<CurveRef>();
            {
                long id = CurveIdBase;
                foreach (int b in animated)
                {
                    for (int ch = 0; ch < 3; ch++)
                    {
                        for (int axis = 0; axis < 3; axis++)
                        {
                            if (ch == 2 && !Varies(trs[b], axis)) continue;
                            curvePlan.Add(new CurveRef { Bone = b, Channel = ch, Axis = axis, Id = id });
                            id++;
                        }
                    }
                }
            }

            // ============================================ 组装节点树
            var rootNodes = new List<Node>();

            var hdr = new Node("FBXHeaderExtension");
            hdr.Add("FBXHeaderVersion", P(1003));
            hdr.Add("FBXVersion", P(7400));
            hdr.Add("EncryptionType", P(0));
            hdr.Add("Creator", P("SekiroMapToolkit"));
            rootNodes.Add(hdr);

            var gs = new Node("GlobalSettings");
            gs.Add("Version", P(1000));
            var gsP = gs.Add("Properties70");
            P70(gsP, "UpAxis", "int", "Integer", "", P(1));
            P70(gsP, "UpAxisSign", "int", "Integer", "", P(1));
            P70(gsP, "FrontAxis", "int", "Integer", "", P(2));
            P70(gsP, "FrontAxisSign", "int", "Integer", "", P(1));
            P70(gsP, "CoordAxis", "int", "Integer", "", P(0));
            P70(gsP, "CoordAxisSign", "int", "Integer", "", P(1));
            P70(gsP, "OriginalUpAxis", "int", "Integer", "", P(1));
            P70(gsP, "OriginalUpAxisSign", "int", "Integer", "", P(1));
            P70(gsP, "UnitScaleFactor", "double", "Number", "", P(1.0));
            P70(gsP, "OriginalUnitScaleFactor", "double", "Number", "", P(1.0));
            P70(gsP, "TimeMode", "enum", "", "", P(0));
            P70(gsP, "TimeSpanStart", "KTime", "Time", "", P(0L));
            P70(gsP, "TimeSpanStop", "KTime", "Time", "",
                P(keyTimes.Length > 0 ? keyTimes[keyTimes.Length - 1] : 0L));
            P70(gsP, "CustomFrameRate", "double", "Number", "",
                P(1.0 / Math.Max(1e-6, (double)anim.FrameDuration)));
            rootNodes.Add(gs);

            var docs = new Node("Documents");
            docs.Add("Count", P(1));
            var doc = docs.Add("Document");
            doc.Prop(P(100000L)).Prop(P("")).Prop(P("Scene"));
            var docP = doc.Add("Properties70");
            P70(docP, "SourceObject", "object", "", "");
            P70(docP, "ActiveAnimStackName", "KString", "", "", P(SafeLbl(anim.Name)));
            doc.Add("RootNode", P(0L));
            rootNodes.Add(docs);

            var objects = new Node("Objects");
            var conns = new Node("Connections");

            int curveNodeWritten = animated.Count * 3;
            int curveWritten = curvePlan.Count;

            var defs = new Node("Definitions");
            defs.Add("Version", P(100));
            defs.Add("Count", P(boneCount + 2 + curveNodeWritten + curveWritten));
            var defModel = defs.Add("ObjectType");
            defModel.Prop(P("Model"));
            defModel.Add("Count", P(boneCount));
            var defStack = defs.Add("ObjectType");
            defStack.Prop(P("AnimationStack"));
            defStack.Add("Count", P(1));
            var defLayer = defs.Add("ObjectType");
            defLayer.Prop(P("AnimationLayer"));
            defLayer.Add("Count", P(1));
            var defCn = defs.Add("ObjectType");
            defCn.Prop(P("AnimationCurveNode"));
            defCn.Add("Count", P(curveNodeWritten));
            var defCurve = defs.Add("ObjectType");
            defCurve.Prop(P("AnimationCurve"));
            defCurve.Add("Count", P(curveWritten));
            rootNodes.Add(defs);

            // -------- 骨骼
            for (int b = 0; b < boneCount; b++)
            {
                var m = objects.Add("Model");
                // Blender 的 FBX importer 取 props[-2] 并按其 \0\1 拆分，
                // 所以「名字\0\1Model」必须是倒数第二个属性，子类名放最后
                m.Prop(P(BoneId(b)))
                 .Prop(P(names[b] + "\u0000\u0001Model"))
                 .Prop(P("LimbNode"));
                m.Add("Version", P(232));
                var pp = m.Add("Properties70");
                var d0 = trs[b][0];
                P70(pp, "Lcl Translation", "Lcl Translation", "", "A",
                    P((double)d0.Translation.X), P((double)d0.Translation.Y), P((double)d0.Translation.Z));
                var e0 = euler[b][0];
                P70(pp, "Lcl Rotation", "Lcl Rotation", "", "A",
                    P((double)e0.X), P((double)e0.Y), P((double)e0.Z));
                P70(pp, "Lcl Scaling", "Lcl Scaling", "", "A",
                    P((double)d0.Scale.X), P((double)d0.Scale.Y), P((double)d0.Scale.Z));
                P70(pp, "RotationOrder", "enum", "", "", P(0));
                P70(pp, "RotationActive", "bool", "", "", P(1));
                P70(pp, "InheritType", "enum", "", "", P(1));
                P70(pp, "ScalingMax", "Vector3D", "Vector", "", P(0.0), P(0.0), P(0.0));
                P70(pp, "DefaultAttributeIndex", "int", "Integer", "", P(0));
                m.Add("Shading", P(true));
                m.Add("Culling", P("CullingOff"));
            }

            // -------- 动画栈
            const long stackId = 2000000L;
            const long layerId = 2100000L;
            long span = keyTimes.Length > 0 ? keyTimes[keyTimes.Length - 1] : 0L;

            var stack = objects.Add("AnimationStack");
            stack.Prop(P(stackId)).Prop(P(SafeLbl(anim.Name) + "\u0000\u0001AnimStack")).Prop(P(""));
            var sp = stack.Add("Properties70");
            P70(sp, "LocalStart", "KTime", "Time", "", P(0L));
            P70(sp, "LocalStop", "KTime", "Time", "", P(span));
            P70(sp, "ReferenceStart", "KTime", "Time", "", P(0L));
            P70(sp, "ReferenceStop", "KTime", "Time", "", P(span));

            var layer = objects.Add("AnimationLayer");
            layer.Prop(P(layerId)).Prop(P("BaseLayer\u0000\u0001AnimLayer")).Prop(P(""));

            // -------- 曲线节点 + 曲线
            foreach (int b in animated)
            {
                WriteCurveNode(objects, CurveNodeId(b, 0), "T");
                WriteCurveNode(objects, CurveNodeId(b, 1), "R");
                WriteCurveNode(objects, CurveNodeId(b, 2), "S");
            }

            foreach (var cr in curvePlan)
            {
                var values = new float[frames];
                for (int f = 0; f < frames; f++)
                {
                    var t = trs[cr.Bone][f];
                    var e = euler[cr.Bone][f];
                    values[f] = cr.Channel == 0
                        ? (cr.Axis == 0 ? t.Translation.X : cr.Axis == 1 ? t.Translation.Y : t.Translation.Z)
                        : cr.Channel == 1
                            ? (cr.Axis == 0 ? e.X : cr.Axis == 1 ? e.Y : e.Z)
                            : (cr.Axis == 0 ? t.Scale.X : cr.Axis == 1 ? t.Scale.Y : t.Scale.Z);
                }
                WriteCurve(objects, cr.Id, cr.Axis, keyTimes, values);
            }

            // -------- 连接
            for (int b = 0; b < boneCount; b++)
            {
                long parentId = (parents[b] >= 0 && parents[b] < boneCount) ? BoneId(parents[b]) : 0L;
                var c = conns.Add("C");
                c.Prop(P("OO")).Prop(P(BoneId(b))).Prop(P(parentId));
            }
            {
                var c = conns.Add("C");
                c.Prop(P("OO")).Prop(P(layerId)).Prop(P(stackId));
            }
            foreach (int b in animated)
            {
                for (int ch = 0; ch < 3; ch++)
                {
                    var c1 = conns.Add("C");
                    c1.Prop(P("OO")).Prop(P(CurveNodeId(b, ch))).Prop(P(layerId));
                    var c2 = conns.Add("C");
                    c2.Prop(P("OP")).Prop(P(CurveNodeId(b, ch))).Prop(P(BoneId(b)))
                      .Prop(P(ch == 0 ? "Lcl Translation" : ch == 1 ? "Lcl Rotation" : "Lcl Scaling"));
                }
            }
            foreach (var cr in curvePlan)
            {
                var c = conns.Add("C");
                c.Prop(P("OP")).Prop(P(cr.Id)).Prop(P(CurveNodeId(cr.Bone, cr.Channel)))
                 .Prop(P("d|" + (cr.Axis == 0 ? "X" : cr.Axis == 1 ? "Y" : "Z")));
            }

            rootNodes.Add(objects);
            rootNodes.Add(conns);

            // -------- 网格（可选）
            var meshList = meshes ?? new List<ChrMesh>();
            for (int i = 0; i < meshList.Count; i++)
            {
                var gm = meshList[i];
                if (gm.Positions.Count == 0 || gm.Indices.Count == 0) continue;
                WriteMesh(objects, conns, gm, 5000000L + i * 100L, boneCount);
            }

            var takes = new Node("Takes");
            takes.Add("Current", P("Take 001"));
            rootNodes.Add(takes);

            // ============================================ 写二进制
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs, new UTF8Encoding(false)))
            {
                WriteMagic(bw);
                foreach (var n in rootNodes) WriteNode(bw, n);
                WriteNullRecord(bw);
                WriteFooter(bw);
            }
        }

        struct CurveRef
        {
            public int Bone;
            public int Channel;
            public int Axis;
            public long Id;
        }

        // ============================================================ 网格

        /// <summary>写一个蒙皮网格（Geometry + Model + Deformer + SubDeformer）。</summary>
        static void WriteMesh(Node objects, Node conns, ChrMesh gm, long baseId,
                              int boneCount)
        {
            long geoId = baseId + 1;
            long modelId = baseId + 2;
            long skinId = baseId + 3;
            long clusterBase = baseId + 10;

            int vcount = gm.Positions.Count;
            int icount = gm.Indices.Count;

            // -------- Geometry
            var geo = objects.Add("Geometry");
            geo.Prop(P(geoId)).Prop(P(gm.Name + "\u0000\u0001Geometry")).Prop(P("Mesh"));
            geo.Add("GeometryVersion", P(124));

            // 顶点
            var verts = new double[vcount * 3];
            for (int i = 0; i < vcount; i++)
            {
                verts[i * 3 + 0] = gm.Positions[i].X;
                verts[i * 3 + 1] = gm.Positions[i].Y;
                verts[i * 3 + 2] = gm.Positions[i].Z;
            }
            var vn = geo.Add("Vertices", new Prop { Type = PType.DoubleArray, Value = verts });

            // FBX 的 PolygonVertexIndex 语义：每个多边形的最后一个索引写成 ~index（= -(index+1)）
            // 用来标记这个多边形到此结束。不这么写 Blender 会解出 0 个面。
            var idxArr = new int[icount];
            for (int t = 0; t + 2 < icount; t += 3)
            {
                idxArr[t] = gm.Indices[t];
                idxArr[t + 1] = gm.Indices[t + 1];
                idxArr[t + 2] = ~gm.Indices[t + 2];
            }
            geo.Add("PolygonVertexIndex", PInts(idxArr));

            // 法线 / UV 走 LayerElement
            var normals = new double[vcount * 3];
            for (int i = 0; i < vcount; i++)
            {
                normals[i * 3 + 0] = gm.Normals[i].X;
                normals[i * 3 + 1] = gm.Normals[i].Y;
                normals[i * 3 + 2] = gm.Normals[i].Z;
            }
            var ln = geo.Add("LayerElementNormal");
            ln.Prop(P(0));
            ln.Add("Version", P(101));
            ln.Add("Name", P(""));
            ln.Add("MappingInformationType", P("ByPolygonVertex"));
            ln.Add("ReferenceInformationType", P("Direct"));
            ln.Add("Normals", new Prop { Type = PType.DoubleArray, Value = normals });

            var uvs = new double[vcount * 2];
            for (int i = 0; i < vcount; i++)
            {
                uvs[i * 2 + 0] = gm.Uvs[i].X;
                uvs[i * 2 + 1] = gm.Uvs[i].Y;
            }
            var lu = geo.Add("LayerElementUV");
            lu.Prop(P(0));
            lu.Add("Version", P(101));
            lu.Add("Name", P("UVChannel_1"));
            lu.Add("MappingInformationType", P("ByPolygonVertex"));
            lu.Add("ReferenceInformationType", P("IndexToDirect"));
            lu.Add("UV", new Prop { Type = PType.DoubleArray, Value = uvs });

            var lm = geo.Add("LayerElementMaterial");
            lm.Prop(P(0));
            lm.Add("Version", P(101));
            lm.Add("Name", P(""));
            lm.Add("MappingInformationType", P("AllSame"));
            lm.Add("ReferenceInformationType", P("IndexToDirect"));
            lm.Add("Materials", PInts(new[] { 0 }));

            var ll = geo.Add("Layer");
            ll.Prop(P(0));
            ll.Add("Version", P(100));
            var lln = ll.Add("LayerElement");
            lln.Add("Type", P("LayerElementNormal"));
            lln.Add("TypedIndex", P(0));
            var llu = ll.Add("LayerElement");
            llu.Add("Type", P("LayerElementUV"));
            llu.Add("TypedIndex", P(0));
            var llm = ll.Add("LayerElement");
            llm.Add("Type", P("LayerElementMaterial"));
            llm.Add("TypedIndex", P(0));

            // -------- Model
            var model = objects.Add("Model");
            model.Prop(P(modelId)).Prop(P(gm.Name + "\u0000\u0001Model")).Prop(P("Mesh"));
            model.Add("Version", P(232));
            var mp = model.Add("Properties70");
            P70(mp, "Lcl Translation", "Lcl Translation", "", "A", P(0.0), P(0.0), P(0.0));
            P70(mp, "Lcl Rotation", "Lcl Rotation", "", "A", P(0.0), P(0.0), P(0.0));
            P70(mp, "Lcl Scaling", "Lcl Scaling", "", "A", P(1.0), P(1.0), P(1.0));
            P70(mp, "DefaultAttributeIndex", "int", "Integer", "", P(0));
            model.Add("Shading", P(true));
            model.Add("Culling", P("CullingOff"));

            // Geometry → Model
            {
                var c = conns.Add("C");
                c.Prop(P("OO")).Prop(P(geoId)).Prop(P(modelId));
            }

            // -------- 蒙皮 Deformer
            var skin = objects.Add("Deformer");
            skin.Prop(P(skinId)).Prop(P("Skin\u0000\u0001Deformer")).Prop(P("Skin"));
            skin.Add("Version", P(101));
            skin.Add("Link_DeformAcuracy", P(50.0));

            {
                var c = conns.Add("C");
                c.Prop(P("OO")).Prop(P(skinId)).Prop(P(geoId));
            }

            // -------- 骨骼簇
            bool hasWeights = gm.Weights.Count == vcount;
            if (hasWeights)
            {
                // 每根骨骼收集「受它影响的顶点 + 权重」
                var byBone = new Dictionary<int, List<(int vi, float w)>>();
                for (int vi = 0; vi < vcount; vi++)
                {
                    var bw = gm.Weights[vi];
                    AddWeight(byBone, bw.B0, bw.W0, vi);
                    AddWeight(byBone, bw.B1, bw.W1, vi);
                    AddWeight(byBone, bw.B2, bw.W2, vi);
                    AddWeight(byBone, bw.B3, bw.W3, vi);
                }

                int k = 0;
                foreach (var kv in byBone.OrderBy(x => x.Key))
                {
                    int bone = kv.Key;
                    if (bone < 0 || bone >= boneCount) continue;
                    var list = kv.Value;
                    if (list.Count == 0) continue;

                    long cid = clusterBase + k * 2;
                    k++;

                    var cl = objects.Add("Deformer");
                    cl.Prop(P(cid)).Prop(P(namesCache(bone) + "\u0000\u0001SubDeformer")).Prop(P("Cluster"));
                    cl.Add("Version", P(100));
                    cl.Add("UserData", P(""));

                    var indexArr = new int[list.Count];
                    var weightArr = new double[list.Count];
                    for (int j = 0; j < list.Count; j++)
                    {
                        indexArr[j] = list[j].vi;
                        weightArr[j] = list[j].w;
                    }
                    cl.Add("Indexes", PInts(indexArr));
                    cl.Add("Weights", new Prop { Type = PType.DoubleArray, Value = weightArr });

                    // TransformLink 必须是这根骨骼的「全局绑定姿态」，不能写单位阵
                    // —— 否则 Blender 会把骨骼绑到原点，蒙皮整个散掉。
                    var bind = _bindGlobals != null && bone < _bindGlobals.Length
                        ? _bindGlobals[bone]
                        : Matrix4x4.Identity;

                    cl.Add("Transform", new Prop
                    {
                        Type = PType.DoubleArray,
                        Value = new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 }
                    });
                    cl.Add("TransformLink", new Prop
                    {
                        Type = PType.DoubleArray,
                        Value = ToFbxMatrix(bind)
                    });
                    // Blender 的绑定流程会读这个；缺了它 armature_matrix 是 None，
                    // 整段蒙皮会被跳过（表现就是 mesh 有、权重全 0）
                    cl.Add("TransformAssociateModel", new Prop
                    {
                        Type = PType.DoubleArray,
                        Value = new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 }
                    });

                    // 连接方向照 Blender 官方导出器 export_fbx_bin.py:3052
                    //   connections.append((b"OO", bo_obj.fbx_uuid, clstr_key))   ← 骨骼在前，cluster 在后
                    // 反着写会让 importer 从 cluster 侧找不到骨骼，蒙皮整段被跳过。
                    var c1 = conns.Add("C");
                    c1.Prop(P("OO")).Prop(P(cid)).Prop(P(skinId));
                    var c2 = conns.Add("C");
                    c2.Prop(P("OO")).Prop(P(BoneId(bone))).Prop(P(cid));
                }
            }

            // Model → 根（0）
            {
                var c = conns.Add("C");
                c.Prop(P("OO")).Prop(P(modelId)).Prop(P(0L));
            }
        }

        static string[] _namesCache;
        static Matrix4x4[] _bindGlobals;

        /// <summary>把全局绑定矩阵算出来：父链连乘。</summary>
        static Matrix4x4[] BuildBindGlobals(DecodedSkeleton skel, int boneCount, int[] parents)
        {
            var g = new Matrix4x4[boneCount];
            for (int b = 0; b < boneCount; b++)
            {
                Matrix4x4 local = Matrix4x4.Identity;
                if (skel != null && b < skel.ReferencePose.Length)
                    local = skel.ReferencePose[b];

                int p = b < parents.Length ? parents[b] : -1;
                if (p >= 0 && p < boneCount && p != b)
                    g[b] = local * g[p];
                else
                    g[b] = local;
            }
            return g;
        }

        /// <summary>Matrix4x4 → FBX 的 16 个 double（列主序、行向量约定）。</summary>
        static double[] ToFbxMatrix(Matrix4x4 m)
        {
            return new double[]
            {
                m.M11, m.M12, m.M13, m.M14,
                m.M21, m.M22, m.M23, m.M24,
                m.M31, m.M32, m.M33, m.M34,
                m.M41, m.M42, m.M43, m.M44,
            };
        }

        static string namesCache(int bone)
        {
            return (_namesCache != null && bone < _namesCache.Length) ? _namesCache[bone] : ("bone" + bone);
        }

        static void AddWeight(Dictionary<int, List<(int vi, float w)>> map, int bone, float w, int vi)
        {
            if (w <= 1e-6f) return;
            if (!map.TryGetValue(bone, out var list))
            {
                list = new List<(int, float)>();
                map[bone] = list;
            }
            list.Add((vi, w));
        }

        // ============================================================ 曲线节点

        static void WriteCurveNode(Node objects, long id, string ch)
        {
            var n = objects.Add("AnimationCurveNode");
            n.Prop(P(id)).Prop(P(ch + "\u0000\u0001AnimCurveNode")).Prop(P(""));
            var pp = n.Add("Properties70");
            P70(pp, "d|X", "Number", "", "A", P(0.0));
            P70(pp, "d|Y", "Number", "", "A", P(0.0));
            P70(pp, "d|Z", "Number", "", "A", P(0.0));
        }

        static void WriteCurve(Node objects, long id, int axis, long[] times, float[] values)
        {
            string ax = axis == 0 ? "X" : axis == 1 ? "Y" : "Z";
            var c = objects.Add("AnimationCurve");
            c.Prop(P(id)).Prop(P(ax + "\u0000\u0001AnimCurve")).Prop(P(""));
            c.Add("Default", P((double)values[0]));
            c.Add("KeyVer", P(4008));
            c.Add("KeyTime", PArr((long[])times.Clone()));
            c.Add("KeyValueFloat", PArr(values));
            c.Add("KeyAttrRefCount", PInts(new[] { times.Length }));
        }

        static long BoneId(int b) => 1000000L + b + 1;
        static long CurveNodeId(int b, int ch) => 3000000L + b * 3 + ch + 1;

        static bool Varies(DecodedTransform[] frames, int axis)
        {
            if (frames.Length < 2) return false;
            float v0 = axis == 0 ? frames[0].Scale.X : axis == 1 ? frames[0].Scale.Y : frames[0].Scale.Z;
            for (int f = 1; f < frames.Length; f++)
            {
                float v = axis == 0 ? frames[f].Scale.X : axis == 1 ? frames[f].Scale.Y : frames[f].Scale.Z;
                if (Math.Abs(v - v0) > 1e-5f) return true;
            }
            return false;
        }

        // ============================================================ 二进制序列化

        static void WriteMagic(BinaryWriter w)
        {
            var bytes = Encoding.ASCII.GetBytes("Kaydara FBX Binary  ");
            w.Write(bytes);                 // 20 字节
            w.Write((byte)0x00);
            w.Write((byte)0x1A);
            w.Write((byte)0x00);
            w.Write((uint)7400);            // 到此累计 27 字节
        }

        /// <summary>
        /// 写一个节点。EndOffset 是相对文件起点的绝对偏移，所以必须写完子节点再回填。
        /// </summary>
        static void WriteNode(BinaryWriter w, Node n)
        {
            var nameBytes = Encoding.UTF8.GetBytes(n.Name);

            byte[] propBuf;
            using (var ms = new MemoryStream())
            using (var pw = new BinaryWriter(ms))
            {
                foreach (var p in n.Props) WriteProp(pw, p);
                pw.Flush();
                propBuf = ms.ToArray();
            }

            long start = w.BaseStream.Position;

            w.Write((uint)0);                       // EndOffset 占位
            w.Write((uint)n.Props.Count);
            w.Write((uint)propBuf.Length);
            w.Write((byte)nameBytes.Length);
            w.Write(nameBytes);
            w.Write(propBuf);

            if (n.Children.Count > 0)
            {
                foreach (var c in n.Children) WriteNode(w, c);
                WriteNullRecord(w);
            }

            long end = w.BaseStream.Position;
            if (end - start > uint.MaxValue)
                throw new InvalidDataException("节点超过 4 GB，FBX 二进制装不下");

            w.Seek((int)start, SeekOrigin.Begin);
            w.Write((uint)end);                     // 绝对偏移
            w.Seek((int)end, SeekOrigin.Begin);
        }

        static void WriteNullRecord(BinaryWriter w)
        {
            w.Write(new byte[13]);
        }

        static void WriteProp(BinaryWriter w, Prop p)
        {
            w.Write((byte)p.Type);
            switch (p.Type)
            {
                case PType.Bool: w.Write((byte)((bool)p.Value ? 1 : 0)); break;
                case PType.Int: w.Write((int)p.Value); break;
                case PType.Float: w.Write((float)p.Value); break;
                case PType.Double: w.Write((double)p.Value); break;
                case PType.Long: w.Write((long)p.Value); break;

                case PType.String:
                    {
                        var b = Encoding.UTF8.GetBytes((string)p.Value ?? "");
                        w.Write((uint)b.Length);
                        w.Write(b);
                        break;
                    }
                case PType.FloatArray:
                    {
                        var a = (float[])p.Value;
                        w.Write((uint)a.Length);
                        w.Write((uint)0);
                        w.Write((uint)(a.Length * 4));
                        foreach (var v in a) w.Write(v);
                        break;
                    }
                case PType.LongArray:
                    {
                        var a = (long[])p.Value;
                        w.Write((uint)a.Length);
                        w.Write((uint)0);
                        w.Write((uint)(a.Length * 8));
                        foreach (var v in a) w.Write(v);
                        break;
                    }
                case PType.DoubleArray:
                    {
                        var a = (double[])p.Value;
                        w.Write((uint)a.Length);
                        w.Write((uint)0);
                        w.Write((uint)(a.Length * 8));
                        foreach (var v in a) w.Write(v);
                        break;
                    }
                case PType.IntArray:
                    {
                        var a = (int[])p.Value;
                        w.Write((uint)a.Length);
                        w.Write((uint)0);
                        w.Write((uint)(a.Length * 4));
                        foreach (var v in a) w.Write(v);
                        break;
                    }
                default:
                    throw new NotSupportedException("未支持的属性类型 " + p.Type);
            }
        }

        static void WriteFooter(BinaryWriter w)
        {
            // FBX 7.4 二进制 footer：16 字节标记 + 4 字节 0 + 版本 + 120 字节 0 + 再来一遍标记
            var stamp = new byte[]
            {
                0xFA, 0xBC, 0xAB, 0x09, 0xD0, 0xC8, 0xD4, 0x66,
                0xB1, 0x76, 0xFB, 0x83, 0x1C, 0xF7, 0x26, 0x7E,
            };
            w.Write(stamp);
            w.Write((uint)0);
            w.Write((uint)7400);
            w.Write(new byte[120]);
            w.Write(stamp);
            w.Write((uint)0);
            w.Write((uint)0);
        }

        // ============================================================ 数学

        static DecodedTransform RestTransform(DecodedSkeleton skel, int b)
        {
            var d = new DecodedTransform
            {
                Translation = Vector3.Zero,
                Rotation = Quaternion.Identity,
                Scale = Vector3.One,
            };
            if (skel != null && b < skel.ReferencePose.Length)
            {
                if (Matrix4x4.Decompose(skel.ReferencePose[b], out var s, out var r, out var t))
                {
                    d.Translation = t;
                    d.Rotation = r;
                    d.Scale = s;
                }
                else d.Translation = skel.ReferencePose[b].Translation;
            }
            return d;
        }

        static Vector3[][] BuildEuler(DecodedTransform[][] trs, int boneCount, int frames)
        {
            var result = new Vector3[boneCount][];
            for (int b = 0; b < boneCount; b++)
            {
                result[b] = new Vector3[frames];
                Vector3 prev = Vector3.Zero;
                for (int f = 0; f < frames; f++)
                {
                    var q = trs[b][f].Rotation;
                    if (q.LengthSquared() < 1e-12f) q = Quaternion.Identity;
                    q = Quaternion.Normalize(q);
                    var e = ToEulerDegrees(q);
                    if (f > 0) e = Unwrap(prev, e);
                    result[b][f] = e;
                    prev = e;
                }
            }
            return result;
        }

        static Vector3 ToEulerDegrees(Quaternion q)
        {
            double x = q.X, y = q.Y, z = q.Z, w = q.W;
            double sinr = 2.0 * (w * x + y * z);
            double cosr = 1.0 - 2.0 * (x * x + y * y);
            double rx = Math.Atan2(sinr, cosr);

            double sinp = 2.0 * (w * y - z * x);
            double ry = Math.Abs(sinp) >= 1.0 ? Math.Sign(sinp) * Math.PI / 2.0 : Math.Asin(sinp);

            double siny = 2.0 * (w * z + x * y);
            double cosy = 1.0 - 2.0 * (y * y + z * z);
            double rz = Math.Atan2(siny, cosy);

            const double R2D = 180.0 / Math.PI;
            return new Vector3((float)(rx * R2D), (float)(ry * R2D), (float)(rz * R2D));
        }

        static Vector3 Unwrap(Vector3 prev, Vector3 cur) =>
            new Vector3(Unwrap1(prev.X, cur.X), Unwrap1(prev.Y, cur.Y), Unwrap1(prev.Z, cur.Z));

        static float Unwrap1(float prev, float cur)
        {
            while (cur - prev > 180f) cur -= 360f;
            while (cur - prev < -180f) cur += 360f;
            return cur;
        }

        static string[] MakeUnique(string[] names)
        {
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var outp = new string[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                var n = SafeLbl(names[i]);
                if (seen.TryGetValue(n, out int c))
                {
                    seen[n] = c + 1;
                    n = n + "_" + (c + 1);
                }
                else seen[n] = 0;
                outp[i] = n;
            }
            return outp;
        }

        static string SafeLbl(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unnamed";
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
                sb.Append(c == '"' || c == ':' || c == ',' || c == '\n' || c == '\r' ? '_' : c);
            return sb.ToString();
        }
    }
}
