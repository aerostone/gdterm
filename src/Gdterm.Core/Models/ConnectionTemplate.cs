using System;
using System.Collections.Generic;
using Gdterm.Core.Enums;

namespace Gdterm.Core.Models
{
    /// <summary>
    /// 连接模板（快速创建连接的预设）
    /// </summary>
    public class ConnectionTemplate
    {
        /// <summary>唯一标识</summary>
        public string Id { get; set; }

        /// <summary>模板名称（如：标准 SSH、跳板机 SSH、RDP 办公网）</summary>
        public string Name { get; set; }

        /// <summary>模板描述</summary>
        public string Description { get; set; }

        /// <summary>协议类型</summary>
        public ProtocolType Protocol { get; set; }

        /// <summary>默认端口</summary>
        public int DefaultPort { get; set; }

        /// <summary>默认用户名</summary>
        public string DefaultUsername { get; set; }

        /// <summary>默认分组路径</summary>
        public string DefaultGroupPath { get; set; }

        /// <summary>是否需要隧道</summary>
        public bool RequiresTunnel { get; set; }

        /// <summary>隧道配置模板</summary>
        public TunnelConfig TunnelTemplate { get; set; }

        /// <summary>RDP 选项模板</summary>
        public RdpOptions RdpOptions { get; set; }

        /// <summary>SSH 高级选项</summary>
        public SshAdvancedOptions SshOptions { get; set; }

        /// <summary>关联的 OS 类型</summary>
        public string OsType { get; set; }

        /// <summary>图标标识</summary>
        public string Icon { get; set; }

        /// <summary>是否系统内置（不可删除）</summary>
        public bool IsBuiltIn { get; set; }
    }

    /// <summary>
    /// SSH 高级连接选项
    /// </summary>
    public class SshAdvancedOptions
    {
        /// <summary>字符编码（默认 UTF-8）</summary>
        public string Encoding { get; set; }

        /// <summary>终端类型（默认 xterm-256color）</summary>
        public string TerminalType { get; set; }

        /// <summary>保持连接间隔秒数（0=不发送）</summary>
        public int KeepAliveIntervalSeconds { get; set; }

        /// <summary>连接超时秒数</summary>
        public int ConnectTimeoutSeconds { get; set; }

        /// <summary>自动接受主机密钥</summary>
        public bool AutoAcceptHostKey { get; set; }
    }

    /// <summary>
    /// 模板分类
    /// </summary>
    public class TemplateCategory
    {
        public string Name { get; set; }
        public List<ConnectionTemplate> Templates { get; set; }
    }
}
