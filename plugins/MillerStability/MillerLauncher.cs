// MillerLauncher.cs
// GRT 2021.2030-NIGHTLY 向插件传递无效的 --ipcport 0，导致 Miller 插件连接超时自杀。
// 本启动器由 GRT 清单拉起后，自动发现 GRT 进程的真实 TCP 监听端口，
// 逐个用 --ipcport <port> 尝试拉起 Miller：5 秒内未退出即视为连接成功。
// 纯自研代码，不修改 GRT 任何文件。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

static class MillerLauncher
{
    const string MillerExe = "GRT_Miller_Stability_Plugin.exe";
    const string GrtProcName = "GordonsReloadingTool";
    const int TryWaitMs = 5000;          // Miller 失败时约 3.2 秒后自杀，等 5 秒判定
    static readonly List<string> LogLines = new List<string>();

    static void Log(string fmt, params object[] a)
    {
        string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + string.Format(fmt, a);
        LogLines.Add(line);
    }

    static void FlushLog(string dir)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("---- " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ----");
            foreach (var l in LogLines) sb.AppendLine(l);
            File.AppendAllText(Path.Combine(dir, "MillerLauncher.log"), sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }

    static void Main()
    {
        string baseDir = "";
        try
        {
            baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string millerPath = Path.Combine(baseDir, MillerExe);

            // 已有 Miller 在运行（上次已连上）则不重复拉起
            if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(MillerExe)).Length > 0)
            {
                Log("Miller already running, exit.");
                FlushLog(baseDir);
                return;
            }
            if (!File.Exists(millerPath))
            {
                Log("Miller exe not found: " + millerPath);
                FlushLog(baseDir);
                return;
            }

            // 收集所有 GRT 进程的 TCP 监听端口
            var grtPids = new HashSet<int>();
            foreach (var p in Process.GetProcessesByName(GrtProcName)) grtPids.Add(p.Id);
            if (grtPids.Count == 0) { Log("no GRT process, exit."); FlushLog(baseDir); return; }

            // GRT 每次插件附着会轮换监听端口，此处多轮重扫直到成功
            string lastFile = Path.Combine(baseDir, "MillerLauncher.port");
            for (int round = 1; round <= 5; round++)
            {
                Log("round " + round + ": scan listeners");
                var candidates = new List<int>();
                foreach (var l in RunNetstat())
                {
                    // 形如: TCP    127.0.0.1:13607    0.0.0.0:0    LISTENING    122820
                    var parts = l.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 5 && parts[3] == "LISTENING")
                    {
                        int pid, port;
                        var local = parts[1];
                        int ci = local.LastIndexOf(':');
                        if (ci > 0 && int.TryParse(parts[parts.Length - 1], out pid) && grtPids.Contains(pid)
                            && int.TryParse(local.Substring(ci + 1), out port))
                            candidates.Add(port);
                    }
                }
                // 去重，并把上次成功的端口排最前
                var seen = new HashSet<int>();
                var ordered = new List<int>();
                if (File.Exists(lastFile))
                {
                    int last;
                    if (int.TryParse(File.ReadAllText(lastFile).Trim(), out last))
                    { ordered.Add(last); seen.Add(last); }
                }
                foreach (var c in candidates) if (seen.Add(c)) ordered.Add(c);
                Log("round " + round + " listeners: " + string.Join(",", ordered));

                foreach (var port in ordered)
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = millerPath,
                        Arguments = "--ipcport " + port,
                        WorkingDirectory = baseDir,
                        UseShellExecute = false
                    };
                    Log("try --ipcport " + port);
                    var p = Process.Start(psi);
                    if (p.WaitForExit(TryWaitMs))
                    {
                        Log("port " + port + " -> Miller exited (code " + p.ExitCode + ")");
                        continue;
                    }
                    File.WriteAllText(lastFile, port.ToString());
                    Log("port " + port + " -> ALIVE, success");
                    FlushLog(baseDir);
                    return; // Miller 保持运行，启动器退出
                }
                if (round < 5) Thread.Sleep(3000); // 等 GRT 建立新的插件监听端口
            }
            Log("all rounds failed");
            FlushLog(baseDir);
        }
        catch (Exception ex)
        {
            Log("EXC " + ex.Message);
            try { FlushLog(baseDir); } catch { }
        }
    }

    static IEnumerable<string> RunNetstat()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netstat.exe",
            Arguments = "-ano -p tcp",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        using (var p = Process.Start(psi))
        {
            string line;
            while ((line = p.StandardOutput.ReadLine()) != null) yield return line;
            p.WaitForExit(3000);
        }
    }
}
