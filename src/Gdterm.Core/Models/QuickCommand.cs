using System;

namespace Gdterm.Core.Models
{
    /// <summary>
    /// 快捷命令条目
    /// </summary>
    public class QuickCommand
    {
        /// <summary>唯一标识</summary>
        public string Id { get; set; }

        /// <summary>命令显示名称</summary>
        public string Name { get; set; }

        /// <summary>要执行的命令</summary>
        public string Command { get; set; }

        /// <summary>所属分组（如：网络、磁盘、进程等）</summary>
        public string Group { get; set; }

        /// <summary>排序顺序</summary>
        public int SortOrder { get; set; }

        /// <summary>说明备注</summary>
        public string Description { get; set; }

        /// <summary>是否需要 root 权限</summary>
        public bool RequiresRoot { get; set; }

        /// <summary>执行前命令（如 sudo -i）</summary>
        public string PreCommand { get; set; }

        /// <summary>执行后命令（如 cleanup）</summary>
        public string PostCommand { get; set; }

        /// <summary>关联的 OS 类型（null=全部）</summary>
        public string OsType { get; set; }

        /// <summary>快捷键</summary>
        public string Shortcut { get; set; }
    }
}
