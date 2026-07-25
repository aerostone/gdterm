using System;
using System.Collections.Generic;
using System.Linq;

namespace Gdterm.Tools
{
    /// <summary>
    /// 工具注册表——管理所有工具模块的注册和发现
    /// </summary>
    public class ToolRegistry : IDisposable
    {
        private readonly Dictionary<string, IToolModule> _tools = new Dictionary<string, IToolModule>(StringComparer.OrdinalIgnoreCase);

        /// <summary>注册工具模块</summary>
        public void Register(IToolModule tool)
        {
            if (tool == null) throw new ArgumentNullException("tool");
            _tools[tool.ToolId] = tool;
        }

        /// <summary>获取工具（找不到返回 null）</summary>
        public IToolModule GetTool(string toolId)
        {
            IToolModule tool;
            _tools.TryGetValue(toolId, out tool);
            return tool;
        }

        /// <summary>获取所有已注册工具</summary>
        public IReadOnlyList<IToolModule> GetAllTools()
        {
            return _tools.Values.ToList().AsReadOnly();
        }

        /// <summary>按分类获取工具</summary>
        public IReadOnlyList<IToolModule> GetByCategory(string category)
        {
            return _tools.Values.Where(t => string.Equals(t.Category, category, StringComparison.OrdinalIgnoreCase)).ToList().AsReadOnly();
        }

        /// <summary>获取所有分类</summary>
        public IReadOnlyList<string> GetCategories()
        {
            return _tools.Values.Select(t => t.Category).Distinct(StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly();
        }

        /// <summary>加载所有工具配置</summary>
        public void LoadAllConfigs()
        {
            foreach (var tool in _tools.Values)
            {
                try { tool.LoadConfig(); }
                catch { /* 单个工具配置加载失败不影响其他 */ }
            }
        }

        /// <summary>保存所有工具配置</summary>
        public void SaveAllConfigs()
        {
            foreach (var tool in _tools.Values)
            {
                try { tool.SaveConfig(); }
                catch { /* 单个工具配置保存失败不影响其他 */ }
            }
        }

        public void Dispose()
        {
            foreach (var tool in _tools.Values)
            {
                try { tool.Dispose(); }
                catch { }
            }
            _tools.Clear();
        }
    }
}
