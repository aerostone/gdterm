using System.Windows.Forms;

namespace Gdterm.Terminal.Rendering
{
    /// <summary>
    /// 终端渲染抽象接口——封装底层终端模拟库，对外隔离实现细节
    /// </summary>
    public interface IRenderer
    {
        /// <summary>
        /// 写入文本到终端（含 ANSI 转义序列）
        /// </summary>
        void Write(string text);

        /// <summary>
        /// 清除终端内容
        /// </summary>
        void Clear();

        /// <summary>
        /// 获取承载渲染的 WinForms 控件（用于嵌入 UI）
        /// </summary>
        Control GetControl();

        /// <summary>
        /// 获取当前终端行数
        /// </summary>
        int Rows { get; }

        /// <summary>
        /// 获取当前终端列数
        /// </summary>
        int Columns { get; }

        /// <summary>
        /// 获取当前选中的文本
        /// </summary>
        string GetSelection();

        /// <summary>
        /// 获取终端最近 N 行文本内容
        /// </summary>
        string[] GetRecentLines(int lineCount);

        /// <summary>
        /// 暂停渲染（非活动标签调用，节省 CPU）
        /// </summary>
        void Pause();

        /// <summary>
        /// 恢复渲染并刷新
        /// </summary>
        void Resume();
    }
}
