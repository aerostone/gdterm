using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Services;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// Tab 补全候选弹窗——ListBox 悬浮于终端画布底部，↑↓选、Tab/Enter 确认、Esc 关闭。
    /// </summary>
    internal sealed class TerminalCompletionPopup : ListBox
    {
        public TerminalCompletionPopup()
        {
            Visible = false;
            IntegralHeight = false;
            BorderStyle = BorderStyle.FixedSingle;
            BackColor = GdtermColorTable.Surface2;
            ForeColor = GdtermColorTable.Foreground;
            Font = FormFontPolicy.UiFont();
        }

        /// <summary>填充并定位（canvas 内底部悬浮）。</summary>
        public void ShowPopup(Control canvas, IList<string> items)
        {
            if (canvas == null || items == null || items.Count == 0) { Visible = false; return; }
            try
            {
                int show = Math.Min(items.Count, 8);
                int itemH = Math.Max(22, FormFontPolicy.RowStep(this));
                int w = 200;
                using (var g = CreateGraphics())
                {
                    foreach (var s in items)
                    {
                        if (string.IsNullOrEmpty(s)) continue;
                        int tw = (int)g.MeasureString(s, Font).Width + 32;
                        if (tw > w) w = tw;
                    }
                }
                w = Math.Min(w, Math.Max(200, canvas.ClientSize.Width - 24));
                int h = show * itemH + 4;
                int x = 12;
                int y = Math.Max(0, canvas.ClientSize.Height - h - 12);
                Bounds = new Rectangle(x, y, w, h);
                ItemHeight = itemH;
                BeginUpdate();
                Items.Clear();
                for (int i = 0; i < items.Count && i < 12; i++) Items.Add(items[i]);
                EndUpdate();
                if (Items.Count > 0) SelectedIndex = 0;
                if (Parent != canvas) canvas.Controls.Add(this);
                BringToFront();
                Visible = true;
            }
            catch { Visible = false; }
        }

        public void HidePopup()
        {
            try { Visible = false; } catch { }
        }

        public bool IsShown { get { return Visible; } }

        public string SelectedCandidate
        {
            get
            {
                try { return SelectedItem as string; }
                catch { return null; }
            }
        }

        public void MoveSelection(int delta)
        {
            try
            {
                if (Items.Count == 0) return;
                int i = SelectedIndex + delta;
                if (i < 0) i = 0;
                if (i >= Items.Count) i = Items.Count - 1;
                SelectedIndex = i;
            }
            catch { }
        }
    }
}
