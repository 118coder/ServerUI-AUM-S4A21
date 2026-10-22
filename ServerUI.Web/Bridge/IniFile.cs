using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ServerUI.Web.Bridge;

/// <summary>
/// GameGaurd.ini 的读写。
/// 编码探测顺序（对齐旧 MainForm.DllExt.ReadIniText）：UTF-8 BOM → 严格 UTF-8 → GBK(936)。
/// 写回一律 UTF-8 无 BOM（旧 WriteIniText 行为），所以文件只会出现这一种编码。
/// 本类只负责文本与 [Plugins] 段的解析，不碰任何界面逻辑。
/// </summary>
internal static class IniFile
{
    private static Encoding Gbk()
    {
        try { return Encoding.GetEncoding(936); }
        catch { return new UTF8Encoding(false); }
    }

    /// <summary>按探测结果读取 ini 文本；文件不存在返回空字符串。</summary>
    public static string ReadText(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "";
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0) return "";

        // 1) UTF-8 BOM
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        // 2) 严格 UTF-8（非法字节会抛异常）
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // 3) 回落 GBK
            return Gbk().GetString(bytes);
        }
    }

    /// <summary>UTF-8 无 BOM 写回。目录不存在时自动创建。</summary>
    public static void WriteText(string path, string text)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, text ?? "", new UTF8Encoding(false));
    }

    public static List<string> ReadLines(string path)
        => new List<string>(ReadText(path).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'));

    public static void WriteLines(string path, IEnumerable<string> lines)
        => WriteText(path, string.Join("\r\n", lines));

    /// <summary>
    /// 解析 [Plugins] 段里所有 "Plugin*=value" 行，按出现顺序返回 (key, value)。
    /// key 保留等号前原文（去空白），便于原样回写。
    /// </summary>
    public static List<KeyValuePair<string, string>> ReadPluginEntries(string path)
    {
        var result = new List<KeyValuePair<string, string>>();
        var inSection = false;
        foreach (var raw in ReadLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                inSection = string.Equals(line, "[Plugins]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection) continue;
            if (line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal) || line.StartsWith("#", StringComparison.Ordinal))
                continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line.Substring(0, eq).Trim();
            var val = line.Substring(eq + 1).Trim();
            if (key.StartsWith("Plugin", StringComparison.OrdinalIgnoreCase))
                result.Add(new KeyValuePair<string, string>(key, val));
        }
        return result;
    }
}
