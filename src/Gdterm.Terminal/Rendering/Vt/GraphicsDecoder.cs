using System;
using System.Collections.Generic;
using System.Text;

namespace Gdterm.Terminal.Rendering.Vt
{
    /// <summary>解码后的位图（RGBA8888，行优先，无 padding）。</summary>
    public sealed class TerminalImage
    {
        public byte[] Rgba;
        public int Width;
        public int Height;
        /// <summary>0=sixel，100=PNG，24=RGB888，32=RGBA8888。</summary>
        public int Format;
        public int ImageId;
        /// <summary>kitty 的 p= 放置 id（同一放置 id 会被覆盖）。</summary>
        public int PlacementId;
        /// <summary>kitty 的 c=/r= 显示格数；0 表示按像素尺寸自行换算。</summary>
        public int Columns;
        public int Rows;
        /// <summary>X=/Y=：单元格内的像素偏移。</summary>
        public int OffsetX;
        public int OffsetY;
        /// <summary>是否移动光标（kitty C=1，默认移动；sixel 也移动）。</summary>
        public bool CursorMove = true;
        public int ZIndex;
        /// <summary>收到该图时光标所在列（引擎填，用于定位）。</summary>
        public int CursorColumn;
        /// <summary>收到该图时光标所在视口行（0-based，随滚动漂移）。</summary>
        public int CursorRow;
        /// <summary>绝对行号（scrollback + 视口行），滚动时用它把图钉在内容上。</summary>
        public int AbsoluteRow;
    }

    /// <summary>
    /// DEC sixel 解码器（只做解码，不碰 GDI）：DCS <c>ESC P [params] q &lt;data&gt; ESC \</c>。
    /// 语法要点：
    ///   <c>#Pc</c> 选色，<c>#Pc;Pu;Px;Py;Pz</c> 定义色（Pu=2 时 Px/Py/Pz 是 RGB 0-100 百分比）；
    ///   <c>!N</c> 重复下一个字节 N 次；<c>$</c> 回到本带左边界；<c>-</c> 下移一带；<c>"</c> 光栅属性。
    /// 6 个纵向像素按 bit0..bit5（从最高位对应上方）叠在一个字符上。
    /// </summary>
    public static class SixelDecoder
    {
        public const int MaxDimension = 4096;
        private const int MaxPayload = 8 * 1024 * 1024;

        /// <summary>解码；失败返回 null（调用方按“不显示”处理，绝不影响文本流）。</summary>
        public static TerminalImage Decode(byte[] payload, int offset, int count)
        {
            if (payload == null || count <= 0 || count > MaxPayload) return null;

            var palette = new Dictionary<int, int>();   // index → ARGB
            int current = 0;
            palette[0] = unchecked((int)0xFF000000);

            int width = 0, height = 0;
            int capacity = 0;
            byte[] buf = null;                          // RGBA，动态扩容
            int x = 0, y = 0;                           // 当前绘制位置（左上角像素）
            int rasterW = 0, rasterH = 0;

            int i = offset, end = offset + count;
            while (i < end)
            {
                byte b = payload[i];

                if (b == (byte)'"')
                {
                    // "Pan;Pad;Ph;Pv
                    i++;
                    int v1, v2;
                    ReadNums(payload, ref i, end, out v1, out v2, out rasterW, out rasterH);
                    if (rasterW > MaxDimension || rasterH > MaxDimension) return null;
                    continue;
                }

                if (b == (byte)'#')
                {
                    i++;
                    var nums = new List<int>(5);
                    ReadNums(payload, ref i, end, nums);
                    if (nums.Count == 0) continue;
                    current = nums[0];
                    if (nums.Count >= 5 && nums[1] == 2)
                    {
                        int r = Clamp255(nums[2] * 255 / 100);
                        int g = Clamp255(nums[3] * 255 / 100);
                        int bl = Clamp255(nums[4] * 255 / 100);
                        palette[current] = unchecked((int)(0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)bl));
                    }
                    else if (!palette.ContainsKey(current))
                    {
                        palette[current] = unchecked((int)0xFF000000);
                    }
                    continue;
                }

                if (b == (byte)'!')
                {
                    i++;
                    int repeat = 0;
                    while (i < end && payload[i] >= (byte)'0' && payload[i] <= (byte)'9')
                    {
                        repeat = repeat * 10 + (payload[i] - '0');
                        i++;
                        if (repeat > MaxDimension * 8) return null;
                    }
                    if (i >= end) break;
                    byte ch = payload[i++];
                    {
                        int pass;
                        for (pass = 0; pass < repeat; pass++)
                        {
                            if (repeat > 100000) break;
                            if (ch == (byte)'$') { x = 0; continue; }
                            if (ch == (byte)'-') { x = 0; y += 6; continue; }
                            if (ch < 0x3F || ch > 0x7E) continue;
                            EnsureBuffer(ref buf, ref capacity, ref width, ref height, x + 1, y + 6);
                            Blit(buf, width, height, x, y, ColorOf(palette, current), ch);
                            x++;
                        }
                    }
                    continue;
                }

                if (b == (byte)'$')
                {
                    x = 0;
                    i++;
                    continue;
                }

                if (b == (byte)'-')
                {
                    x = 0;
                    y += 6;
                    if (y > MaxDimension) return null;
                    i++;
                    continue;
                }

                if (b >= 0x3F && b <= 0x7E)
                {
                    EnsureBuffer(ref buf, ref capacity, ref width, ref height, x + 1, y + 6);
                    Blit(buf, width, height, x, y, ColorOf(palette, current), b);
                    x++;
                    i++;
                    continue;
                }

                i++;   // 其他控制字符忽略
            }

            if (buf == null || width == 0 || height == 0) return null;
            if (rasterW > width) width = Math.Min(rasterW, MaxDimension);
            if (rasterH > height) height = Math.Min(rasterH, MaxDimension);

            var img = new TerminalImage
            {
                Rgba = buf,
                Width = width,
                Height = height,
                Format = 0
            };
            return img;
        }

        private static void Blit(byte[] buf, int width, int height, int x, int y, int argb, byte ch)
        {
            int bits = ch - 0x3F;
            if (bits == 0) return;
            for (int bit = 0; bit < 6; bit++)
            {
                if ((bits & (1 << bit)) == 0) continue;
                int py = y + bit;
                if (py >= height) break;
                int off = (py * width + x) * 4;
                if (off + 3 >= buf.Length) break;
                buf[off + 0] = (byte)((argb >> 16) & 0xFF);
                buf[off + 1] = (byte)((argb >> 8) & 0xFF);
                buf[off + 2] = (byte)(argb & 0xFF);
                buf[off + 3] = 0xFF;
            }
        }

        private static int ColorOf(Dictionary<int, int> palette, int index)
        {
            int argb;
            if (!palette.TryGetValue(index, out argb)) argb = unchecked((int)0xFF000000);
            return argb;
        }

        private static void EnsureBuffer(ref byte[] buf, ref int capacity, ref int width, ref int height,
                                         int needW, int needH)
        {
            if (needW > MaxDimension) needW = MaxDimension;
            if (needH > MaxDimension) needH = MaxDimension;
            int w = Math.Max(width, needW);
            int h = Math.Max(height, needH);
            if (w == width && h == height && buf != null) return;

            if (w > width || h > height || buf == null)
            {
                int newW = Math.Max(w, 1);
                int newH = Math.Max(h, 1);
                int newCap = newW * newH * 4;
                var nb = new byte[newCap];
                if (buf != null)
                {
                    for (int row = 0; row < height; row++)
                        Array.Copy(buf, row * width * 4, nb, row * newW * 4, width * 4);
                }
                buf = nb;
                capacity = newCap;
                width = newW;
                height = newH;
            }
        }

        private static void ReadNums(byte[] payload, ref int i, int end, List<int> into)
        {
            int value = 0;
            bool any = false;
            while (i < end)
            {
                byte b = payload[i];
                if (b >= (byte)'0' && b <= (byte)'9')
                {
                    value = value * 10 + (b - '0');
                    any = true;
                    i++;
                    continue;
                }
                if (b == (byte)';')
                {
                    if (!any) value = 0;
                    into.Add(value);
                    value = 0;
                    any = false;
                    i++;
                    continue;
                }
                break;
            }
            if (any) into.Add(value);
        }

        private static void ReadNums(byte[] payload, ref int i, int end, out int a, out int b, out int c, out int d)
        {
            var list = new List<int>(4);
            ReadNums(payload, ref i, end, list);
            a = list.Count > 0 ? list[0] : 0;
            b = list.Count > 1 ? list[1] : 0;
            c = list.Count > 2 ? list[2] : 0;
            d = list.Count > 3 ? list[3] : 0;
        }

        private static int Clamp255(int v)
        {
            if (v < 0) return 0;
            if (v > 255) return 255;
            return v;
        }
    }
}

namespace Gdterm.Terminal.Rendering.Vt
{
    /// <summary>删除图形事件参数（kitty a=d）。</summary>
    public sealed class TerminalImageDeleteEventArgs : EventArgs
    {
        /// <summary>要删的图 id；0 表示全部。</summary>
        public int ImageId;
        /// <summary>只删除显示（保留已传输的图像数据）。</summary>
        public bool PlacementOnly;
    }
}
