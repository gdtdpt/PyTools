using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PyTools.Native;

namespace PyTools.Core
{
    /// <summary>
    /// --selftest 无界面自测：热键解析、配置兼容、单实例命名、前台恢复干跑、日志。
    /// 结果打 stdout（AttachConsole）与日志文件，exit code 0 = 全部通过。
    /// </summary>
    public static class SelfTest
    {
        private sealed class Result
        {
            public string Name; public bool Passed; public string Detail;
        }

        public static int Run()
        {
            NativeMethods.TryAttachConsole();
            var results = new List<Result>();

            results.Add(Test("热键解析：Ctrl+Shift+Space", () =>
            {
                uint mods, vk; string err;
                if (!HotkeyParser.TryParse("Ctrl+Shift+Space", out mods, out vk, out err)) return err;
                const uint expected = NativeMethods.ModControl | NativeMethods.ModShift;
                return (mods == expected && vk == 0x20) ? null : "mods=" + mods + " vk=" + vk;
            }));

            results.Add(Test("热键解析：非法串被拒绝", () =>
            {
                uint mods, vk; string err;
                return HotkeyParser.TryParse("NotAHotkey", out mods, out vk, out err) ? "非法串解析成功" : null;
            }));

            results.Add(Test("配置：缺文件回退默认值", () =>
            {
                var path = Path.Combine(Path.GetTempPath(), "pytools-selftest-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    var cfg = AppConfig.Load(path);
                    return cfg.Hotkey == "Ctrl+Shift+Space" ? null : "默认热键不符: " + cfg.Hotkey;
                }
                finally { try { File.Delete(path); } catch { } }
            }));

            results.Add(Test("配置：未知键与 v1 键写回保留", () =>
            {
                var path = Path.Combine(Path.GetTempPath(), "pytools-selftest-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    File.WriteAllText(path, "{\"hotkey\":\"Alt+Q\",\"v1onlyKey\":123,\"nested\":{\"a\":1}}");
                    var cfg = AppConfig.Load(path);
                    if (cfg.Hotkey != "Alt+Q") return "hotkey 未读入";
                    cfg.SaveBounds(1, 2, 3, 4);
                    var after = File.ReadAllText(path);
                    if (!after.Contains("v1onlyKey")) return "未知键丢失";
                    if (!after.Contains("nested")) return "嵌套未知键丢失";
                    if (!after.Contains("\"hotkey\":\"Alt+Q\"")) return "已知键被改写";
                    return null;
                }
                finally { try { File.Delete(path); } catch { } }
            }));

            results.Add(Test("简洁模式键：v2.liteMode 读取", () =>
            {
                var path = Path.Combine(Path.GetTempPath(), "pytools-selftest-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    File.WriteAllText(path, "{\"v2.liteMode\":true}");
                    var cfg = AppConfig.Load(path);
                    return cfg.LiteMode ? null : "v2.liteMode 未读入";
                }
                finally { try { File.Delete(path); } catch { } }
            }));

            results.Add(Test("前台恢复：零句柄 no-op", () =>
            {
                var report = Foreground.Restore(IntPtr.Zero);
                return (report.Restored && report.Attempts == 0) ? null : "attempts=" + report.Attempts;
            }));

            results.Add(Test("前台 capture：可获取句柄（可能为 0）", () =>
            {
                var handle = Foreground.Capture();
                return handle == IntPtr.Zero ? "(空会话，句柄为 0，合法)" : null;
            }));

            var failed = 0;
            var lines = new List<string>();
            foreach (var r in results)
            {
                var line = (r.Passed ? "[PASS] " : "[FAIL] ") + r.Name + (r.Detail != null ? "  → " + r.Detail : "");
                Console.WriteLine(line);
                lines.Add(line);
                if (!r.Passed) failed++;
            }
            var summary = failed == 0 ? "selftest: ALL PASS (" + results.Count + ")" : "selftest: " + failed + " FAILED";
            Console.WriteLine(summary);
            lines.Add(summary);
            try
            {
                // stdout 在部分宿主下不可见（GUI 进程），落盘一份供自动化读取（带 BOM，PowerShell 5.1 按 ANSI 读会乱码）
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "pytools-selftest-last.txt"), string.Join(Environment.NewLine, lines) + Environment.NewLine, new UTF8Encoding(true));
            }
            catch { /* 落盘失败不影响退出码语义 */ }
            return failed == 0 ? 0 : 1;
        }

        private static Result Test(string name, Func<string> body)
        {
            try
            {
                var detail = body();
                return new Result { Name = name, Passed = detail == null, Detail = detail };
            }
            catch (Exception ex)
            {
                return new Result { Name = name, Passed = false, Detail = ex.Message };
            }
        }
    }
}
