using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Gdterm.Connections;
using Gdterm.Core;
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
                Title = L10n.T("导入连接"),
                Multiselect = true,
                Filter = L10n.T("所有支持格式 (*.xsh;*.ini;*.json;*.reg;user.sessions)|*.xsh;*.ini;*.json;*.reg;user.sessions|")
                    + L10n.T("Xshell 会话 (*.xsh)|*.xsh|")
                    + L10n.T("MobaXterm 配置 (*.ini)|*.ini|")
                    + L10n.T("PuTTY / KiTTY 会话 (*.reg)|*.reg|")
                    + L10n.T("WindTerm 会话 (user.sessions)|user.sessions|")
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
                            L10n.T("未找到可导入的连接") + (bad.Count > 0 ? L10n.T("\n无法解析：") + string.Join("\n", bad.ToArray()) : ""),
                            L10n.T("导入"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
                    var skippedNote = merge.Duplicates.Count > 0 ? L10n.T("，跳过 ") + merge.Duplicates.Count + L10n.T(" 条重复") : "";
                    var badNote = bad.Count > 0 ? L10n.T("，无法解析 ") + bad.Count + L10n.T(" 个文件") : "";
                    // §9.3：成功/完成用非阻断 toast（原阻断 MessageBox 触发 R08）
                    ToastNotifier.Success(L10n.T("导入完成：新增 ") + merge.NewConnections.Count + skippedNote + badNote, 4000);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(owner, L10n.T("导入失败：") + ex.Message, L10n.T("错误"),
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
                MessageBox.Show(owner, L10n.T("没有可导出的连接"), L10n.T("导出"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (var dlg = new SaveFileDialog
            {
                Title = L10n.T("导出连接"),
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
                    // §9.3：成功用非阻断 toast
                    ToastNotifier.Success(L10n.T("已导出 ") + connections.Count + L10n.T(" 个连接"), 3000);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(owner, L10n.T("导出失败：") + ex.Message, L10n.T("错误"),
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
    }
}
