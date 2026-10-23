using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ServerUI.Web.Bridge.Aum;

/// <summary>清单里的一个资产条目（严格字段名，未知字段忽略）。</summary>
internal sealed class AumAsset
{
    public string Kind { get; set; }
    public string Name { get; set; }
    public string Sha256 { get; set; }
    public long Size { get; set; }
    public string Inner { get; set; }
}

/// <summary>latest.json 的强类型视图。</summary>
internal sealed class AumManifest
{
    public const int MaxAssetBytes = 200 * 1024 * 1024;
    public const string ChannelStable = "stable";

    public int Schema { get; set; }
    public string Version { get; set; }
    public string Channel { get; set; }
    public List<AumAsset> Assets { get; } = new List<AumAsset>();
    public string MinSupportedVersion { get; set; }
    public string NotesUrl { get; set; }

    private static readonly Regex Sha256Re = new Regex("^[0-9a-f]{64}$", RegexOptions.Compiled);
    /// <summary>
    /// 资产名 / 包内文件名：允许中文（发布物就叫「ServerUI-有依赖版.exe」），
    /// 但禁止路径分隔符、盘符、通配符、引号、控制字符，以及 ".."、前导点、结尾点/空格。
    /// 逐字符判断而不是大正则：可读、可测，且不会被转义问题写坏。
    /// </summary>
    private static bool IsSafeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Length > 120) return false;
        if (name.Contains("..")) return false;
        if (name[0] == '.') return false;
        if (name[name.Length - 1] == '.' || name[name.Length - 1] == ' ') return false;
        foreach (var c in name)
        {
            if (c < 0x20 || c == 0x7F) return false;
            switch (c)
            {
                case '/': case '\\': case ':': case '*': case '?':
                case '"': case '<': case '>': case '|':
                    return false;
            }
        }
        return true;
    }

    public AumAsset Find(string kind)
    {
        foreach (var a in Assets)
            if (string.Equals(a.Kind, kind, StringComparison.Ordinal)) return a;
        return null;
    }

    /// <summary>当前运行形态的首选资产种类：自包含版（exe &gt; 50MB）→ sc，否则 fd。</summary>
    public static string KindForShape(bool selfContained) => selfContained ? "sc" : "fd";

    /// <summary>
    /// 严格解析 + 校验。任一条不满足即整份拒绝（不"跳过坏项继续"）。
    /// </summary>
    /// <param name="json">latest.json 原文</param>
    /// <param name="tagVersion">该 Release 的 tag 版本（不带 v），用于一致性校验</param>
    /// <param name="selfContained">当前进程是否为自包含形态（exe &gt; 50MB）</param>
    /// <param name="error">失败原因（中文，可直接写日志）</param>
    public static AumManifest ParseStrict(string json, string tagVersion, bool selfContained, out string error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "清单为空";
            return null;
        }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (Exception ex)
        {
            error = "清单不是合法 JSON：" + ex.Message;
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "清单根节点不是对象";
                return null;
            }

            var m = new AumManifest();

            if (!TryInt(root, "schema", out var schema)) { error = "清单缺 schema 或类型错误"; return null; }
            if (schema != 1) { error = "清单 schema=" + schema + "，只支持 1"; return null; }
            m.Schema = schema;

            if (!TryString(root, "version", out var version) || !AumVersion.IsValid(version))
            {
                error = "清单 version 缺失或格式非法";
                return null;
            }
            version = AumVersion.Normalize(version);
            m.Version = version;

            if (!TryString(root, "channel", out var channel) || channel != ChannelStable)
            {
                error = "清单 channel 必须为 stable（实际 " + (channel ?? "缺失") + "）";
                return null;
            }
            m.Channel = channel;

            if (!string.IsNullOrEmpty(tagVersion))
            {
                var tv = AumVersion.Normalize(tagVersion);
                if (!AumVersion.IsValid(tv) || AumVersion.Compare(tv, version) != 0)
                {
                    error = "清单 version=" + version + " 与 tag v" + tv + " 不一致（Release 不可信）";
                    return null;
                }
            }

            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                error = "清单 assets 缺失或不是数组";
                return null;
            }

            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenKinds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var el in assets.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) { error = "assets 含非对象条目"; return null; }

                if (!TryString(el, "kind", out var kind)) { error = "资产缺 kind"; return null; }
                kind = kind.Trim().ToLowerInvariant();
                if (kind != "fd" && kind != "sc" && kind != "compat")
                { error = "资产 kind 非法：" + kind; return null; }

                if (!TryString(el, "name", out var name) || !IsSafeName(name) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                { error = "资产 name 非法：" + (name ?? "缺失"); return null; }

                if (!TryString(el, "sha256", out var sha)) { error = "资产 " + name + " 缺 sha256"; return null; }
                sha = sha.Trim().ToLowerInvariant();
                if (!Sha256Re.IsMatch(sha)) { error = "资产 " + name + " 的 sha256 不是 64 位小写十六进制"; return null; }

                if (!TryLong(el, "size", out var size)) { error = "资产 " + name + " 缺 size 或类型错误"; return null; }
                if (size < 1 || size > MaxAssetBytes)
                { error = "资产 " + name + " 的 size=" + size + " 超出 1.." + MaxAssetBytes + " 字节"; return null; }

                if (!TryString(el, "inner", out var inner) || !IsSafeName(inner) || !inner.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                { error = "资产 " + name + " 的 inner 非法：" + (inner ?? "缺失"); return null; }

                if (!seenNames.Add(name)) { error = "资产 name 重复：" + name; return null; }
                if (!seenKinds.Add(kind)) { error = "资产 kind 重复：" + kind; return null; }

                m.Assets.Add(new AumAsset { Kind = kind, Name = name, Sha256 = sha, Size = size, Inner = inner });
            }

            if (m.Assets.Count == 0) { error = "清单 assets 为空"; return null; }

            string minSupported = null;
            if (root.TryGetProperty("minSupportedVersion", out var minEl) && minEl.ValueKind == JsonValueKind.String)
                minSupported = AumVersion.Normalize(minEl.GetString());
            if (!string.IsNullOrEmpty(minSupported) && !AumVersion.IsValid(minSupported))
            {
                error = "minSupportedVersion 格式非法：" + minSupported;
                return null;
            }
            m.MinSupportedVersion = minSupported;

            if (root.TryGetProperty("notesUrl", out var notesEl) && notesEl.ValueKind == JsonValueKind.String)
                m.NotesUrl = notesEl.GetString();

            // 必须有当前形态的资产；否则若另一形态存在也先接受，交给上层提示（严格模式下直接拒绝）
            var need = KindForShape(selfContained);
            if (m.Find(need) == null)
            {
                var other = m.Find(KindForShape(!selfContained));
                error = other == null
                    ? "清单缺少当前形态（" + need + "）的资产，也没有另一形态可用"
                    : "清单缺少当前形态（" + need + "）的资产（仅有 " + other.Kind + "，其运行时形态不同步）";
                return null;
            }

            return m;
        }
    }

    private static bool TryString(JsonElement el, string name, out string value)
    {
        value = null;
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return false;
        value = v.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryInt(JsonElement el, string name, out int value)
    {
        value = 0;
        if (!el.TryGetProperty(name, out var v)) return false;
        if (v.ValueKind != JsonValueKind.Number) return false;
        return v.TryGetInt32(out value);
    }

    private static bool TryLong(JsonElement el, string name, out long value)
    {
        value = 0;
        if (!el.TryGetProperty(name, out var v)) return false;
        if (v.ValueKind != JsonValueKind.Number) return false;
        return v.TryGetInt64(out value);
    }
}
