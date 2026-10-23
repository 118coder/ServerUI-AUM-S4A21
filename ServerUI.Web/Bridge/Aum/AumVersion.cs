using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ServerUI.Web.Bridge.Aum;

/// <summary>
/// 版本号工具：仓库协议要求 MAJOR.MINOR[.PATCH]，逐段数字比较、缺段补 0。
/// 与旧 SelfUpdateService.CompareVersion 的宽松解析不同，这里对「清单/标签」严格，
/// 对「exe FileVersion」宽松（FileVersion 常带 .0.0 后缀）。
/// </summary>
internal static class AumVersion
{
    private static readonly Regex Strict = new Regex(@"^\d+(?:\.\d+){1,3}$", RegexOptions.Compiled);
    private static readonly Regex DigitGroup = new Regex(@"\d+", RegexOptions.Compiled);

    /// <summary>去掉首尾空白与可选的 v 前缀。</summary>
    public static string Normalize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = s.Trim();
        if (t.Length > 0 && (t[0] == 'v' || t[0] == 'V')) t = t.Substring(1).Trim();
        return t;
    }

    /// <summary>清单/标签版本：必须是至少两段的纯数字点分串。</summary>
    public static bool IsValid(string s) => Strict.IsMatch(Normalize(s));

    public static int[] Parse(string s)
    {
        var list = new List<int>();
        foreach (Match m in DigitGroup.Matches(Normalize(s)))
            if (int.TryParse(m.Value, out var n)) list.Add(n);
        return list.Count == 0 ? new[] { 0 } : list.ToArray();
    }

    public static int Compare(string a, string b)
    {
        var pa = Parse(a);
        var pb = Parse(b);
        var len = Math.Max(pa.Length, pb.Length);
        for (var i = 0; i < len; i++)
        {
            var va = i < pa.Length ? pa[i] : 0;
            var vb = i < pb.Length ? pb[i] : 0;
            if (va != vb) return va.CompareTo(vb);
        }
        return 0;
    }

    public static int Major(string s)
    {
        var p = Parse(s);
        return p.Length > 0 ? p[0] : 0;
    }

    public static int Minor(string s)
    {
        var p = Parse(s);
        return p.Length > 1 ? p[1] : 0;
    }

    /// <summary>Major.Minor 相同（exe FileVersion 与清单版本的关键比对）。</summary>
    public static bool SameMajorMinor(string a, string b)
        => Major(a) == Major(b) && Minor(a) == Minor(b);

    /// <summary>用于文件名/日志的安全版本串（只保留数字与点）。</summary>
    public static string Safe(string s)
    {
        var n = Normalize(s);
        var chars = new List<char>(n.Length);
        foreach (var c in n)
            if (char.IsDigit(c) || c == '.') chars.Add(c);
        return chars.Count == 0 ? "0" : new string(chars.ToArray());
    }
}
