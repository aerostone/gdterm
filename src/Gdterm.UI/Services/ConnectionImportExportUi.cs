using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Gdterm.Connections;
using Gdterm.Core.Models;
using Gdterm.UI.Diagnostics;
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
                Multiselect = true,
                Filter = "所有支持格式 (*.xsh;*.ini;*.json;user.sessions)|*.xsh;*.ini;*.json;user.sessions|"
                    + "Xshell 会话 (*.xsh)|*.xsh|"
                    + "MobaXterm 配置 (*.ini)|*.ini|"
                    + "WindTerm 会话 (user.sessions)|user.sessions|"
                    + "JSON (*.json)|*.json|"
                    + "CSV (*.csv)|*.csv|"
                    + "mRemoteNG XML (*.xml)|*.xml"
            })
            {
                if (dlg.ShowDialog(owner) != DialogResult.OK) return;
                try
                {
                    var imported = new List<ConnectionConfig>();
                    var bad = new List<string>();
                    foreach (var file in dlg.FileNames)
                    {
                        // 逐文件容错：一个坏文件不该让整批导入全灭（原来一处抛异常，前面读到的也丢了）
                        try
                        {
                            var one = ConnectionImporterExporter.ImportFromFile(file) ?? new List<ConnectionConfig>();
                            if (one.Count == 0) bad.Add(file);
                            imported.AddRange(one);
                        }
                        catch (Exception fileEx)
                        {
                            bad.Add(file);
                            try { DiagLog.Error("ConnectionImport.File", fileEx); } catch (Exception) { }
                        }
                    }
                    if (imported.Count == 0)
                    {
                        MessageBox.Show(owner,
                            "未找到可导入的连接" + (bad.Count > 0 ? "\n无法解析：" + string.Join("\n", bad.ToArray()) : ""),
                            "导入", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    var existing = store.LoadAll();
                    var merge = ConnectionImporterExporter.MergeConnections(existing, imported);

                    // 先预览再写库：原来选完文件直接落库，用户看不到写什么、也拦不住
                    using (var preview = new Gdterm.UI.Forms.ImportPreviewForm(merge.NewConnections, merge.Duplicates))
                    {
                        if (preview.ShowDialog(owner) != DialogResult.OK || !preview.Confirmed)
                            return; // 用户取消：一条都不写
                    }

                    foreach (var conn in merge.NewConnections)
                        store.Add(conn);
                    if (onReloaded != null) onReloaded();
                    var skippedNote = merge.Duplicates.Count > 0 ? "\n跳过 " + merge.Duplicates.Count + " 条重复" : "";
                    var badNote = bad.Count > 0 ? "\n无法解析 " + bad.Count + " 个文件（已跳过）" : "";
                    MessageBox.Show(owner,
                        "导入完成：\n新增 " + merge.NewConnections.Count + skippedNote + badNote,
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
