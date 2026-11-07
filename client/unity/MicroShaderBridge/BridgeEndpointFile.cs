// ─────────────────────────────────────────────────────────────────────────────
// 模块 11B · Unity 桥接 · 发现文件的磁盘操作
//
// 只负责「把一份文本安全地落到 <Project>/Library/MicroShader/endpoint.json」，
// 以及「标记」与「删除」。不知道文本里有什么字段 —— 那是契约 1 编码器的事。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.IO;
using System.Text;
using System.Threading;

namespace MicroShader.UnityBridge
{
    /// <summary>
    /// 发现文件的原子写 / 标记 / 删除。
    /// </summary>
    /// <remarks>
    /// <b>为什么必须原子写</b>：读端（语言服务端）会在任意时刻打开这个文件。
    /// 直接覆盖写会让它有机会读到半截 JSON；而半截 JSON 在读端只能算「不可读」，
    /// 于是桥接在「正在更新」时看起来像「坏了」。同卷 <c>*.tmp</c> + 替换把窗口降到零。
    /// <b>为什么要重试</b>：读端可能以不含 <c>FileShare.Delete</c> 的方式持有句柄
    /// （第三方工具、杀毒、编辑器自身都可能），替换会短暂撞上共享冲突。
    /// </remarks>
    internal static class BridgeEndpointFile
    {
        private const int ReplaceRetryCount = 4;

        private const int ReplaceRetryDelayMs = 50;

        /// <summary>写入发现文件（原子替换）。失败只记日志 —— 桥接不能因为写文件失败就停摆。</summary>
        public static void Write(string path, string json)
        {
            if (string.IsNullOrEmpty(path) || json == null)
            {
                return;
            }

            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var temporary = path + ".tmp";
                File.WriteAllText(temporary, json, Utf8NoBom);

                for (var attempt = 0; attempt < ReplaceRetryCount; attempt++)
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            File.Replace(temporary, path, destinationBackupFileName: null);
                        }
                        else
                        {
                            File.Move(temporary, path);
                        }

                        return;
                    }
                    catch (IOException) when (attempt < ReplaceRetryCount - 1)
                    {
                        Thread.Sleep(ReplaceRetryDelayMs);
                    }
                }
            }
            catch (Exception exception)
            {
                BridgeLog.Error("写发现文件失败：" + path + " → " + exception.Message);
            }
        }

        /// <summary>
        /// 把发现文件标成「域重载中」。
        /// </summary>
        /// <remarks>
        /// 文本变换交给 <see cref="BridgeEndpointDocument.MarkReloading"/>，本方法只管读写。
        /// 文件不存在时直接返回：那说明桥接从未成功启动过，没有可标记的东西。
        /// </remarks>
        public static void MarkReloading(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                var current = File.ReadAllText(path, Encoding.UTF8);
                var updated = BridgeEndpointDocument.MarkReloading(current);
                if (updated != null)
                {
                    File.WriteAllText(path, updated, Utf8NoBom);
                }
            }
            catch (Exception exception)
            {
                BridgeLog.Warn("标记重载状态失败：" + exception.Message);
            }
        }

        /// <summary>删除发现文件（**只在编辑器退出时**调用）。顺带清掉可能残留的 <c>.tmp</c>。</summary>
        public static void Delete(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                var temporary = path + ".tmp";
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (Exception exception)
            {
                BridgeLog.Warn("删除发现文件失败：" + exception.Message);
            }
        }

        /// <summary>无 BOM UTF-8：JSON 规范不认 BOM，部分解析器会因此在首字节报错。</summary>
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }
}
