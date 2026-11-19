using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SekiroMapToolkit.Core
{
    /// <summary>
    /// 角色贴图解析：把 FLVER 材质里写的贴图名（形如 c1010_hat_a.tif）
    /// 对到解包目录下真实的 DDS 文件（形如 chr_c1010_tex_c1010_hat_a.dds）。
    ///
    /// 只狼的命名规则：
    ///   &lt;root&gt;\mtd\textures\chr_&lt;角色&gt;_tex_&lt;贴图基名&gt;.dds
    /// FLVER 里给的是 &lt;贴图基名&gt;.tif，所以按基名去尾巴上匹配。
    /// </summary>
    class ChrTextureResolver
    {
        readonly string _pool;
        readonly string _chrId;
        readonly Dictionary<string, DdsImage> _cache =
            new Dictionary<string, DdsImage>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> _index =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool _built;

        /// <summary>只认漫反射贴图（_a 结尾），法线 / 遮罩对视口意义不大。</summary>
        public bool AlbedoOnly = true;

        public ChrTextureResolver(string root, string chrId, Action<string> log = null)
        {
            _chrId = chrId ?? "";
            _pool = Path.Combine(root ?? "", "mtd", "textures");
            _log = log;
        }

        readonly Action<string> _log;

        void Build()
        {
            if (_built) return;
            _built = true;
            if (!Directory.Exists(_pool)) return;

            string[] files;
            try { files = Directory.GetFiles(_pool, "*.dds"); }
            catch { return; }

            foreach (var f in files)
            {
                var name = Path.GetFileNameWithoutExtension(f);
                // 去掉 chr_<角色>_tex_ 前缀，剩下的就是贴图基名
                var key = StripPrefix(name);
                if (string.IsNullOrEmpty(key)) continue;
                if (!_index.ContainsKey(key)) _index[key] = f;
            }
            _log?.Invoke("[贴图] " + _chrId + " 索引 " + _index.Count + " 项（池 " + files.Length + " 个 DDS）");
        }

        static string StripPrefix(string name)
        {
            // chr_c1010_tex_c1010_hat_a -> c1010_hat_a
            int i = name.IndexOf("_tex_", StringComparison.OrdinalIgnoreCase);
            if (i >= 0 && i + 5 < name.Length) return name.Substring(i + 5);
            return name;
        }

        /// <summary>按 FLVER 给的贴图名找 DDS。找不到返回 null。</summary>
        public DdsImage Get(string flverTexName)
        {
            if (string.IsNullOrEmpty(flverTexName)) return null;
            Build();

            // 去掉扩展名（FLVER 里是 .tif）
            var baseName = flverTexName.Replace('\\', '/');
            var slash = baseName.LastIndexOf('/');
            if (slash >= 0) baseName = baseName.Substring(slash + 1);
            var dot = baseName.LastIndexOf('.');
            if (dot > 0) baseName = baseName.Substring(0, dot);

            if (AlbedoOnly && !IsAlbedo(baseName)) return null;

            if (_cache.TryGetValue(baseName, out var hit)) return hit;

            string path = null;
            if (_index.TryGetValue(baseName, out var p)) path = p;
            else
            {
                // 退一步：尾巴匹配（有些贴图名带额外前缀）
                foreach (var kv in _index)
                {
                    if (kv.Key.EndsWith(baseName, StringComparison.OrdinalIgnoreCase)
                        || baseName.EndsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        path = kv.Value;
                        break;
                    }
                }
            }

            var img = path != null ? DdsImage.Load(path) : null;
            _cache[baseName] = img;
            return img;
        }

        /// <summary>
        /// 只认漫反射：只狼的贴图后缀 _a = albedo、_n = normal、_m / _e / _r = 遮罩类。
        /// 但对特殊材质（如纯发光）可能没有 _a，这时放宽。
        /// </summary>
        static bool IsAlbedo(string baseName)
        {
            var n = baseName.ToLowerInvariant();
            if (n.EndsWith("_a")) return true;
            if (n.EndsWith("_n") || n.EndsWith("_m") || n.EndsWith("_e")
                || n.EndsWith("_r") || n.EndsWith("_h") || n.EndsWith("_s")) return false;
            return true;   // 没后缀的当 albedo
        }
    }
}
