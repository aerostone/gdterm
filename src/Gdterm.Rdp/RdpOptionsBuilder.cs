using Gdterm.Core.Models;
using Gdterm.Rdp.Models;

namespace Gdterm.Rdp
{
    /// <summary>
    /// 从 ConnectionConfig.Metadata 构建 RdpOptions，避免 UI 内联解析。
    /// </summary>
    public static class RdpOptionsBuilder
    {
        public static RdpOptions FromConnection(ConnectionConfig config)
        {
            var opts = new RdpOptions();
            if (config?.Metadata == null) return opts;

            if (config.Metadata.ContainsKey("rdp_drives"))
                opts.RedirectDrives = config.Metadata["rdp_drives"] == "true";
            if (config.Metadata.ContainsKey("rdp_clipboard"))
                opts.RedirectClipboard = config.Metadata["rdp_clipboard"] != "false";
            if (config.Metadata.ContainsKey("rdp_colordepth") &&
                int.TryParse(config.Metadata["rdp_colordepth"], out var depth))
                opts.ColorDepth = depth;
            if (config.Metadata.ContainsKey("rdp_fullscreen"))
                opts.FullScreen = config.Metadata["rdp_fullscreen"] == "true";
            if (config.Metadata.ContainsKey("rdp_nla"))
                opts.EnableNLA = config.Metadata["rdp_nla"] != "false";
            if (config.Metadata.ContainsKey("rdp_force_nla"))
                opts.ForceNLA = config.Metadata["rdp_force_nla"] == "true";
            if (config.Metadata.ContainsKey("rdp_loadbalance"))
                opts.LoadBalanceInfo = config.Metadata["rdp_loadbalance"];

            return opts;
        }
    }
}
