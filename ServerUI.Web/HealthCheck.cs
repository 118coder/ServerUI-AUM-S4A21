using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace ServerUI.Web;

/// <summary>
/// <c>--healthcheck &lt;tokenFile&gt;</c>：被 AUM 更新流程用来验证刚落地的新 exe 能否正常启动。
/// 只读：不建窗口、不写设置、不联网、不动游戏目录；原子写一个 JSON 令牌后立即退出 0。
/// 版本取 AssemblyVersion 的 Major.Minor，与清单版本比对（替换前的最后一道防错配）。
/// </summary>
internal static class HealthCheck
{
    internal static int Run(string tokenFile)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
            var version = asm.Major + "." + asm.Minor;

            var payload = new
            {
                ok = true,
                version,
                fileVersion = ReadFileVersion(),
                exe = Process.GetCurrentProcess().MainModule?.FileName ?? "",
                pid = Process.GetCurrentProcess().Id,
                at = DateTime.Now.ToString("o"),
                machine = Environment.MachineName
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            });

            if (!string.IsNullOrWhiteSpace(tokenFile))
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(tokenFile));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                // 先写 .tmp 再改名，避免读方看到半截 JSON
                var tmp = tokenFile + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (File.Exists(tokenFile)) File.Delete(tokenFile);
                File.Move(tmp, tokenFile);
            }

            try
            {
                using var con = new FileStream("CONOUT$", FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                var bytes = Encoding.UTF8.GetBytes(json + Environment.NewLine);
                con.Write(bytes, 0, bytes.Length);
            }
            catch { }

            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(tokenFile))
                    File.WriteAllText(tokenFile + ".error", ex.ToString(), new UTF8Encoding(false));
            }
            catch { }
            return 2;
        }
    }

    private static string ReadFileVersion()
    {
        try
        {
            var p = Process.GetCurrentProcess().MainModule?.FileName;
            return string.IsNullOrEmpty(p) ? null : FileVersionInfo.GetVersionInfo(p).FileVersion;
        }
        catch { return null; }
    }
}
