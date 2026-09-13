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

        private TerminalControl _recordSource;
        private MacroRecorder _pendingMacro; // 刚录完未保存的
        private System.Windows.Forms.Timer _tickTimer;

        public MacroPanel(string macroDir, Func<TerminalControl> getActiveTerminal)
        {
            _macroDir = macroDir ?? throw new ArgumentNullException("macroDir");
            _getActiveTerminal = getActiveTerminal;
            try { Directory.CreateDirectory(_macroDir); } catch { }
            InitializeComponent();
            RefreshMacroList();
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

            _btnRecord = MkBtn(this, "● 录制", "开始录制当前活动终端的后续全部按键");
            _btnStop = MkBtn(this, "■ 停止", "停止录制，未保存的宏保留在内存");
            _btnReplay = MkBtn(this, "▶ 回放", "把选中宏回放到当前活动终端（直接发送，不过危险命令确认）");
            _speedBox = new AntdUI.Select
            {
                Width = DpiScale.V(this, 96),
                AutoSize = true,
                MinimumSize = new Size(0, Math.Max(DpiScale.V(this, 38), FormFontPolicy.RowStep(this)))
            };
            _speedBox.Items.AddRange(new object[] { "1x", "2x", "4x" });
            _speedBox.SelectedIndex = 0;
            _speedBox.ToolTipText2("回放速度");
            _btnSave = MkBtn(this, "保存", "把刚录完的宏存为文件");
            _btnLoad = MkBtn(this, "载入", "从文件载入宏");
            _btnDelete = MkBtn(this, "删除", "删除选中的宏文件");

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
                BackColor = GdtermColorTable.Background,
                ForeColor = GdtermColorTable.Foreground
            };
            _macroList.Columns.Add("宏", 180);
            _macroList.Columns.Add("步数", 60);
            _macroList.Columns.Add("时长", 80);
            _macroList.Columns.Add("来源", 140);
            _macroList.DoubleClick += (s, e) => ReplaySelected();

            // ── 底部状态（含 Muted 危险提示）──
            _statusLabel = new AntdUI.Label
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                Text = "回放直接发送，不经过危险命令确认；请先在测试机验证。",
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(3, 0, 0, 0),
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Muted
            };

            Controls.Add(_macroList);
            Controls.Add(toolPanel);
            Controls.Add(_statusLabel);

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
                MessageBox.Show(FindForm(), "请先打开终端标签。", "宏录制",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var sess = tc.Session;
            if (sess == null || !sess.IsConnected)
            {
                MessageBox.Show(FindForm(), "当前终端未连接。", "宏录制",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                tc.StartMacroRecording();
                _recordSource = tc;
                _pendingMacro = null;
                StartTick();
                SetStatus("正在录制… 录制源锁定为当前标签页；切换标签不影响录制。");
                ToastNotifier.Info("宏录制已开始");
            }
            catch (Exception ex)
            {
                MessageBox.Show(FindForm(), "开始录制失败：\n" + ex.Message, "宏录制",
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
                    SetStatus("本次录制为空（未捕获到按键）。");
                    ToastNotifier.Warning("录制为空，未捕获到按键");
                }
                else
                {
                    SetStatus("录制完成：" + _pendingMacro.StepCount + " 步 / "
                        + _pendingMacro.Duration.TotalSeconds.ToString("0.0") + "s。点「保存」存为文件。");
                    ToastNotifier.Success("录制完成：" + _pendingMacro.StepCount + " 步");
                }
                _recordSource = null;
            }
            catch (Exception ex)
            {
                MessageBox.Show(FindForm(), "停止录制失败：\n" + ex.Message, "宏录制",
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
                        SetStatus("正在录制… " + _recordSource.MacroStepCount + " 步 / "
                            + _recordSource.MacroDuration.TotalSeconds.ToString("0.0") + "s");
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
                MessageBox.Show(FindForm(), "请先打开终端标签。", "宏回放",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var sess = tc.Session;
            if (sess == null || !sess.IsConnected)
            {
                MessageBox.Show(FindForm(), "当前终端未连接。", "宏回放",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            double speed = Speed();
            SetStatus("正在回放 " + Path.GetFileNameWithoutExtension(path) + "（" + speed + "x）…");
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
                    SetStatus("回放失败：" + err.Message);
                    MessageBox.Show(FindForm(), "回放失败：\n" + err.Message, "宏回放",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else
                {
                    SetStatus("回放完成。");
                    ToastNotifier.Success("宏回放完成");
                }
                UpdateButtons();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // ── 文件管理 ──

        private void RefreshMacroList()
        {
            if (InvokeRequired) { Invoke(new Action(RefreshMacroList)); return; }
            _macroList.Items.Clear();
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
                catch { steps = "损坏"; }
                var item = new ListViewItem(name);
                item.SubItems.Add(steps);
                item.SubItems.Add(dur);
                item.SubItems.Add(new FileInfo(f).LastWriteTime.ToString("yyyy-MM-dd HH:mm"));
                item.Tag = f;
                _macroList.Items.Add(item);
            }
            UpdateButtons();
        }

        private void SavePending()
        {
            if (_pendingMacro == null || _pendingMacro.StepCount == 0) return;
            using (var dlg = new Gdterm.UI.Forms.TextInputForm("保存宏", "宏名称（存于 data/macros/）：", "macro-" + DateTime.Now.ToString("yyyyMMdd-HHmm")))
            {
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                var name = (dlg.InputText ?? "").Trim();
                if (name.Length == 0) return;
                foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c.ToString(), "_");
                var path = Path.Combine(_macroDir, name + ".json");
                if (File.Exists(path))
                {
                    if (MessageBox.Show(FindForm(), "已存在同名宏，覆盖？", "保存宏",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                }
                try
                {
                    _pendingMacro.SaveToFile(path);
                    _pendingMacro = null;
                    RefreshMacroList();
                    SetStatus("已保存：" + name);
                    ToastNotifier.Success("宏已保存");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(FindForm(), "保存失败：\n" + ex.Message, "保存宏",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            UpdateButtons();
        }

        private void LoadFromFile()
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "载入宏文件",
                Filter = "宏文件 (*.json)|*.json",
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
                    SetStatus("已载入：" + name);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(FindForm(), "载入失败：\n" + ex.Message, "载入宏",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            UpdateButtons();
        }

        private void DeleteSelected()
        {
            if (_macroList.SelectedItems.Count == 0) return;
            var path = _macroList.SelectedItems[0].Tag as string;
            if (string.IsNullOrEmpty(path)) return;
            if (MessageBox.Show(FindForm(), "删除宏「" + Path.GetFileNameWithoutExtension(path) + "」？", "删除宏",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try
            {
                File.Delete(path);
                RefreshMacroList();
                SetStatus("已删除。");
            }
            catch (Exception ex)
            {
                MessageBox.Show(FindForm(), "删除失败：\n" + ex.Message, "删除宏",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            UpdateButtons();
        }
    }
}
