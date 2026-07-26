using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 分屏容器——支持水平/垂直分割，每个面板可嵌套或承载终端
    /// 支持拖拽调整分割比例
    /// </summary>
    public class SplitPaneControl : UserControl
    {
        /// <summary>
        /// 从控件树递归查找第一个 TerminalControl（finding-05）。
        /// </summary>
        public static TerminalControl FindFirstTerminal(Control root)
        {
            if (root == null) return null;
            var direct = root as TerminalControl;
            if (direct != null) return direct;

            var split = root as SplitPaneControl;
            if (split != null)
            {
                var t = FindFirstTerminal(split.FirstPane) ?? FindFirstTerminal(split.SecondPane);
                if (t != null) return t;
            }

            foreach (Control child in root.Controls)
            {
                var t = FindFirstTerminal(child);
                if (t != null) return t;
            }
            return null;
        }

        /// <summary>递归收集控件树中全部 TerminalControl。</summary>
        public static void CollectTerminals(Control root, IList<TerminalControl> into)
        {
            if (root == null || into == null) return;
            var direct = root as TerminalControl;
            if (direct != null)
            {
                into.Add(direct);
                return;
            }

            var split = root as SplitPaneControl;
            if (split != null)
            {
                CollectTerminals(split.FirstPane, into);
                CollectTerminals(split.SecondPane, into);
                return;
            }

            foreach (Control child in root.Controls)
                CollectTerminals(child, into);
        }

        private SplitterControl _splitter;
        private Control _firstPane;
        private Control _secondPane;
        private SplitOrientation _orientation;
        private double _splitRatio = 0.5; // 0.0 ~ 1.0

        /// <summary>
        /// 分割方向
        /// </summary>
        public enum SplitOrientation
        {
            /// <summary>
            /// 水平分割（左右）
            /// </summary>
            Horizontal,

            /// <summary>
            /// 垂直分割（上下）
            /// </summary>
            Vertical
        }

        /// <summary>
        /// 第一个面板
        /// </summary>
        public Control FirstPane
        {
            get => _firstPane;
            set
            {
                if (_firstPane != null) Controls.Remove(_firstPane);
                _firstPane = value;
                if (_firstPane != null)
                {
                    Controls.Add(_firstPane);
                    UpdateLayout();
                }
            }
        }

        /// <summary>
        /// 第二个面板
        /// </summary>
        public Control SecondPane
        {
            get => _secondPane;
            set
            {
                if (_secondPane != null) Controls.Remove(_secondPane);
                _secondPane = value;
                if (_secondPane != null)
                {
                    Controls.Add(_secondPane);
                    UpdateLayout();
                }
            }
        }

        /// <summary>
        /// 分割方向
        /// </summary>
        public SplitOrientation Orientation
        {
            get => _orientation;
            set
            {
                _orientation = value;
                UpdateLayout();
            }
        }

        /// <summary>
        /// 分割比例（0.0 ~ 1.0）
        /// </summary>
        public double SplitRatio
        {
            get => _splitRatio;
            set
            {
                _splitRatio = Math.Max(0.1, Math.Min(0.9, value));
                UpdateLayout();
            }
        }

        public SplitPaneControl()
        {
            _splitter = new SplitterControl();
            _splitter.SplitterMoved += OnSplitterMoved;
            Controls.Add(_splitter);

            Resize += (s, e) => UpdateLayout();
        }

        /// <summary>
        /// 创建水平分割（左右）
        /// </summary>
        public static SplitPaneControl CreateHorizontal(Control left, Control right, double ratio = 0.5)
        {
            var split = new SplitPaneControl
            {
                Orientation = SplitOrientation.Horizontal,
                SplitRatio = ratio
            };
            split.FirstPane = left;
            split.SecondPane = right;
            return split;
        }

        /// <summary>
        /// 创建垂直分割（上下）
        /// </summary>
        public static SplitPaneControl CreateVertical(Control top, Control bottom, double ratio = 0.5)
        {
            var split = new SplitPaneControl
            {
                Orientation = SplitOrientation.Vertical,
                SplitRatio = ratio
            };
            split.FirstPane = top;
            split.SecondPane = bottom;
            return split;
        }

        private void OnSplitterMoved(object sender, SplitterMovedEventArgs e)
        {
            if (_orientation == SplitOrientation.Horizontal)
            {
                _splitRatio = (double)e.Position / Width;
            }
            else
            {
                _splitRatio = (double)e.Position / Height;
            }
            UpdateLayout();
        }

        private void UpdateLayout()
        {
            if (_firstPane == null || _secondPane == null) return;

            int splitterSize = 4;

            if (_orientation == SplitOrientation.Horizontal)
            {
                int firstWidth = (int)(Width * _splitRatio);
                int secondWidth = Width - firstWidth - splitterSize;

                _firstPane.Location = new Point(0, 0);
                _firstPane.Size = new Size(firstWidth, Height);

                _splitter.Location = new Point(firstWidth, 0);
                _splitter.Size = new Size(splitterSize, Height);
                _splitter.IsHorizontal = true;

                _secondPane.Location = new Point(firstWidth + splitterSize, 0);
                _secondPane.Size = new Size(secondWidth, Height);
            }
            else
            {
                int firstHeight = (int)(Height * _splitRatio);
                int secondHeight = Height - firstHeight - splitterSize;

                _firstPane.Location = new Point(0, 0);
                _firstPane.Size = new Size(Width, firstHeight);

                _splitter.Location = new Point(0, firstHeight);
                _splitter.Size = new Size(Width, splitterSize);
                _splitter.IsHorizontal = false;

                _secondPane.Location = new Point(0, firstHeight + splitterSize);
                _secondPane.Size = new Size(Width, secondHeight);
            }
        }
    }

    /// <summary>
    /// 分割条控件——支持拖拽调整分割比例
    /// </summary>
    internal class SplitterControl : Control
    {
        private bool _isDragging;
        private int _dragStartPos;

        /// <summary>
        /// 是否水平分割条（左右拖拽）
        /// </summary>
        public bool IsHorizontal { get; set; }

        /// <summary>
        /// 分割条移动事件
        /// </summary>
        public event EventHandler<SplitterMovedEventArgs> SplitterMoved;

        public SplitterControl()
        {
            Cursor = Cursors.VSplit;
            BackColor = SystemColors.ControlDark;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _isDragging = true;
                _dragStartPos = IsHorizontal ? e.X : e.Y;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_isDragging)
            {
                int currentPos = IsHorizontal ? e.X : e.Y;
                int delta = currentPos - _dragStartPos;

                var parent = Parent as SplitPaneControl;
                if (parent != null)
                {
                    int newPos;
                    if (IsHorizontal)
                    {
                        newPos = Location.X + delta;
                        newPos = Math.Max(50, Math.Min(Parent.Width - 50, newPos));
                    }
                    else
                    {
                        newPos = Location.Y + delta;
                        newPos = Math.Max(50, Math.Min(Parent.Height - 50, newPos));
                    }

                    SplitterMoved?.Invoke(this, new SplitterMovedEventArgs(newPos));
                }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _isDragging = false;
        }
    }

    /// <summary>
    /// 分割条移动事件参数
    /// </summary>
    internal class SplitterMovedEventArgs : EventArgs
    {
        public int Position { get; }

        public SplitterMovedEventArgs(int position)
        {
            Position = position;
        }
    }
}
