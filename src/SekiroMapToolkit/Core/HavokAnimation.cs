using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using Havoc.Objects;
using Havoc.Reflection;
using SapAnimation = SoulsAssetPipeline.Animation;

namespace SekiroMapToolkit.Core
{
    /// <summary>一条已解压的动画：每帧、每条轨道一个 TRS。</summary>
    class DecodedAnimation
    {
        public string Name;
        public long Id;
        public string SkeletonName;
        public float Duration;
        public float FrameDuration;
        public int FrameCount;
        public int TrackCount;

        /// <summary>轨道下标 → 骨骼下标（来自 hkaAnimationBinding）。</summary>
        public int[] TrackToBone = Array.Empty<int>();

        /// <summary>[帧][轨道] 的 TRS。</summary>
        public List<DecodedTransform[]> Frames = new List<DecodedTransform[]>();

        public bool Ok => Frames.Count > 0 && TrackCount > 0;
    }

    /// <summary>一帧里一条轨道的位移 / 旋转 / 缩放。</summary>
    struct DecodedTransform
    {
        public Vector3 Translation;
        public Quaternion Rotation;
        public Vector3 Scale;
    }

    /// <summary>一个角色的骨架。</summary>
    class DecodedSkeleton
    {
        public string Name;
        public string[] BoneNames = Array.Empty<string>();
        public short[] Parents = Array.Empty<short>();
        public Matrix4x4[] ReferencePose = Array.Empty<Matrix4x4>();

        public int BoneCount => BoneNames.Length;
    }

    /// <summary>
    /// 只狼 Havok 动画解析。这就是 DSAnimStudio 干的那件事，收进来自己做。
    ///
    /// 数据路径：
    ///   chr\&lt;id&gt;.anibnd.dcx           tae\*.tae（动画表）+ hkx\skeleton.hkx（骨架）
    ///   chr\&lt;id&gt;_a000_hi.anibnd.dcx   a000_*.hkx（关键帧，TAG0 + compendium）
    ///
    /// 关键帧 HKX 是 Havok 2016 的 tagfile（TAG0），里面的 TCRF 段要求一份类型库，
    /// 那份类型库就是同目录的 *.compendium（TCM0）。没有它读不出任何字段 —— 这是最初
    /// 一直报 "Unexpected signature" 的根本原因。
    /// </summary>
    static class HavokAnimation
    {
        const BindingFlags InstancePublic = BindingFlags.Public | BindingFlags.Instance;

        static Assembly _havoc;

        static Assembly HavocAsm
        {
            get
            {
                if (_havoc != null) return _havoc;
                var local = Path.Combine(AppContext.BaseDirectory, "Havoc.dll");
                _havoc = Assembly.LoadFrom(File.Exists(local)
                    ? local
                    : Path.Combine(AppContext.BaseDirectory, "Havoc.dll"));
                return _havoc;
            }
        }

        /// <summary>读一个 TAG0 HKX，返回根对象；失败返回 null。</summary>
        static object ReadTagfile(byte[] hkx, byte[] compendium)
        {
            var t = HavocAsm.GetType("Havoc.IO.Tagfile.Binary.HkBinaryTagfileReader");
            if (t == null) return null;
            var m = t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                     .FirstOrDefault(x => x.Name == "Read"
                                       && x.GetParameters().Length == 2
                                       && x.GetParameters()[0].ParameterType == typeof(byte[]));
            if (m == null) return null;
            try { return m.Invoke(null, new object[] { hkx, compendium }); }
            catch { return null; }
        }

        // ---------------------------------------------------------------- 字段访问

        static string TypeNameOf(object hkClass)
        {
            if (hkClass == null) return null;
            var t = hkClass.GetType().GetProperty("Type", InstancePublic)?.GetValue(hkClass);
            return t?.GetType().GetProperty("Name", InstancePublic)?.GetValue(t) as string;
        }

        static List<KeyValuePair<string, object>> Dict(object hkClass)
        {
            var list = new List<KeyValuePair<string, object>>();
            if (hkClass == null) return list;
            var dict = hkClass.GetType().GetProperty("Value", InstancePublic)?.GetValue(hkClass);
            if (!(dict is System.Collections.IEnumerable en)) return list;
            foreach (var kv in en)
            {
                var kt = kv.GetType();
                var key = kt.GetProperty("Key")?.GetValue(kv);
                var val = kt.GetProperty("Value")?.GetValue(kv);
                var name = key?.GetType().GetProperty("Name", InstancePublic)?.GetValue(key) as string;
                list.Add(new KeyValuePair<string, object>(name, val));
            }
            return list;
        }

        static object Field(object hkClass, string name)
        {
            foreach (var kv in Dict(hkClass))
                if (kv.Key == name) return kv.Value;
            return null;
        }

        static List<object> ArrayItems(object hkArray)
        {
            var list = new List<object>();
            if (hkArray == null) return list;
            if (hkArray.GetType().Name != "HkArray") return list;
            if (hkArray.GetType().GetProperty("Value", InstancePublic)?.GetValue(hkArray) is System.Collections.IList il)
                foreach (var x in il) list.Add(x);
            return list;
        }

        /// <summary>剥掉 HkPtr 包装。</summary>
        static object Deref(object hk)
        {
            int guard = 0;
            while (hk != null && hk.GetType().Name == "HkPtr" && guard++ < 32)
                hk = hk.GetType().GetProperty("Value", InstancePublic)?.GetValue(hk);
            return hk;
        }

        static float Float(object hk)
        {
            var v = hk?.GetType().GetProperty("Value", InstancePublic)?.GetValue(hk);
            return v is IConvertible c ? Convert.ToSingle(c, CultureInfo.InvariantCulture) : 0f;
        }

        static int Int(object hk)
        {
            var v = hk?.GetType().GetProperty("Value", InstancePublic)?.GetValue(hk);
            return v is IConvertible c ? Convert.ToInt32(c, CultureInfo.InvariantCulture) : 0;
        }

        static string Str(object hk)
        {
            return hk?.GetType().GetProperty("Value", InstancePublic)?.GetValue(hk) as string;
        }

        static byte[] Bytes(object hkArray)
        {
            var list = ArrayItems(hkArray);
            var buf = new byte[list.Count];
            for (int i = 0; i < list.Count; i++)
                buf[i] = (byte)Int(list[i]);
            return buf;
        }

        // ---------------------------------------------------------------- 动画

        /// <summary>
        /// 从一个关键帧 HKX 解出完整动画。
        /// </summary>
        /// <param name="hkx">a000_xxxxxx.hkx 的字节</param>
        /// <param name="compendium">同包的 *.compendium（TCM0），不能为空</param>
        /// <param name="name">显示名</param>
        /// <param name="id">TAE 里的动画 ID</param>
        public static DecodedAnimation Decode(byte[] hkx, byte[] compendium, string name, long id)
        {
            var anim = new DecodedAnimation { Name = name, Id = id };
            if (hkx == null || hkx.Length < 32 || compendium == null || compendium.Length < 16) return anim;

            var root = ReadTagfile(hkx, compendium);
            if (root == null) return anim;

            var variants = ArrayItems(Field(root, "namedVariants"));
            if (variants.Count == 0) return anim;
            var container = Deref(Field(variants[0], "variant"));
            if (container == null) return anim;

            var anims = ArrayItems(Field(container, "animations"));
            if (anims.Count == 0) return anim;
            var a = Deref(anims[0]);
            if (a == null) return anim;

            var kind = TypeNameOf(a) ?? "";
            if (!kind.Contains("SplineCompressed"))
            {
                // 只狼的高精度关键帧都是 hkaSplineCompressedAnimation；
                // 遇到别的类型先记录，别硬解出错数据
                return anim;
            }

            anim.Duration = Float(Field(a, "duration"));
            anim.FrameDuration = Float(Field(a, "frameDuration"));
            anim.FrameCount = Int(Field(a, "numFrames"));
            anim.TrackCount = Int(Field(a, "numberOfTransformTracks"));

            int numBlocks = Int(Field(a, "numBlocks"));
            int maxPerBlock = Int(Field(a, "maxFramesPerBlock"));
            var data = Bytes(Field(a, "data"));

            if (anim.TrackCount <= 0 || anim.FrameCount <= 0 || data.Length == 0) return anim;
            if (numBlocks <= 0) numBlocks = 1;
            if (maxPerBlock <= 0) maxPerBlock = 256;

            var bindings = ArrayItems(Field(container, "bindings"));
            if (bindings.Count > 0)
            {
                var b = Deref(bindings[0]);
                if (b != null)
                {
                    anim.SkeletonName = Str(Field(b, "originalSkeletonName"));
                    var map = ArrayItems(Field(b, "transformTrackToBoneIndices"));
                    if (map.Count > 0)
                    {
                        anim.TrackToBone = new int[map.Count];
                        for (int i = 0; i < map.Count; i++) anim.TrackToBone[i] = Int(map[i]);
                    }
                }
            }

            // 交给 SoulsAssetPipeline 的样条解压：量化解码 + B 样条采样都在这儿
            List<SapAnimation.NewBlendableTransform> sampled;
            try
            {
                sampled = SapAnimation.SplineCompressedAnimation.ReadSplCmpAnimBytesAndSampleToUncomp(
                    data, anim.TrackCount, numBlocks, anim.FrameCount, maxPerBlock);
            }
            catch
            {
                return anim;
            }

            if (sampled == null || sampled.Count == 0) return anim;

            anim.Frames = new List<DecodedTransform[]>(anim.FrameCount);
            for (int f = 0; f < anim.FrameCount; f++)
            {
                var frame = new DecodedTransform[anim.TrackCount];
                for (int t = 0; t < anim.TrackCount; t++)
                {
                    int idx = f * anim.TrackCount + t;
                    if (idx >= sampled.Count) break;
                    var s = sampled[idx];
                    frame[t] = new DecodedTransform
                    {
                        Translation = new Vector3(s.Translation.X, s.Translation.Y, s.Translation.Z),
                        Rotation = new Quaternion(s.Rotation.X, s.Rotation.Y, s.Rotation.Z, s.Rotation.W),
                        Scale = new Vector3(s.Scale.X, s.Scale.Y, s.Scale.Z),
                    };
                }
                anim.Frames.Add(frame);
            }

            return anim;
        }

        // ---------------------------------------------------------------- 骨架

        /// <summary>从 skeleton.hkx 读骨骼名、父子关系、参考姿势。</summary>
        public static DecodedSkeleton DecodeSkeleton(byte[] hkx, byte[] compendium)
        {
            var sk = new DecodedSkeleton();
            if (hkx == null || compendium == null) return sk;

            var root = ReadTagfile(hkx, compendium);
            if (root == null) return sk;

            var variants = ArrayItems(Field(root, "namedVariants"));
            object container = null;
            foreach (var v in variants)
            {
                var c = Deref(Field(v, "variant"));
                var tn = TypeNameOf(c);
                if (tn != null && tn.Contains("AnimationContainer")) { container = c; break; }
            }
            if (container == null && variants.Count > 0)
                container = Deref(Field(variants[0], "variant"));
            if (container == null) return sk;

            var skels = ArrayItems(Field(container, "skeletons"));
            if (skels.Count == 0) return sk;
            var s = Deref(skels[0]);
            if (s == null) return sk;

            sk.Name = Str(Field(s, "name")) ?? "";

            var bones = ArrayItems(Field(s, "bones"));
            sk.BoneNames = new string[bones.Count];
            for (int i = 0; i < bones.Count; i++)
            {
                var bd = Deref(bones[i]);
                sk.BoneNames[i] = bd == null ? ("bone" + i) : (Str(Field(bd, "name")) ?? ("bone" + i));
            }

            var parents = ArrayItems(Field(s, "parentIndices"));
            sk.Parents = new short[parents.Count];
            for (int i = 0; i < parents.Count; i++) sk.Parents[i] = (short)Int(parents[i]);

            var pose = ArrayItems(Field(s, "referencePose"));
            sk.ReferencePose = new Matrix4x4[pose.Count];
            for (int i = 0; i < pose.Count; i++)
                sk.ReferencePose[i] = ReadTransform(pose[i]);

            return sk;
        }

        /// <summary>Havok 的 12 个 float 变换 → Matrix4x4。</summary>
        static Matrix4x4 ReadTransform(object hkTransform)
        {
            var list = ArrayItems(hkTransform);
            var f = new float[12];
            for (int i = 0; i < Math.Min(12, list.Count); i++) f[i] = Float(list[i]);
            if (list.Count < 12) return Matrix4x4.Identity;

            // 布局：右侧 3x4（列主序），最后 4 个是平移
            return new Matrix4x4(
                f[0], f[1], f[2], 0f,
                f[3], f[4], f[5], 0f,
                f[6], f[7], f[8], 0f,
                f[9], f[10], f[11], 1f);
        }
    }
}
