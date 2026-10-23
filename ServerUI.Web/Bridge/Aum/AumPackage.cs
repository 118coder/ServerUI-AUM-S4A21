using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ServerUI.Web.Bridge.Aum;

/// <summary>
/// 发行包 ZIP 与内部 exe 的校验（任务书 §5）。
/// 这里只做「拒绝」，不做「尽力修复」：任何一条不满足即整体失败。
/// </summary>
internal static class AumPackage
{
    public const int MaxEntries = 200;
    public const long MaxUncompressedTotal = 300L * 1024 * 1024;
    public const int MaxCompressionRatio = 100;
    public const long MinExeBytes = 200 * 1024;

    internal sealed class ZipCheck
    {
        public bool Ok;
        public string Error;
        public string InnerPath;
        public long InnerBytes;
    }

    /// <summary>zip-slip / 绝对路径 / 盘符 / 反斜杠开头 的统一判定。</summary>
    public static bool IsUnsafeEntryName(string name, out string reason)
    {
        reason = null;
        if (string.IsNullOrEmpty(name)) { reason = "条目名为空"; return true; }
        if (name.IndexOf('\0') >= 0) { reason = "条目名含 NUL"; return true; }
        var n = name.Replace('\\', '/');
        if (n.StartsWith("/", StringComparison.Ordinal)) { reason = "条目为绝对路径：" + name; return true; }
        if (n.Length >= 2 && n[1] == ':' && char.IsLetter(n[0])) { reason = "条目含盘符：" + name; return true; }
        if (name.StartsWith("\\", StringComparison.Ordinal)) { reason = "条目以反斜杠开头：" + name; return true; }
        foreach (var seg in n.Split('/'))
            if (seg == "..") { reason = "条目含 ..（zip-slip）：" + name; return true; }
        return false;
    }

    /// <summary>
    /// 校验 zip：条目数、总解压大小、压缩比、条目名安全，并确认包含清单声明的 inner。
    /// 只读清单与流长度，不解压到磁盘。
    /// </summary>
    public static ZipCheck CheckZip(string zipPath, string expectedInner)
    {
        var res = new ZipCheck();
        try
        {
            using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var za = new ZipArchive(fs, ZipArchiveMode.Read, false);

            if (za.Entries.Count == 0) { res.Error = "zip 为空"; return res; }
            if (za.Entries.Count > MaxEntries)
            {
                res.Error = "zip 条目数 " + za.Entries.Count + " 超过上限 " + MaxEntries;
                return res;
            }

            long totalUncompressed = 0;
            long totalCompressed = 0;
            ZipArchiveEntry inner = null;

            foreach (var e in za.Entries)
            {
                if (IsUnsafeEntryName(e.FullName, out var why)) { res.Error = why; return res; }

                // 目录条目：长度为 0，不参与总量统计
                if (string.IsNullOrEmpty(e.Name)) continue;

                totalUncompressed += e.Length;
                totalCompressed += Math.Max(1, e.CompressedLength);

                if (e.Length > MaxUncompressedTotal)
                {
                    res.Error = "条目 " + e.FullName + " 单文件超过 300MB";
                    return res;
                }

                if (string.Equals(e.Name, expectedInner, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(e.FullName, expectedInner, StringComparison.OrdinalIgnoreCase))
                    inner = e;
            }

            if (totalUncompressed > MaxUncompressedTotal)
            {
                res.Error = "解压总量 " + totalUncompressed + " 超过 300MB";
                return res;
            }

            if (totalCompressed > 0 && totalUncompressed / totalCompressed > MaxCompressionRatio)
            {
                res.Error = "压缩比 " + (totalUncompressed / totalCompressed) + ":1 超过 " + MaxCompressionRatio + ":1，疑似压缩炸弹";
                return res;
            }

            if (inner == null)
            {
                res.Error = "zip 内缺少清单声明的 " + expectedInner;
                return res;
            }

            res.InnerBytes = inner.Length;
            res.Ok = true;
            res.InnerPath = inner.FullName;
            return res;
        }
        catch (InvalidDataException ex)
        {
            res.Error = "zip 结构损坏：" + ex.Message;
            return res;
        }
        catch (Exception ex)
        {
            res.Error = "读取 zip 失败：" + ex.Message;
            return res;
        }
    }

    /// <summary>把 inner 解压到指定目录；返回解压出的 exe 路径（失败为 null）。</summary>
    public static bool ExtractInner(string zipPath, string expectedInner, string destDir, out string exePath, out string error)
    {
        exePath = null;
        error = null;
        try
        {
            Directory.CreateDirectory(destDir);
            using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var za = new ZipArchive(fs, ZipArchiveMode.Read, false);
            foreach (var e in za.Entries)
            {
                if (!string.Equals(e.Name, expectedInner, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(e.FullName, expectedInner, StringComparison.OrdinalIgnoreCase)) continue;

                // 目标文件名固定为 inner（不采用压缩包里的路径，规避任何路径拼接风险）
                var target = Path.Combine(destDir, expectedInner);
                using var src = e.Open();
                using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                src.CopyTo(dst);
                exePath = target;
                return true;
            }
            error = "zip 内缺少 " + expectedInner;
            return false;
        }
        catch (Exception ex)
        {
            error = "解压失败：" + ex.Message;
            return false;
        }
    }

    internal sealed class PeInfo
    {
        public bool Ok;
        public string Error;
        public ushort Machine;
        public long Bytes;
    }

    /// <summary>PE 校验：MZ + e_lfanew 合法 + PE\0\0 + Machine 0x8664 + 长度 &gt; 200KB。</summary>
    public static PeInfo CheckPe64(string path)
    {
        var info = new PeInfo();
        try
        {
            if (!File.Exists(path)) { info.Error = "文件不存在：" + path; return info; }
            var len = new FileInfo(path).Length;
            info.Bytes = len;
            if (len <= MinExeBytes)
            {
                info.Error = "文件只有 " + len + " 字节（要求 > " + MinExeBytes + "）";
                return info;
            }

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);
            if (fs.Length < 0x40) { info.Error = "文件过短，不是 PE"; return info; }
            if (br.ReadUInt16() != 0x5A4D) { info.Error = "缺少 MZ 头（不是 exe）"; return info; }

            fs.Seek(0x3C, SeekOrigin.Begin);
            var eLfanew = br.ReadInt32();
            if (eLfanew < 0x40 || eLfanew > fs.Length - 24) { info.Error = "e_lfanew 非法：" + eLfanew; return info; }

            fs.Seek(eLfanew, SeekOrigin.Begin);
            var sig = br.ReadUInt32();
            if (sig != 0x00004550) { info.Error = "缺少 PE\\0\\0 签名"; return info; }

            info.Machine = br.ReadUInt16();
            if (info.Machine != 0x8664)
            {
                info.Error = "Machine=0x" + info.Machine.ToString("x4") + "，要求 x64 (0x8664)";
                return info;
            }

            info.Ok = true;
            return info;
        }
        catch (Exception ex)
        {
            info.Error = "PE 校验异常：" + ex.Message;
            return info;
        }
    }

    /// <summary>读 exe 的 FileVersion（失败返回 null）。</summary>
    public static string ReadFileVersion(string path)
    {
        try
        {
            var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            return vi.FileVersion;
        }
        catch { return null; }
    }

    /// <summary>
    /// 关键防错配：FileVersion 的 Major.Minor 必须等于清单版本。
    /// </summary>
    public static bool VersionMatches(string exePath, string manifestVersion, out string actual, out string error)
    {
        actual = ReadFileVersion(exePath);
        error = null;
        if (string.IsNullOrWhiteSpace(actual))
        {
            error = "读不到 exe 的 FileVersion";
            return false;
        }
        if (!AumVersion.SameMajorMinor(actual, manifestVersion))
        {
            error = "exe FileVersion=" + actual + " 与清单版本 " + manifestVersion + " 的 Major.Minor 不一致（资产错配/降级）";
            return false;
        }
        return true;
    }
}
