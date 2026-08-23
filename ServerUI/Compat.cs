/*
 * ==================================================================
 * 双框架兼容助手 (Compat.cs)
 * ==================================================================
 * 主版本 (net10.0-windows) 与 Win7 兼容版 (net48) 共享同一源码,
 * .NET Framework 4.8 缺少的部分 API 在此提供等价实现:
 *   Compat.ExePath()           ≈ Environment.ProcessPath
 *   Compat.Pid                 ≈ Environment.ProcessId
 *   Compat.GetRelativePath()   ≈ Path.GetRelativePath
 *   Compat.Sha256Hex()         ≈ Convert.ToHexString(SHA256.HashData())
 * ==================================================================
 */
using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

#if NET48
namespace System.Runtime.CompilerServices
{
    // net48 缺少 C# 9 init 访问器所需的 IsExternalInit, 提供兼容定义
    // (net10 已内置该类型, 无需定义)
    internal static class IsExternalInit { }
}
#endif

namespace ServerUI
{
    static class Compat
    {
        /// <summary>当前进程 PID (≈ Environment.ProcessId)</summary>
        public static int Pid => Process.GetCurrentProcess().Id;

        /// <summary>当前进程 EXE 完整路径 (≈ Environment.ProcessPath)</summary>
        public static string ExePath()
        {
            try { return Process.GetCurrentProcess().MainModule?.FileName ?? ""; }
            catch { return AppDomain.CurrentDomain.BaseDirectory; }
        }

        /// <summary>计算 bytes 的 SHA256 十六进制小写串 (≈ Convert.ToHexString(SHA256.HashData()))</summary>
        public static string Sha256Hex(byte[] bytes)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(bytes);
            return BitConverter.ToString(hash).Replace("-", "").ToLower();
        }

        /// <summary>计算相对路径 (≈ Path.GetRelativePath(from, to))</summary>
        public static string GetRelativePath(string from, string to)
        {
            var fromFull = Path.GetFullPath(from);
            // 驱动器根目录 (如 C:\) 不能去掉尾部斜杠, 否则前缀匹配失效
            var fromPrefix = fromFull.EndsWith("\\", StringComparison.Ordinal)
                ? fromFull
                : fromFull + "\\";
            var toFull = Path.GetFullPath(to);
            if (toFull.StartsWith(fromPrefix, StringComparison.OrdinalIgnoreCase))
                return toFull.Substring(fromPrefix.Length);
            return toFull;
        }
    }
}
