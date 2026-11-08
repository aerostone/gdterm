using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Gdterm.Terminal;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Services;
using Gdterm.Core;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 宏录制面板——开始/停止录制 + 宏文件管理（回放/变速/保存/加载/删除）+ 危险提示。
    /// 录制源是启动录制时的标签页；回放目标是当前活动终端（录一次到处跑）。
    /// </summary>
    public class MacroPanel : UserControl
    {
        private readonly string _macroDir;
        private readonly Func<TerminalControl> _getActiveTerminal;

        private AntdUI.Button _btnRecord;
        private AntdUI.Button _btnStop;
        private AntdUI.Button _btnReplay;
        private AntdUI.Button _btnSave;
        private AntdUI.Button _btnLoad;
        private AntdUI.Button _btnDelete;
        private AntdUI.Select _speedBox;
        private ListView _macroList;
        private AntdUI.Label _statusLabel;
        private EmptyStateView _empty;
        /// <summary>过滤/排序/批量/导出壳（宏多达几十条时靠它找，不再靠肉眼滚）</summary>
        private ListShell _shell;

        private TerminalControl _recordSource;
        private MacroRecorder _pendingMacro; // 刚录完未保存的
        private System.Windows.Forms.Timer _tickTimer;

        public MacroPanel(string macroDir, Func<TerminalControl> getActiveTerminal)
        {
            _macroDir = macroDir ?? throw new ArgumentNullException("macroDir");
            _getActiveTerminal = getActiveTerminal;
            try { Directory.CreateDirectory(_macroDir); } catch { }
            InitializeComponent();
            // F16 回收站：删除的宏（文件）可经「工具 → 回收站」恢复
            TrashStore.Default.RegisterRestorer("macro", entry =>
            {
                var mp = entry.Payload as MacroTrashPayload;
                if (mp != null)
                {
                    if (File.Exists(mp.Path))
                        throw new System.IO.IOException(L10n.T("同名文件已存在：") + mp.Path);
                    File.WriteAllText(mp.Path, mp.Content);
                    if (!IsDisposed) RefreshMacroList();
                }
            });
            RefreshMacroList();
        }

        /// <summary>回收站 payload：宏文件原件（路径 + 内容）。</summary>
        private sealed class MacroTrashPayload
        {
            public string Path { get; set; }
            public string Content { get; set; }
        }

        private static AntdUI.Button MkBtn(MacroPanel self, string text, string tip)
        {
            var b = new AntdUI.Button
            {
                Text = text,
                AutoSize = true,
                Padding = new Padding(DpiScale.V(self, 8), DpiScale.V(self, 3), DpiScale.V(self, 8), DpiScale.V(self, 3))
            };
            if (!string.IsNullOrEmpty(tip)) b.ToolTipText2(tip);
            return b;
        }

        private void InitializeComponent()
        {
            Size = DpiScale.S(this, 500, 400);
            BackColor = GdtermColorTable.Background;

            // ── 顶部工具条：录制组 + 回放组 ──
            var toolPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                BackColor = GdtermColorTable.Surface,
                Padding = new Padding(DpiScale.V(this, 3))
            };

            _btnRecord = MkBtn(this, L10n.T("● 录制"), L10n.T("开始录制当前活动终端的后续全部按键"));
            _btnStop = MkBtn(this, L10n.T("■ 停止"), L10n.T("停止录制，未保存的宏保留在内存"));
            _btnReplay = MkBtn(this, L10n.T("▶ 回放"), L10n.T("把选中宏回放到当前活动终端（直接发送，不过危险命令确认）"));
            _speedBox = new AntdUI.Select
            {
                Width = DpiScale.V(this, 96),
                AutoSize = true,
                MinimumSize = new Size(0, FormFontPolicy.FieldHeight(this))
            };
            _speedBox.Items.AddRange(new object[] { "1x", "2x", "4x" });
            _speedBox.SelectedIndex = 0;
            _speedBox.ToolTipText2(L10n.T("回放速度"));
            _btnSave = MkBtn(this, L10n.T("保存"), L10n.T("把刚录完的宏存为文件"));
            _btnLoad = MkBtn(this, L10n.T("载入"), L10n.T("从文件载入宏"));
            _btnDelete = MkBtn(this, L10n.T("删除"), L10n.T("删除选中的宏文件"));

            _btnRecord.Click += (s, e) => StartRecording();
            _btnStop.Click += (s, e) => StopRecording();
            _btnReplay.Click += (s, e) => ReplaySelected();
            _btnSave.Click += (s, e) => SavePending();
            _btnLoad.Click += (s, e) => LoadFromFile();
            _btnDelete.Click += (s, e) => DeleteSelected();

            toolPanel.Controls.AddRange(new Control[]
                { _btnRecord, _btnStop, _btnReplay, _speedBox, _btnSave, _btnLoad, _btnDelete });

            // ── 中部：宏列表 ──
            _macroList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                CheckBoxes = true,          // 批量删除靠勾选（Ctrl+A 在壳里切换全选）
                BackColor = GdtermColorTable.Background,
                ForeColor = GdtermColorTable.Foreground
            };
            _macroList.Columns.Add(L10n.T("宏"), 180);
            _macroList.Columns.Add(L10n.T("步数"), 60);
            _macroList.Columns.Add(L10n.T("时长"), 80);
            _macroList.Columns.Add(L10n.T("来源"), 140);
            _macroList.DoubleClick += (s, e) => ReplaySelected();

            // 列表壳：过滤 + 排序 + 导出（原先 0 项，宏一多就只能靠滚）
            _shell = new ListShell(_macroList, L10n.T("过滤宏名/来源…"));
            var btnExport = _shell.AddAction(L10n.T("导出"), (s, e) => _shell.ExportCsv(FindForm(), "macros"));
            btnExport.Name = "MacroExportButton";

            // ── 底部状态（含 Muted 危险提示）──
            _statusLabel = new AntdUI.Label
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                Text = L10n.T("回放直接发送，不经过危险命令确认；请先在测试机验证。"),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(3, 0, 0, 0),
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Muted
            };

            Controls.Add(_shell);
            Controls.Add(toolPanel);
            Controls.Add(_statusLabel);
            _macroList.AccessibleName = L10n.T("宏列表");
            _macroList.AccessibleDescription = L10n.T("已保存的宏：名称/步数/时长/来源");
            // 无障碍名称回填：ListView/Table 本身没有 Text，AutoName 只补有文本的控件，
            // 列表名单独设（读屏软件念"宏列表"而不是"列表"）
            try { UiA11y.AutoName(this); } catch (Exception) { }


            // 审计 C1：空态引导 —— 宏目录为空时此前只剩一行状态提示，看不出该录还是该载入
            _empty = EmptyStateView.Attach(this, L10n.T("还没有保存的宏"),
                L10n.T("点「● 录制」录下当前终端的按键序列，或「载入」从 data/macros/ 里选已有的宏文件"),
                L10n.T("开始录制"), (s2, e2) => StartRecording());

            UpdateButtons();
        }

        private TerminalControl ActiveTerminal()
        {
            try { return _getActiveTerminal != null ? _getActiveTerminal() : null; }
            catch { return null; }
        }

        private double Speed()
        {
            try
            {
                var t = _speedBox.SelectedIndex >= 0 ? _speedBox.Items[_speedBox.SelectedIndex].ToString() : "1x";
                if (t == "2x") return 2.0;
                if (t == "4x") return 4.0;
            }
            catch { }
            return 1.0;
        }

        private void SetStatus(string text)
        {
            try { if (_statusLabel != null) _statusLabel.Text = text ?? ""; } catch { }
        }

        private void UpdateButtons()
        {
            try
            {
                bool recording = _recordSource != null && _recordSource.IsMacroRecording;
                _btnRecord.Enabled = !recording;
                _btnStop.Enabled = recording;
                _btnReplay.Enabled = !recording && _macroList.SelectedItems.Count > 0;
                _btnSave.Enabled = !recording && _pendingMacro != null && _pendingMacro.StepCount > 0;
            }
            catch { }
        }

        // ── 录制 ──

        private void StartRecording()
        {
            var tc = ActiveTerminal();
            if (tc == null)
            {
                MessageBox.Show(FindForm(), L10n.T("请先打开终端标签。"), L10n.T("宏录制"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var sess = tc.Session;
            if (sess == null || !sess.IsConnected)
            {
                MessageBox.Show(FindForm(), L10n.T("当前终端未连接。"), L10n.T("宏录制"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                tc.StartMacroRecording();
                _recordSource = tc;
                _pendingMacro = null;
                StartTick();
                SetStatus(L10n.T("正在录制… 录制源锁定为当前标签页；切换标签不影响录制。"));
                ToastNotifier.Info(L10n.T("宏录制已开始"));
            }
            catch (Exception ex)
            {
                MessageBox.Show(FindForm(), L10n.T("开始录制失败：\n") + ex.Message, L10n.T("宏录制"),
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            UpdateButtons();
        }

        private void StopRecording()
        {
            try
            {
                if (_recordSource == null) return;
                _pendingMacro = _recordSource.StopMacroRecording();
                StopTick();
                if (_pendingMacro == null || _pendingMacro.StepCount == 0)
                {
                    SetStatus(L10n.T("本次录制为空（未捕获到按键）。"));
                    ToastNotifier.Warning(L10n.T("录制为空，未捕获到按键"));
                }
                else
                {
                    SetStatus(L10n.T("录制完成：") + _pendingMacro.StepCount + L10n.T(" 步 / ") +
                        _pendingMacro.Duration.TotalSeconds.ToString("0.0") + L10n.T("s。点「保存」存为文件。"));
                    ToastNotifier.Success(L10n.T("录制完成：") + _pendingMacro.StepCount + L10n.T(" 步"));
                }
                _recordSource = null;
            }
            catch (Exception ex)
            {
                MessageBox.Show(FindForm(), L10n.T("停止录制失败：\n") + ex.Message, L10n.T("宏录制"),
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            UpdateButtons();
        }

        private void StartTick()
        {
            StopTick();
            _tickTimer = new System.Windows.Forms.Timer { Interval = 500 };
            _tickTimer.Tick += (s, e) =>
            {
                try
                {
                    if (_recordSource != null && _recordSource.IsMacroRecording)
                        SetStatus(L10n.T("正在录制… ") + _recordSource.MacroStepCount + L10n.T(" 步 / ") +
                            _recordSource.MacroDuration.TotalSeconds.ToString("0.0") + L10n.T("s"));
                    else
                        StopTick();
                }
                catch { }
            };
            _tickTimer.Start();
        }

        private void StopTick()
        {
            try { if (_tickTimer != null) { _tickTimer.Stop(); _tickTimer.Dispose(); _tickTimer = null; } } catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) StopTick();
            base.Dispose(disposing);
        }

        // ── 回放 ──

        private void ReplaySelected()
        {
            if (_macroList.SelectedItems.Count == 0) return;
            var path = _macroList.SelectedItems[0].Tag as string;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            var tc = ActiveTerminal();
            if (tc == null)
            {
                MessageBox.Show(FindForm(), L10n.T("请先打开终端标签。"), L10n.T("宏回放"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var sess = tc.Session;
            if (sess == null || !sess.IsConnected)
            {
                MessageBox.Show(FindForm(), L10n.T("当前终端未连接。"), L10n.T("宏回放"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            double speed = Speed();
            SetStatus(L10n.T("正在回放 ") + Path.GetFileNameWithoutExtension(path) + L10n.T("（") + speed + L10n.T("x）…"));
            Task.Run(async () =>
            {
                try
                {
                    var rec = MacroRecorder.FromFile(path);
                    await rec.ReplayAsync(sess, speed, CancellationToken.None);
                    return (object)null;
                }
                catch (Exception ex) { return ex; }
            }).ContinueWith(t =>
            {
                var err = t.Result as Exception;
                if (err != null)
                {
                    SetStatus(L10n.T("回放失败：") + err.Message);
                    MessageBox.Show(FindForm(), L10n.T("回放失败：\n") + err.Message, L10n.T("宏回放"),
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else
                {
                    SetStatus(L10n.T("回放完成。"));
                    ToastNotifier.Success(L10n.T("宏回放完成"));
                }
                UpdateButtons();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // ── 文件管理 ──

        private void RefreshMacroList()
        {
            if (InvokeRequired) { Invoke(new Action(RefreshMacroList)); return; }
            var items = new System.Collections.Generic.List<ListViewItem>();
            // 每个宏都要读盘 + 反序列化，几十条时同步刷新会明显迟滞：套 BusyScope 给即时反馈
            using (BusyScope.Begin(this, L10n.T("正在读取宏…"), false, _btnLoad))
            {
            string[] files;
            try { files = Directory.GetFiles(_macroDir, "*.json"); }
            catch { files = new string[0]; }
            Array.Sort(files);
            foreach (var f in files)
            {
                string name = Path.GetFileNameWithoutExtension(f);
                string steps = "-", dur = "-";
                try
                {
                    var rec = MacroRecorder.FromFile(f);
                    steps = rec.StepCount.ToString();
                    dur = rec.Duration.TotalSeconds.ToString("0.0") + "s";
                }
                catch { steps = L10n.T("损坏"); }
                var item = new ListViewItem(name);
                item.SubItems.Add(steps);
                item.SubItems.Add(dur);
                item.SubItems.Add(new FileInfo(f).LastWriteTime.ToString("yyyy-MM-dd HH:mm"));
                item.Tag = f;
                items.Add(item);
            }
            _shell.SetItems(items);
            }
            if (_empty != null)
            {
                // 目录读不出来（权限/IO）与「确实没有宏」必须分开说，不能都显示成空态
                bool readFailed = false;
                try { Directory.GetFiles(_macroDir, "*.json"); } catch { readFailed = true; }
                // 用全量判空：过滤到 0 条时该亮的是壳的「没有匹配」，不是「还没有保存的宏」
                _empty.SetEmpty(_shell.AllItems.Count == 0, readFailed ? L10n.T("无法读取宏目录") : null,
                    readFailed ? L10n.T("检查 data/macros/ 是否存在且可写") : null);
            }
            UpdateButtons();
        }

        private void SavePending()
        {
            if (_pendingMacro == null || _pendingMacro.StepCount == 0) return;
            using (var dlg = new Gdterm.UI.Forms.TextInputForm(L10n.T("保存宏"), L10n.T("宏名称（存于 data/macros/）："), "macro-" + DateTime.Now.ToString("yyyyMMdd-HHmm")))
            {
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                var name = (dlg.InputText ?? "").Trim();
                if (name.Length == 0) return;
                foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c.ToString(), "_");
                var path = Path.Combine(_macroDir, name + ".json");
                if (File.Exists(path))
                {
                    if (MessageBox.Show(FindForm(), L10n.T("已存在同名宏，覆盖？"), L10n.T("保存宏"),
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                }
                try
                {
                    _pendingMacro.SaveToFile(path);
                    _pendingMacro = null;
                    RefreshMacroList();
                    SetStatus(L10n.T("已保存：") + name);
                    ToastNotifier.Success(L10n.T("宏已保存"));
                }
                catch (Exception ex)
                {
                    MessageBox.Show(FindForm(), L10n.T("保存失败：\n") + ex.Message, L10n.T("保存宏"),
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            UpdateButtons();
        }

        private void LoadFromFile()
        {
            using (var dlg = new OpenFileDialog
            {
                Title = L10n.T("载入宏文件"),
                Filter = L10n.T("宏文件 (*.json)|*.json"),
                InitialDirectory = _macroDir
            })
            {
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                try
                {
                    var name = Path.GetFileNameWithoutExtension(dlg.FileName);
                    foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c.ToString(), "_");
                    var dest = Path.Combine(_macroDir, name + ".json");
                    if (!string.Equals(dlg.FileName, dest, StringComparison.OrdinalIgnoreCase))
                        File.Copy(dlg.FileName, dest, true);
                    RefreshMacroList();
                    SetStatus(L10n.T("已载入：") + name);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(FindForm(), L10n.T("载入失败：\n") + ex.Message, L10n.T("载入宏"),
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            UpdateButtons();
        }

        private void DeleteSelected()
        {
            // 勾选的多条一起删（之前的实现只认 SelectedItems[0]，勾了 10 条也只删 1 条）
            var picked = _shell.SelectedOrChecked();
            if (picked.Count == 0) return;
            string tip = picked.Count == 1
                ? L10n.T("删除宏「") + Path.GetFileNameWithoutExtension((string)picked[0].Tag) + L10n.T("」？")
                : L10n.T("删除选中的 ") + picked.Count + L10n.T(" 个宏？");
            if (MessageBox.Show(FindForm(), tip, L10n.T("删除宏"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            int ok = 0, fail = 0;
            try
            {
                foreach (var it in picked)
                {
                    var p = it.Tag as string;
                    if (string.IsNullOrEmpty(p)) continue;
                    try
                    {
                        var name = Path.GetFileNameWithoutExtension(p);
                        var content = File.ReadAllText(p);
                        File.Delete(p); ok++;
                        TrashStore.Default.Add("macro", L10n.T("宏"), name, new MacroTrashPayload { Path = p, Content = content });
                    }
                    catch (Exception) { fail++; }
                }
                RefreshMacroList();
                SetStatus(fail == 0 ? L10n.T("已删除 ") + ok + L10n.T(" 个宏。") : L10n.T("删除完成：") + ok + L10n.T(" 成功，") + fail + L10n.T(" 失败。"));
                if (fail == 0) ToastNotifier.Success(L10n.T("已删除 ") + ok + L10n.T(" 个宏"));
                else ToastNotifier.Warning(fail + L10n.T(" 个宏删除失败（可能被占用或权限不足）"));
            }
            catch (Exception ex)
            {
                MessageBox.Show(FindForm(), L10n.T("删除失败：\n") + ex.Message, L10n.T("删除宏"),
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            UpdateButtons();
        }
    }
}
