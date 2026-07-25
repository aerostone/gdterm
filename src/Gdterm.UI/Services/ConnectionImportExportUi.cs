using System;
using System.Windows.Forms;
using Gdterm.Connections;
using Gdterm.UI.ImportExport;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 连接导入/导出文件对话框与 merge 流程（从 MainForm 抽出）。
    /// </summary>
    public static class ConnectionImportExportUi
    {
        public static void Import(IWin32Window owner, IConnectionStore store, Action onReloaded)
        {
            if (store == null) return;

            using (var dlg = new OpenFileDialog
            {
                Title = "导入连接",
                Filter = "所有支持格式|*.json;*.csv;*.xml|JSON|*.json|CSV|*.csv|mRemoteNG XML|*.xml"
            })
            {
                if (dlg.ShowDialog(owner) != DialogResult.OK) return;
                try
                {
                    var imported = ConnectionImporterExporter.ImportFromFile(dlg.FileName);
                    if (imported.Count == 0)
                    {
                        MessageBox.Show(owner, "未找到可导入的连接", "导入",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    var existing = store.LoadAll();
                    var merge = ConnectionImporterExporter.MergeConnections(existing, imported);
                    foreach (var conn in merge.NewConnections)
                        store.Add(conn);
                    if (onReloaded != null) onReloaded();
                    MessageBox.Show(owner,
                        "导入完成：\n新增 " + merge.NewConnections.Count + "\n跳过 " + merge.Duplicates.Count,
                        "导入成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(owner, "导入失败：" + ex.Message, "错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        public static void Export(IWin32Window owner, IConnectionStore store)
        {
            if (store == null) return;

            var connections = store.LoadAll();
            if (connections.Count == 0)
            {
                MessageBox.Show(owner, "没有可导出的连接", "导出",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (var dlg = new SaveFileDialog
            {
                Title = "导出连接",
                Filter = "JSON|*.json|CSV|*.csv",
                FileName = "gdterm-connections"
            })
            {
                if (dlg.ShowDialog(owner) != DialogResult.OK) return;
                try
                {
                    if (dlg.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                        ConnectionImporterExporter.ExportAsCsv(connections, dlg.FileName);
                    else
                        ConnectionImporterExporter.ExportAsJson(connections, dlg.FileName);
                    MessageBox.Show(owner, "已导出 " + connections.Count + " 个连接", "导出成功",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(owner, "导出失败：" + ex.Message, "错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
    }
}
