using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ServerUI.Web.Bridge;

/// <summary>
/// 自定义扩展清单（DLL 扩展页里「已添加」的扩展）。
///
/// 为什么不能只看 GameGaurd.ini：
///   ini 的 [Plugins] 只表示「当前挂载」，取消勾选并应用后条目就被移除了。
///   如果列表直接由 ini 反推，取消勾选的那一刻行就会从界面上消失，
///   玩家再也点不回来——而开关本该只是挂载/卸载，删除才该让行消失。
///
/// 所以这里单独记一份清单：添加时写入，删除时才移除；勾选状态始终以 ini 为准。
/// 清单与 ini 的并集才是界面要显示的行（兼容玩家用启动器手动挂载的条目）。
/// </summary>
internal static class DllCustomStore
{
    internal const string FileName = "dll-custom.json";

    private sealed class Model
    {
        public List<string> Files { get; set; } = new List<string>();
    }

    private static string Path_(string dataRoot) => Path.Combine(dataRoot, FileName);

    internal static List<string> Load(string dataRoot)
    {
        var list = new List<string>();
        try
        {
            var p = Path_(dataRoot);
            if (!File.Exists(p)) return list;
            var m = JsonSerializer.Deserialize<Model>(File.ReadAllText(p, Encoding.UTF8));
            if (m?.Files == null) return list;
            foreach (var f in m.Files)
            {
                if (string.IsNullOrWhiteSpace(f)) continue;
                if (!list.Any(x => x.Equals(f, StringComparison.OrdinalIgnoreCase))) list.Add(f);
            }
        }
        catch { /* 清单损坏不致命：退回 ini 反推 */ }
        return list;
    }

    internal static void Save(string dataRoot, IEnumerable<string> files)
    {
        try
        {
            var m = new Model();
            foreach (var f in files)
            {
                if (string.IsNullOrWhiteSpace(f)) continue;
                if (!m.Files.Any(x => x.Equals(f, StringComparison.OrdinalIgnoreCase))) m.Files.Add(f);
            }
            var dir = Path.GetDirectoryName(Path_(dataRoot));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = Path_(dataRoot) + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(m, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            File.Move(tmp, Path_(dataRoot), true);
        }
        catch { /* 写失败不影响本次会话，只是下次要重新加 */ }
    }
}