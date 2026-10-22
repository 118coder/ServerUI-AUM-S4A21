using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ServerUI.Web.Bridge;

/// <summary>
/// 设置持久化 — JSON 存到 DataRoot\设置.json（默认 AUM管理组件\设置.json）。
/// 只保存界面/行为偏好，不保存任何业务数据。
/// </summary>
internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public sealed class Settings
    {
        /// <summary>dark | light</summary>
        public string Theme { get; set; } = "dark";
        /// <summary>日志区展开时高度(px)，默认 190（与 CSS --h-log / 无头验收一致）</summary>
        public int LogHeight { get; set; } = 190;
        /// <summary>日志区是否折叠</summary>
        public bool LogCollapsed { get; set; }
        /// <summary>去除 dgVoodooCpl 水印</summary>
        public bool Watermark { get; set; }
        /// <summary>off | dx11 | dx12（默认 off = 原生 DX9）</summary>
        public string DxMode { get; set; } = "off";
        /// <summary>跳过更新日志（更新时不拉取提交记录）</summary>
        public bool SkipLog { get; set; }
        /// <summary>镜像下载</summary>
        public bool Mirror { get; set; }
        /// <summary>更新前后清理冗余 DB</summary>
        public bool CleanRedundantDb { get; set; }

        // 窗口几何（最大化时不写回）
        public double? WinX { get; set; }
        public double? WinY { get; set; }
        public double? WinW { get; set; }
        public double? WinH { get; set; }
        public bool WinMaximized { get; set; }
    }

    public Settings Current { get; private set; } = new Settings();

    public void Load()
    {
        try
        {
            var path = Paths.SettingsFile;
            if (!File.Exists(path)) return;
            var text = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text)) return;
            var loaded = JsonSerializer.Deserialize<Settings>(text, JsonOptions);
            if (loaded != null) Current = loaded;
            Normalize();
        }
        catch
        {
            // 设置文件损坏不致命：用默认值启动
            Current = new Settings();
        }
    }

    public void Save()
    {
        try
        {
            Normalize();
            var dir = Path.GetDirectoryName(Paths.SettingsFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = Paths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOptions), new UTF8Encoding(false));
            if (File.Exists(Paths.SettingsFile)) File.Delete(Paths.SettingsFile);
            File.Move(tmp, Paths.SettingsFile);
        }
        catch { }
    }

    private void Normalize()
    {
        if (Current.Theme != "light" && Current.Theme != "dark") Current.Theme = "dark";
        if (Current.DxMode != "dx11" && Current.DxMode != "dx12") Current.DxMode = "off";
        if (Current.LogHeight < 80) Current.LogHeight = 80;
        if (Current.LogHeight > 420) Current.LogHeight = 420;
    }
}
