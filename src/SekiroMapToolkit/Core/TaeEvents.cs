using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;

namespace SekiroMapToolkit.Core
{
    /// <summary>
    /// 一个 TAE 事件（时间轴上的一个动作/音效/特效……）。
    /// </summary>
    public class TaeEventInfo
    {
        /// <summary>事件名，来自模板；没有模板时是 "Event{Type}"。</summary>
        public string Name = "";

        /// <summary>事件类型 ID（只狼：128 = PlaySound_CenterBody 等）。</summary>
        public int Type;

        /// <summary>开始时间（秒）。</summary>
        public float Start;

        /// <summary>结束时间（秒）。</summary>
        public float End;

        /// <summary>解出来的参数；没有模板时为空列表。值一律是纯数字/纯文本，方便直接解析。</summary>
        public List<KeyValuePair<string, string>> Params = new List<KeyValuePair<string, string>>();

        /// <summary>
        /// 枚举参数的显示名（"1: (c) Character" 这种），键和 <see cref="Params"/> 一致。
        /// 不是枚举的参数不在这里。
        /// </summary>
        public Dictionary<string, string> ParamDisplay = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>按参数名取值，取不到返回 null。</summary>
        public string GetParam(string name)
        {
            if (name == null || Params == null) return null;
            for (int i = 0; i < Params.Count; i++)
                if (string.Equals(Params[i].Key, name, StringComparison.Ordinal))
                    return Params[i].Value;
            return null;
        }

        /// <summary>按参数名取枚举显示名（没有就退回数字串）。</summary>
        public string GetParamDisplay(string name)
        {
            string s;
            if (name != null && ParamDisplay != null && ParamDisplay.TryGetValue(name, out s)) return s;
            return GetParam(name);
        }

        /// <summary>按参数名取整数，取不到返回 fallback。</summary>
        public int GetInt(string name, int fallback = 0)
        {
            var s = GetParam(name);
            if (string.IsNullOrEmpty(s)) return fallback;
            int v;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            float f;
            if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) return (int)f;
            // 兜底：万一拿到的还是 "1: (c) Character" 这种
            return LeadingInt(s, fallback);
        }

        private static int LeadingInt(string s, int fallback)
        {
            int i = 0;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            int start = i;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            if (i == start) return fallback;
            int v;
            return int.TryParse(s.Substring(0, i), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0} [{1}] {2:F3}-{3:F3}", Name, Type, Start, End);
        }
    }

    /// <summary>
    /// 一个 TAE 动画（对应 .hkx 动作），带它自己的事件表。
    /// </summary>
    public class TaeAnimInfo
    {
        /// <summary>动画 ID（只狼里就是 a<n>.hkx 的 n）。</summary>
        public long Id;

        /// <summary>TAE 里记录的 hkx 文件名，可能是空串。</summary>
        public string AnimFileName = "";

        public List<TaeEventInfo> Events = new List<TaeEventInfo>();
    }

    /// <summary>
    /// 只狼 TAE 事件 + 参数读取器。
    ///
    /// 背景：SoulsFormats 的 TAE.Read 只解事件头（type/时间/参数偏移），参数要
    /// 靠模板才知道每个字段是什么类型，所以读完之后 Parameters 必然是 null。
    /// 但 TAE.Event.ApplyTemplate(bool, EventTemplate, bool) 是 public 的，
    /// 参数原始字节用 GetParameterBytes 也能拿到 —— 于是"读 → 补模板 → 重解"
    /// 这条路完全不需要反射，也不用管 TAE.ApplyTemplate 的 event bank 校验。
    /// </summary>
    public static class TaeEvents
    {
        // ---------------------------------------------------------------- 公开接口

        /// <summary>
        /// 读一个 tae 的 bytes，返回所有动画及其事件（参数已解成字符串）。
        /// 模板缺失或解析失败时不抛异常：事件照常返回，只是没有参数、名字退化成 "Event{type}"。
        /// </summary>
        /// <param name="taeBytes">.tae 的原始字节（可以是 dcx 压缩的，SoulsFormats 自己会解）。</param>
        /// <param name="templateXmlPath">模板 XML 路径；null 则自动在程序目录附近找 TAE.Template.SDT.xml。</param>
        public static List<TaeAnimInfo> Load(byte[] taeBytes, string templateXmlPath = null)
        {
            var result = new List<TaeAnimInfo>();
            if (taeBytes == null || taeBytes.Length == 0)
                return result;

            SoulsFormats.TAE tae;
            try
            {
                EnsureOodle();
                tae = SoulsFormats.TAE.Read(taeBytes);
            }
            catch
            {
                return result;
            }

            if (tae == null || tae.Animations == null)
                return result;

            bool bigEndian = tae.BigEndian;

            // 模板准备：拿不到就整段降级，绝不因为模板问题把事件也搞丢。
            SoulsFormats.TAE.Template template = null;
            try { template = LoadTemplate(templateXmlPath, tae); }
            catch { template = null; }

            foreach (var anim in tae.Animations)
            {
                var info = new TaeAnimInfo();
                info.Id = anim.ID;
                info.AnimFileName = anim.AnimFileName ?? "";

                if (anim.Events != null)
                {
                    foreach (var ev in anim.Events)
                    {
                        var evInfo = new TaeEventInfo();
                        evInfo.Type = ev.Type;
                        evInfo.Start = ev.StartTime;
                        evInfo.End = ev.EndTime;
                        evInfo.Name = "Event" + ev.Type.ToString(CultureInfo.InvariantCulture);

                        if (template != null)
                            DecodeParams(ev, template, tae.EventBank, bigEndian, evInfo);

                        info.Events.Add(evInfo);
                    }
                }

                result.Add(info);
            }

            return result;
        }

        /// <summary>
        /// 只取音频事件（名字含 Sound）并给出 SoundType / SoundID。
        /// </summary>
        public static List<(long animId, float time, int soundType, int soundId, string name)>
            SoundEvents(byte[] taeBytes, string templateXmlPath = null)
        {
            var list = new List<(long animId, float time, int soundType, int soundId, string name)>();
            var anims = Load(taeBytes, templateXmlPath);
            foreach (var anim in anims)
            {
                foreach (var ev in anim.Events)
                {
                    if (ev.Name == null) continue;
                    if (ev.Name.IndexOf("Sound", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (ev.Params == null || ev.Params.Count == 0) continue;

                    int soundType = ev.GetInt("SoundType", int.MinValue);
                    int soundId = ev.GetInt("SoundID", int.MinValue);
                    if (soundType == int.MinValue && soundId == int.MinValue) continue;

                    list.Add((anim.Id, ev.Start, soundType, soundId, ev.Name));
                }
            }
            return list;
        }

        // ---------------------------------------------------------------- 参数解码

        /// <summary>
        /// 给单个事件套模板并把参数读出来。全程 try/catch：一个事件失败不影响别人。
        /// </summary>
        private static void DecodeParams(SoulsFormats.TAE.Event ev, SoulsFormats.TAE.Template template,
            long preferredBank, bool bigEndian, TaeEventInfo evInfo)
        {
            SoulsFormats.TAE.Template.EventTemplate evTemplate;
            if (!TryFindEventTemplate(template, preferredBank, ev.Type, out evTemplate))
                return; // 模板里没这个 type，保持 "Event{type}" + 空参数

            // 名字先取到（这一步不依赖字节，不会失败）。
            try { evInfo.Name = evTemplate.Name ?? evInfo.Name; } catch { }

            try
            {
                // 1) 原始参数字节
                byte[] raw = null;
                try { raw = ev.GetParameterBytes(bigEndian); } catch { raw = null; }

                int need = evTemplate.GetAllParametersByteCount();
                if (need <= 0)
                    return;

                if (raw == null) raw = new byte[0];
                if (raw.Length < need)
                {
                    // 真实字节比模板短：补零，让流读得下去（值可能不准，但不会炸）
                    var padded = new byte[need];
                    Array.Copy(raw, padded, raw.Length);
                    raw = padded;
                }
                else if (raw.Length > need)
                {
                    // 模板短于真实字节：只取前 need 个，末尾通常是 0x10 对齐填充
                    var trimmed = new byte[need];
                    Array.Copy(raw, trimmed, need);
                    raw = trimmed;
                }

                // 2) 写回 + 应用模板（lenientOnAssert=true，断言字段只跳过不校验）
                ev.SetParameterBytes(bigEndian, raw);
                ev.ApplyTemplate(bigEndian, evTemplate, true);

                if (ev.Parameters == null)
                    return;

                // 3) 拍平成字符串。Params 一律存原始数值（"1"），枚举的说明文字另放 ParamDisplay。
                var t = ev.Parameters.Template;
                foreach (var kv in ev.Parameters.Values)
                {
                    string key = kv.Key;

                    string rawVal;
                    try
                    {
                        rawVal = Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
                    }
                    catch
                    {
                        rawVal = "";
                    }
                    evInfo.Params.Add(new KeyValuePair<string, string>(key, rawVal ?? ""));

                    try
                    {
                        var pt = t[key];
                        if (pt != null && pt.EnumEntries != null && pt.EnumEntries.Count > 0)
                        {
                            var disp = pt.ValueToString(kv.Value);
                            if (!string.IsNullOrEmpty(disp))
                                evInfo.ParamDisplay[key] = disp;
                        }
                    }
                    catch { }
                }
            }
            catch
            {
                // 这个事件的参数解不出来就放着，不影响其它事件
                evInfo.Params.Clear();
            }
        }

        /// <summary>
        /// 先在 TAE 自己的 event bank 里找，找不到再全模板扫一遍（有的 TAE 的 bank 号
        /// 在模板里没有、但事件类型在别的 bank 里能对上）。
        /// </summary>
        private static bool TryFindEventTemplate(SoulsFormats.TAE.Template template, long preferredBank,
            int type, out SoulsFormats.TAE.Template.EventTemplate evTemplate)
        {
            evTemplate = null;
            if (template == null) return false;

            // 1) 首选 bank
            SoulsFormats.TAE.Template.BankTemplate bank;
            if (template.TryGetValue(preferredBank, out bank) && bank != null)
            {
                if (bank.TryGetValue(type, out evTemplate) && evTemplate != null)
                    return true;
            }
            evTemplate = null;

            // 2) 全模板兜底
            foreach (var b in template.Values)
            {
                if (b != null && b.TryGetValue(type, out evTemplate) && evTemplate != null)
                    return true;
            }
            evTemplate = null;
            return false;
        }

        // ---------------------------------------------------------------- 模板加载

        /// <summary>
        /// 拿模板：优先用调用方给的路径，其次找程序目录附近的 TAE.Template.SDT.xml。
        /// DSAnimStudio 那种 &lt;action_template&gt; 会自动转换成 SoulsFormats 要的 &lt;event_template&gt;。
        /// </summary>
        private static SoulsFormats.TAE.Template LoadTemplate(string templateXmlPath, SoulsFormats.TAE tae)
        {
            string path = templateXmlPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                path = FindTemplateFile();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;

            var doc = new XmlDocument();
            doc.Load(path);

            if (doc.DocumentElement == null)
                return null;

            SoulsFormats.TAE.Template template;

            if (doc.DocumentElement.Name == "event_template")
            {
                // 已经是 SoulsFormats 格式，直接读
                template = SoulsFormats.TAE.Template.ReadXMLDoc(doc);
            }
            else if (doc.DocumentElement.Name == "action_template")
            {
                // DSAnimStudio 格式 → 现场转一份
                var converted = ConvertActionTemplate(doc, tae != null ? tae.EventBank : 0);
                template = SoulsFormats.TAE.Template.ReadXMLDoc(converted);
            }
            else
            {
                return null;
            }

            // 游戏对不上就换成这个 TAE 的格式，否则 ApplyTemplate 会直接抛
            if (template != null && tae != null && template.Game != tae.Format)
                template.Game = tae.Format;

            return template;
        }

        /// <summary>
        /// 在程序目录及常见位置找只狼模板。
        /// </summary>
        private static string FindTemplateFile()
        {
            var names = new[] { "TAE.Template.SDT.xml", "TAE.Template.xml" };
            var dirs = new List<string>();
            try { dirs.Add(AppContext.BaseDirectory); } catch { }
            try
            {
                var baseDir = AppContext.BaseDirectory;
                if (!string.IsNullOrEmpty(baseDir))
                {
                    var d = new DirectoryInfo(baseDir);
                    for (int i = 0; i < 3 && d != null; i++)
                    {
                        dirs.Add(d.FullName);
                        dirs.Add(Path.Combine(d.FullName, "Res"));
                        dirs.Add(Path.Combine(d.FullName, "Resources"));
                        d = d.Parent;
                    }
                }
            }
            catch { }
            try
            {
                var cfg = Config.Load();
                if (cfg != null && !string.IsNullOrEmpty(cfg.GameRoot))
                {
                    // 让用户把模板丢到游戏根目录也能找到
                    dirs.Add(cfg.GameRoot);
                    dirs.Add(Path.Combine(cfg.GameRoot, "Res"));
                }
            }
            catch { }

            foreach (var dir in dirs)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                foreach (var n in names)
                {
                    try
                    {
                        var p = Path.Combine(dir, n);
                        if (File.Exists(p)) return p;
                    }
                    catch { }
                }
            }
            return null;
        }

        /// <summary>
        /// 把 DSAnimStudio 的 &lt;action_template game="SDT"&gt;&lt;action id name&gt;
        /// 转成 SoulsFormats 的 &lt;event_template game="SDT"&gt;&lt;bank id name basedon&gt;&lt;event id name&gt;。
        /// 参数节点（s32/u8/b/aob/enum entry...）原样搬过来，因为两边认的是同一套字段。
        /// </summary>
        private static XmlDocument ConvertActionTemplate(XmlDocument source, long bankId)
        {
            var gameAttr = source.DocumentElement.GetAttribute("game");
            if (string.IsNullOrEmpty(gameAttr)) gameAttr = "SDT";

            var sb = new StringBuilder();
            var settings = new XmlWriterSettings
            {
                Indent = true,
                Encoding = new UTF8Encoding(false),
                OmitXmlDeclaration = false,
            };

            using (var sw = new StringWriter(CultureInfo.InvariantCulture))
            {
                using (var w = XmlWriter.Create(sw, settings))
                {
                    w.WriteStartDocument();
                    w.WriteStartElement("event_template");
                    w.WriteAttributeString("game", gameAttr);

                    w.WriteStartElement("bank");
                    w.WriteAttributeString("id", bankId.ToString(CultureInfo.InvariantCulture));
                    w.WriteAttributeString("name", "Sekiro");
                    w.WriteAttributeString("basedon", "-1");

                    var seen = new HashSet<int>();
                    foreach (XmlNode action in source.DocumentElement.ChildNodes)
                    {
                        if (action.NodeType != XmlNodeType.Element) continue;
                        if (action.Name != "action") continue;

                        var idText = action.Attributes != null && action.Attributes["id"] != null
                            ? action.Attributes["id"].InnerText : null;
                        int id;
                        if (!int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                            continue;
                        if (!seen.Add(id)) continue; // 重复 id 会让 BankTemplate 直接抛

                        var nameText = action.Attributes["name"] != null
                            ? action.Attributes["name"].InnerText : ("Event" + id);

                        w.WriteStartElement("event");
                        w.WriteAttributeString("id", id.ToString(CultureInfo.InvariantCulture));
                        w.WriteAttributeString("name", nameText);

                        foreach (XmlNode param in action.ChildNodes)
                        {
                            if (param.NodeType == XmlNodeType.Comment) continue;

                            if (param.NodeType == XmlNodeType.Element)
                            {
                                // 只有已知的 ParamType 名才能搬，否则 SoulsFormats 的 Enum.Parse 会炸
                                if (!IsKnownParamType(param.Name)) continue;
                                param.WriteTo(w);
                            }
                        }

                        w.WriteEndElement(); // event
                    }

                    w.WriteEndElement(); // bank
                    w.WriteEndElement(); // event_template
                    w.WriteEndDocument();
                }

                var doc = new XmlDocument();
                doc.LoadXml(sw.ToString());
                return doc;
            }
        }

        private static readonly HashSet<string> KnownParamTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "b", "u8", "x8", "s8", "u16", "x16", "s16",
            "u32", "x32", "s32", "u64", "x64", "s64",
            "f32", "f32grad", "f64", "aob",
        };

        private static bool IsKnownParamType(string name)
        {
            return name != null && KnownParamTypes.Contains(name);
        }

        // ---------------------------------------------------------------- Oodle

        private static bool _oodleChecked;

        /// <summary>
        /// SoulsFormats 解 dcx 时按裸名字 LoadLibrary("oo2core_6_win64")，
        /// 用 dotnet xx.dll 跑时应用目录不是 exe 目录，会找不到。这里提前钉一下。
        /// </summary>
        private static void EnsureOodle()
        {
            if (_oodleChecked) return;
            _oodleChecked = true;

            try
            {
                var dir = AppContext.BaseDirectory;
                if (string.IsNullOrEmpty(dir)) return;

                var names = new[] { "oo2core_6_win64.dll", "oo2core_8_win64.dll", "oo2core_9_win64.dll" };

                try
                {
                    var path = Environment.GetEnvironmentVariable("PATH") ?? "";
                    if (path.IndexOf(dir, StringComparison.OrdinalIgnoreCase) < 0)
                        Environment.SetEnvironmentVariable("PATH", dir + ";" + path);
                }
                catch { }

                foreach (var n in names)
                {
                    try
                    {
                        var full = Path.Combine(dir, n);
                        if (File.Exists(full))
                        {
                            try { NativeLibrary.Load(full); } catch { }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
