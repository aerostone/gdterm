using System;
using System.Collections.Generic;

namespace Gdterm.UI
{
    /// <summary>
    /// 轻量国际化（zh/en）。设计取舍：
    ///  - 键 = 中文源文本本身（自文档化，免去 key 分配与查表两处维护；
    ///    代价是中文文案改动会使译文键失效——回退中文，不会空白，可接受）。
    ///  - 未翻译/查不到的键回退中文（fail-soft：en 用户看到中文好过看到 key/空白）。
    ///  - v1 语言切换重启生效（构造时读 T()；不做运行期整树重排）。
    /// 使用：Text = L10n.T("外观设置"); 格式化：L10n.Tf("共 {0} 条", n)
    /// （en 词典里的模板必须保留同序 {n} 占位符）。
    /// </summary>
    public static class L10n
    {
        public const string Zh = "zh";
        public const string En = "en";

        /// <summary>当前语言（"zh" 默认；"en" 英文）。启动时由 Program 从 appearance.ini 加载。</summary>
        public static string Language { get; private set; } = Zh;

        public static bool IsEnglish { get { return Language == En; } }

        /// <summary>加载语言（非法值回 zh）。LanguageChanged 供需要即时响应的组件订阅（v1 主要重启生效）。</summary>
        public static void Load(string lang)
        {
            Language = string.Equals(lang, En, StringComparison.OrdinalIgnoreCase) ? En : Zh;
            try { if (LanguageChanged != null) LanguageChanged(null, EventArgs.Empty); } catch { }
        }

        public static event EventHandler LanguageChanged;

        /// <summary>取译文；未翻译回退中文。null/空原样返回。</summary>
        public static string T(string zh)
        {
            if (string.IsNullOrEmpty(zh) || !IsEnglish) return zh;
            string en;
            return Strings.En.TryGetValue(zh, out en) && !string.IsNullOrEmpty(en) ? en : zh;
        }

        /// <summary>格式化译文：模板先查表（en 模板须保留同序占位符），再 Format。</summary>
        public static string Tf(string zhFormat, params object[] args)
        {
            try { return string.Format(T(zhFormat), args ?? new object[0]); }
            catch { return zhFormat; }   // 占位符对不上时宁可回退中文，不炸 UI
        }
    }

    /// <summary>en 词典：键=中文源文本，值=英文。分批迁移时随批次补充（见各提交）。</summary>
    public static class Strings
    {
        public static readonly Dictionary<string, string> En =
            new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // —— 主菜单（批次1：MainFormMenuBuilder，59 项全量）——
            ["文件(&F)"] = "File(&F)",
            ["新建连接(&N)..."] = "New Connection(&N)...",
            ["快速跳转连接 Ctrl+Shift+K"] = "Quick Jump Ctrl+Shift+K",
            ["本地终端(&L)"] = "Local Terminal(&L)",
            ["SFTP 浏览器(&S)"] = "SFTP Browser(&S)",
            ["导入连接(&I)..."] = "Import Connections(&I)...",
            ["导出连接(&E)..."] = "Export Connections(&E)...",
            ["退出(&X)"] = "Exit(&X)",
            ["连接(&C)"] = "Connection(&C)",
            ["重连当前标签 Ctrl+Shift+R"] = "Reconnect Current Tab Ctrl+Shift+R",
            ["关闭当前标签 Ctrl+Shift+W"] = "Close Current Tab Ctrl+Shift+W",
            ["书签 / 最近连接(&B)"] = "Bookmarks / Recent(&B)",
            ["视图(&V)"] = "View(&V)",
            ["标准视图(&S)"] = "Standard View(&S)",
            ["专注模式(&F)"] = "Focus Mode(&F)",
            ["紧凑模式(&C)"] = "Compact Mode(&C)",
            ["切换连接面板(&T)"] = "Toggle Connection Panel(&T)",
            ["自动隐藏连接面板(&P)"] = "Auto-hide Connection Panel(&P)",
            ["水平分割"] = "Split Horizontal",
            ["垂直分割"] = "Split Vertical",
            ["快捷命令栏"] = "Quick Command Bar",
            ["tmux 快捷面板"] = "tmux Quick Panel",
            ["终端(&E)"] = "Terminal(&E)",
            ["查找 Ctrl+Shift+F"] = "Find Ctrl+Shift+F",
            ["片段搜索 Ctrl+Shift+P"] = "Snippet Search Ctrl+Shift+P",
            ["高亮规则"] = "Highlight Rules",
            ["登录脚本"] = "Login Scripts",
            ["多通道广播"] = "Multi-Channel Broadcast",
            ["批量命令"] = "Batch Commands",
            ["宏录制 Ctrl+Shift+H"] = "Macro Record Ctrl+Shift+H",
            ["命令历史"] = "Command History",
            ["AI 助手 Ctrl+Shift+G"] = "AI Assistant Ctrl+Shift+G",
            ["命令模板库"] = "Command Template Library",
            ["健康监控"] = "Health Monitor",
            ["主机资源（远端 CPU/内存）"] = "Host Resources (remote CPU/mem)",
            ["端口转发"] = "Port Forwarding",
            ["工具(&T)"] = "Tools(&T)",
            ["运维工具箱"] = "Ops Toolbox",
            ["敏感信息扫描"] = "Secret Scan",
            ["扫描中心（插件）"] = "Scan Center (plugins)",
            ["传输中心"] = "Transfer Center",
            ["通知中心"] = "Notification Center",
            ["密码库管理(&K)"] = "Password Vault(&K)",
            ["密码健康报告(&H)"] = "Password Health Report(&H)",
            ["密码生成器(&G)"] = "Password Generator(&G)",
            ["SSH 密钥管理"] = "SSH Key Manager",
            ["修改主密码(&M)..."] = "Change Master Password(&M)...",
            ["OTP 动态码管理(&T)…"] = "OTP Manager(&T)…",
            ["外观设置(&R)..."] = "Appearance(&R)...",
            ["AI 助手设置(&I)"] = "AI Assistant Settings(&I)",
            ["危险命令规则(&D)"] = "Dangerous Command Rules(&D)",
            ["快捷键绑定"] = "Shortcut Bindings",
            ["调试模式(&D)"] = "Debug Mode(&D)",
            ["帮助(&H)"] = "Help(&H)",
            ["快捷键列表"] = "Shortcut List",
            ["检查更新…"] = "Check for Updates…",
            ["打开日志文件夹"] = "Open Log Folder",
            ["导出诊断包…"] = "Export Diagnostic Package…",
            ["关于 gdterm"] = "About gdterm",
            // —— 外观设置（批次1：AppearanceSettingsForm 试点）——
            ["外观设置"] = "Appearance",
            ["终端字体"] = "Terminal Font",
            ["字号 (pt)"] = "Size (pt)",
            ["配色方案"] = "Color Scheme",
            ["中日韩字体"] = "CJK Supplement Font",
            ["界面主题"] = "UI Theme",
            ["界面字体"] = "UI Font",
            ["不透明度 %"] = "Opacity %",
            ["启用 DPI 感知（需重启，减轻菜单模糊）"] = "Enable DPI awareness (restart required; fixes blurry menus)",
            ["AaBbCc 0123 预览 Preview"] = "AaBbCc 0123 预览 Preview",
            ["字体异常可恢复默认"] = "If a font is unavailable it falls back to the system default",
            ["保存"] = "Save",
            ["恢复默认"] = "Reset",
            ["取消"] = "Cancel",
            ["终端字号需在 6–48 之间（当前 {0}）"] = "Terminal font size must be 6-48 (current {0})",
            ["界面字号需在 6–36 之间（当前 {0}）"] = "UI font size must be 6-36 (current {0})",
            ["窗口不透明度需在 30–100 之间"] = "Window opacity must be 30-100",
            [" 在本机未安装，将回退到系统默认字体"] = " not installed on this machine; falling back to system font",
            ["外观保存失败: {0}"] = "Failed to save appearance: {0}",
            ["恢复默认外观"] = "Reset Appearance",
            ["将恢复以下默认值：\n  终端字体 Consolas 12pt / 配色 Classic\n  界面字体 {0} 9pt / 界面主题 Dark\n  不透明度 100% / DPI 感知 开启\n\n确定恢复？"]
                = "The following defaults will be restored:\n  Terminal font Consolas 12pt / scheme Classic\n  UI font {0} 9pt / theme Dark\n  Opacity 100% / DPI awareness on\n\nConfirm?",
            ["恢复默认外观失败: {0}"] = "Failed to reset appearance: {0}",
            ["界面语言"] = "Interface Language",
            ["中文"] = "Chinese",
            ["English"] = "English",
            ["重启后生效"] = "Takes effect after restart",
        };
    }
}
