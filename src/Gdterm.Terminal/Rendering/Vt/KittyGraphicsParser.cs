using System;
using System.Collections.Generic;
using System.Text;

namespace Gdterm.Terminal.Rendering.Vt
{
    /// <summary>
    /// kitty graphics protocol（APC <c>ESC _ G&lt;key&gt;=&lt;val&gt;...;&lt;base64&gt; ESC \</c>）的最小实现：
    /// 支持传输（a=t / a=T）、放置（a=p）、删除（a=d）、查询（a=q），
    /// 支持 RGB(24)/RGBA(32)/PNG(100) 三种格式、以及 m=1 分段续传。
    /// 不支持的（共享内存/文件传输、zlib、动画帧）按协议回 <c>ENOTSUP</c>，调用方随后照旧省略不画。
    /// </summary>
    public sealed class KittyGraphicsParser
    {
        private const int MaxDimension = 4096;
        private const int MaxPayload = 8 * 1024 * 1024;

        private sealed class Pending
        {
            public int ImageId;
            public int Format;
            public int Width;
            public int Height;
            public readonly StringBuilder Data = new StringBuilder();
        }

        private Pending _pending;

        /// <summary>成功解析出一张图时回调（不画的也要回调，用于更新“有哪些图”）。</summary>
        public Action<TerminalImage> OnImage;

        /// <summary>回给主机的应答文本（调用方统一回写）。</summary>
        public Action<string> OnReply;

        /// <summary>收到删除指令时回调（imageId=0 表示全部；placementOnly=true 表示只删放置）。</summary>
        public Action<int, bool> OnDelete;

        /// <summary>是否有半截传输（m=1 未传完）。</summary>
        public bool HasPending { get { return _pending != null; } }

        public void Reset()
        {
            _pending = null;
        }

        /// <summary>处理一条 APC 载荷（已去掉 ESC _ 与 ST）。</summary>
        public void HandlePayload(byte[] payload, int offset, int count)
        {
            if (payload == null || count <= 1) return;
            if (payload[offset] != (byte)'G') return;     // 只认 kitty graphics

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            string data = ParseKeyValues(payload, offset + 1, offset + count, map);

            string action;
            if (!map.TryGetValue("a", out action) || string.IsNullOrEmpty(action))
                action = _pending != null ? "t" : "t";    // 缺省 a=t（续传时也是 t）

            // 续传片段：唯一键是 m，数据接在已有缓冲后面
            string more;
            bool hasMore = map.TryGetValue("m", out more) && more == "1";

            if (hasMore && _pending == null)
                _pending = new Pending();

            if (_pending != null)
            {
                // 分段传输：只有第一段带 key=value，后续段是纯 base64
                if (map.ContainsKey("s")) _pending.Width = GetInt(map, "s", 0);
                if (map.ContainsKey("v")) _pending.Height = GetInt(map, "v", 0);
                if (map.ContainsKey("f")) _pending.Format = GetInt(map, "f", 100);
                if (map.ContainsKey("i")) _pending.ImageId = GetInt(map, "i", 0);
                if (_pending.Data.Length + data.Length > MaxPayload) { _pending = null; Reply(map, 0, false, "E2BIG:payload"); return; }
                _pending.Data.Append(data);
                if (hasMore) return;
            }

            int imageId = GetInt(map, "i", _pending != null ? _pending.ImageId : 0);
            int format = GetInt(map, "f", _pending != null ? _pending.Format : 100);
            int pxW = GetInt(map, "s", _pending != null ? _pending.Width : 0);
            int pxH = GetInt(map, "v", _pending != null ? _pending.Height : 0);

            switch (action)
            {
                case "q":
                    _pending = null;
                    Reply(map, imageId, true, null, "OK");
                    return;

                case "d":
                    {
                        _pending = null;
                        var h = OnDelete;
                        bool placementOnly = map.ContainsKey("p") && !map.ContainsKey("i");
                        int id = GetInt(map, "i", 0);
                        if (h != null) { try { h(id, placementOnly); } catch { } }
                        Reply(map, imageId, true, null, "OK");
                        return;
                    }

                case "t":
                case "T":
                case "p":
                    break;

                default:
                    _pending = null;
                    Reply(map, imageId, false, "EINVAL:action");
                    return;
            }

            byte[] raw;
            try
            {
                raw = Convert.FromBase64String(data);
            }
            catch
            {
                _pending = null;
                Reply(map, imageId, false, "EINVAL:base64");
                return;
            }

            if (_pending != null)
            {
                raw = Convert.FromBase64String(_pending.Data.ToString());
                _pending = null;
            }

            if (format == 100)
            {
                if (action == "p") { Reply(map, imageId, false, "ENOENT:image"); return; }
                var png = new TerminalImage
                {
                    Rgba = raw,
                    Width = pxW,
                    Height = pxH,
                    Format = 100,
                    ImageId = imageId,
                    PlacementId = GetInt(map, "p", 0),
                    Columns = GetInt(map, "c", 0),
                    Rows = GetInt(map, "r", 0),
                    OffsetX = GetInt(map, "X", 0),
                    OffsetY = GetInt(map, "Y", 0),
                    CursorMove = GetInt(map, "C", 1) != 0,
                    ZIndex = GetInt(map, "z", 0)
                };
                Emit(png);
                Reply(map, imageId, true, null, "OK");
                return;
            }

            if (format != 24 && format != 32)
            {
                Reply(map, imageId, false, "ENOTSUP:format");
                return;
            }

            int bpp = format == 32 ? 4 : 3;
            if (pxW <= 0 || pxH <= 0 || pxW > MaxDimension || pxH > MaxDimension)
            {
                Reply(map, imageId, false, "EINVAL:size");
                return;
            }
            if ((long)pxW * pxH * bpp > raw.Length)
            {
                Reply(map, imageId, false, "EINVAL:data");
                return;
            }

            var rgba = new byte[pxW * pxH * 4];
            for (int p = 0, q = 0; p < rgba.Length; p += 4, q += bpp)
            {
                rgba[p + 0] = raw[q + 0];
                rgba[p + 1] = raw[q + 1];
                rgba[p + 2] = raw[q + 2];
                rgba[p + 3] = bpp == 4 ? raw[q + 3] : (byte)0xFF;
            }

            var img = new TerminalImage
            {
                Rgba = rgba,
                Width = pxW,
                Height = pxH,
                Format = format,
                ImageId = imageId,
                PlacementId = GetInt(map, "p", 0),
                Columns = GetInt(map, "c", 0),
                Rows = GetInt(map, "r", 0),
                OffsetX = GetInt(map, "X", 0),
                OffsetY = GetInt(map, "Y", 0),
                CursorMove = GetInt(map, "C", 1) != 0,
                ZIndex = GetInt(map, "z", 0)
            };
            Emit(img);
            Reply(map, imageId, true, null, "OK");
        }

        private void Emit(TerminalImage img)
        {
            var h = OnImage;
            if (h != null) { try { h(img); } catch { } }
        }

        /// <summary>按 kitty 协议格式回应答；q=1/2 时静默（不出错就不答）。</summary>
        private void Reply(Dictionary<string, string> map, int imageId, bool ok, string error, string okText = null)
        {
            var h = OnReply;
            if (h == null) return;

            string quiet;
            int q = 0;
            if (map != null && map.TryGetValue("q", out quiet)) int.TryParse(quiet, out q);
            if (ok && q >= 1) return;                 // 安静模式下成功的不用答
            if (!ok && q >= 2) return;

            var sb = new StringBuilder();
            sb.Append("\u001b_G");
            if (imageId > 0) sb.Append("i=").Append(imageId).Append(';');
            if (ok) sb.Append(okText ?? "OK");
            else
            {
                var code = string.IsNullOrEmpty(error) ? "EINVAL:unknown" : error;
                if (code.IndexOf(':') < 0) code = "EINVAL:" + code;   // 已带错误码的不再加前缀
                sb.Append(code);
            }
            sb.Append("\u001b\\");
            try { h(sb.ToString()); } catch { }
        }

        private static int GetInt(Dictionary<string, string> map, string key, int def)
        {
            string v;
            if (!map.TryGetValue(key, out v) || string.IsNullOrEmpty(v)) return def;
            int r;
            return int.TryParse(v, out r) ? r : def;
        }

        /// <summary>解析 “key=value,key=value;base64” 形式，返回数据部分。</summary>
        private static string ParseKeyValues(byte[] payload, int start, int end, Dictionary<string, string> map)
        {
            int i = start;
            while (i < end)
            {
                int keyStart = i;
                while (i < end && payload[i] != (byte)'=' && payload[i] != (byte)';' && payload[i] != (byte)',') i++;
                if (i >= end) break;
                if (payload[i] != (byte)'=') { i++; continue; }   // 无值键（少见），跳过
                string key = Encoding.ASCII.GetString(payload, keyStart, i - keyStart);
                i++;
                int valStart = i;
                while (i < end && payload[i] != (byte)',' && payload[i] != (byte)';') i++;
                string val = Encoding.ASCII.GetString(payload, valStart, i - valStart);
                if (key.Length > 0) map[key] = val;
                if (i < end && payload[i] == (byte)',') { i++; continue; }
                if (i < end && payload[i] == (byte)';') { i++; break; }
            }
            // 余下都是 base64 数据（可能为空：a=d / a=q 这类）
            var sb = new StringBuilder(end - i);
            for (; i < end; i++)
            {
                byte b = payload[i];
                if ((b >= (byte)'A' && b <= (byte)'Z') || (b >= (byte)'a' && b <= (byte)'z')
                    || (b >= (byte)'0' && b <= (byte)'9') || b == (byte)'+' || b == (byte)'/' || b == (byte)'=')
                    sb.Append((char)b);
            }
            return sb.ToString();
        }
    }
}
