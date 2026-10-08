using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace TESCWatchDog;

public partial class Form1
{
    private const int ParserRestartCooldownSeconds = 60;
    private const int ParserDailyRestartLimit = 5;
    private readonly string parserSettingsPath = Path.Combine(AppContext.BaseDirectory, "parser-settings.json");
    private CancellationTokenSource? parserMonitorCancellation;
    private string? lastParserHealthState;
    private long parserLogPosition;
    private int parserMissingChecks;
    private DateOnly parserRestartDate = DateOnly.FromDateTime(DateTime.Today);
    private int parserRestartCount;
    private DateTimeOffset parserLastRestart = DateTimeOffset.MinValue;
    private bool parserRestartInProgress;

    private sealed class ParserSettings
    {
        public string ParserDirectory { get; set; } = string.Empty;
        public int ConsecutiveFailureCount { get; set; } = 3;
    }

    private void InitializeParserSettings()
    {
        try
        {
            if (File.Exists(parserSettingsPath))
            {
                var settings = JsonSerializer.Deserialize<ParserSettings>(File.ReadAllText(parserSettingsPath));
                if (!string.IsNullOrWhiteSpace(settings?.ParserDirectory))
                    parserPathTextBox.Text = settings.ParserDirectory;
                if (settings != null)
                    parserFailureCountNumeric.Value = Math.Clamp(
                        settings.ConsecutiveFailureCount,
                        (int)parserFailureCountNumeric.Minimum,
                        (int)parserFailureCountNumeric.Maximum);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppendLog($"Parser 路徑設定載入失敗，使用預設值：{ex.Message}");
        }

        // 不需要額外的儲存按鈕：完成欄位編輯時即保存。
        parserPathTextBox.Leave += (_, _) => SaveParserSettings();
        parserFailureCountNumeric.ValueChanged += (_, _) => SaveParserSettings();
    }

    private void SaveParserSettings()
    {
        var directory = parserPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(directory)) return;

        try
        {
            var json = JsonSerializer.Serialize(
                new ParserSettings
                {
                    ParserDirectory = directory,
                    ConsecutiveFailureCount = (int)parserFailureCountNumeric.Value
                },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(parserSettingsPath, json, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppendLog($"Parser 路徑設定保存失敗：{ex.Message}");
        }
    }

    private async void StartParserMonitorButton_Click(object? sender, EventArgs e)
    {
        if (parserMonitorCancellation != null) return;

        var parserDirectory = parserPathTextBox.Text.Trim();
        SaveParserSettings();
        if (!Directory.Exists(parserDirectory))
        {
            parserStatusLabel.Text = "Parser 資料夾不存在";
            parserStatusLabel.ForeColor = Color.Firebrick;
            AppendLog($"Parser 監控無法啟動：找不到資料夾 {parserDirectory}");
            return;
        }

        var expectedPython = Path.Combine(parserDirectory, ".venv", "Scripts", "python.exe");
        if (!File.Exists(expectedPython))
        {
            parserStatusLabel.Text = "Parser 尚未安裝：請先執行 install_windows.bat";
            parserStatusLabel.ForeColor = Color.Firebrick;
            parserProcessLabel.Text = $"程序：缺少 {expectedPython}";
            AppendLog($"Parser 監控無法啟動：尚未建立 .venv，請先執行 {Path.Combine(parserDirectory, "install_windows.bat")}");
            return;
        }

        parserLogPathTextBox.Text = Path.Combine(parserDirectory, "logs", "bridge.log");
        parserMonitorCancellation = new CancellationTokenSource();
        startParserMonitorButton.Enabled = false;
        stopParserMonitorButton.Enabled = true;
        parserPathTextBox.Enabled = false;
        parserIntervalNumeric.Enabled = false;
        parserFailureCountNumeric.Enabled = false;
        lastParserHealthState = null;
        parserMissingChecks = 0;
        parserLogPosition = File.Exists(parserLogPathTextBox.Text) ? new FileInfo(parserLogPathTextBox.Text).Length : 0;
        AppendLog($"Parser 監控開始：{parserDirectory}");

        try
        {
            await MonitorParserAsync(parserDirectory, parserMonitorCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            AppendLog("已停止 Parser 監控。");
        }
        catch (Exception ex)
        {
            AppendLog($"Parser 監控異常停止：{ex.Message}");
        }
        finally
        {
            parserMonitorCancellation?.Dispose();
            parserMonitorCancellation = null;
            if (!IsDisposed && !Disposing)
            {
                startParserMonitorButton.Enabled = true;
                stopParserMonitorButton.Enabled = false;
                parserPathTextBox.Enabled = true;
                parserIntervalNumeric.Enabled = true;
                parserFailureCountNumeric.Enabled = true;
                parserStatusLabel.Text = "Parser 監控已停止";
                parserStatusLabel.ForeColor = SystemColors.ControlText;
            }
        }
    }

    private void StopParserMonitorButton_Click(object? sender, EventArgs e)
    {
        stopParserMonitorButton.Enabled = false;
        parserMonitorCancellation?.Cancel();
    }

    private async void RestartParserButton_Click(object? sender, EventArgs e)
    {
        var directory = parserPathTextBox.Text.Trim();
        try
        {
            restartParserButton.Enabled = false;
            await RestartParserAsync(directory, "使用者手動重啟", CancellationToken.None, enforcePolicy: false);
        }
        catch (Exception ex)
        {
            AppendLog($"手動重啟 Parser 失敗：{ex.Message}");
        }
        finally
        {
            if (!IsDisposed && !Disposing) restartParserButton.Enabled = true;
        }
    }

    private async Task MonitorParserAsync(string parserDirectory, CancellationToken token)
    {
        var expectedPython = Path.GetFullPath(Path.Combine(parserDirectory, ".venv", "Scripts", "python.exe"));
        var logPath = Path.Combine(parserDirectory, "logs", "bridge.log");

        while (true)
        {
            token.ThrowIfCancellationRequested();
            var process = FindParserProcess(expectedPython);
            var analysis = await TryAnalyzeNewParserLogAsync(logPath, token);
            string state;

            if (process == null)
            {
                parserMissingChecks++;
                state = "stopped";
                parserStatusLabel.Text = $"異常：找不到指定 Parser 的執行程序（連續 {parserMissingChecks} 次）";
                parserStatusLabel.ForeColor = Color.Firebrick;
                parserProcessLabel.Text = $"程序：未執行（預期 {expectedPython}）";
            }
            else
            {
                parserMissingChecks = 0;
                using (process)
                {
                    state = "running";
                    parserProcessLabel.Text = $"程序：執行中，PID {process.Id}，啟動時間 {SafeStartTime(process):yyyy-MM-dd HH:mm:ss}";
                }

                if (File.Exists(logPath))
                {
                    var lastWrite = File.GetLastWriteTime(logPath);
                    var age = DateTime.Now - lastWrite;
                    parserStatusLabel.Text = $"正常執行；Parser Log 於 {lastWrite:yyyy-MM-dd HH:mm:ss} 更新（{FormatAge(age)}前）";
                    parserStatusLabel.ForeColor = age > TimeSpan.FromMinutes(5) ? Color.DarkOrange : Color.ForestGreen;
                }
                else
                {
                    parserStatusLabel.Text = "程序執行中，但尚未建立 logs\\bridge.log";
                    parserStatusLabel.ForeColor = Color.DarkOrange;
                }
            }

            if (analysis.TransientCount > 0)
                AppendLog($"Parser Log 分析：發現 {analysis.TransientCount} 筆可自行恢復的連線／DB重試訊息，不重啟。");
            if (analysis.DataErrorCount > 0)
                AppendLog($"Parser Log 分析：發現 {analysis.DataErrorCount} 筆資料／SQL寫入錯誤，重啟無法修復，僅記錄。");

            var missingThresholdReached = process == null && parserMissingChecks >= (int)parserFailureCountNumeric.Value;
            if (parserAutoRestartCheckBox.Checked && (missingThresholdReached || analysis.FatalReason != null))
            {
                var reason = analysis.FatalReason ?? $"連續 {parserMissingChecks} 次找不到 Parser 程序";
                var restarted = await RestartParserAsync(parserDirectory, reason, token, enforcePolicy: true);
                if (restarted)
                {
                    state = "restarting";
                    parserMissingChecks = 0;
                    parserStatusLabel.Text = $"已自動重啟 Parser：{reason}";
                    parserStatusLabel.ForeColor = Color.DarkOrange;
                }
            }

            if (state != lastParserHealthState)
            {
                AppendLog(state switch
                {
                    "running" => "Parser 狀態：程序正在執行。",
                    "restarting" => "Parser 狀態：已執行 start_parser.bat，等待確認新程序。",
                    _ => "Parser 狀態：程序未執行。"
                });
                lastParserHealthState = state;
            }

            parserLogTextBox.Text = await TryReadLogTailAsync(logPath, token);
            parserLogTextBox.SelectionStart = parserLogTextBox.TextLength;
            parserLogTextBox.ScrollToCaret();

            await Task.Delay(TimeSpan.FromSeconds((double)parserIntervalNumeric.Value), token);
        }
    }

    private async Task<bool> RestartParserAsync(string parserDirectory, string reason, CancellationToken token, bool enforcePolicy)
    {
        if (parserRestartInProgress) return false;
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (parserRestartDate != today)
        {
            parserRestartDate = today;
            parserRestartCount = 0;
        }

        if (enforcePolicy)
        {
            if (parserRestartCount >= ParserDailyRestartLimit)
            {
                parserStatusLabel.Text = $"需要重啟，但已達每日上限 {parserRestartCount} 次：{reason}";
                parserStatusLabel.ForeColor = Color.Firebrick;
                return false;
            }
            var cooldown = TimeSpan.FromSeconds(ParserRestartCooldownSeconds);
            var remaining = cooldown - (DateTimeOffset.Now - parserLastRestart);
            if (remaining > TimeSpan.Zero)
            {
                parserStatusLabel.Text = $"需要重啟，但仍在冷卻期（剩餘 {remaining.TotalSeconds:F0} 秒）：{reason}";
                parserStatusLabel.ForeColor = Color.DarkOrange;
                return false;
            }
        }

        var expectedPython = Path.GetFullPath(Path.Combine(parserDirectory, ".venv", "Scripts", "python.exe"));
        var startScript = Path.Combine(parserDirectory, "start_parser.bat");
        if (!File.Exists(expectedPython))
            throw new FileNotFoundException("找不到 Parser 虛擬環境 Python；請先執行 install_windows.bat。", expectedPython);
        if (!File.Exists(startScript)) throw new FileNotFoundException("找不到 Parser start_parser.bat。", startScript);

        parserRestartInProgress = true;
        try
        {
            using var existing = FindParserProcess(expectedPython);
            if (existing != null)
            {
                AppendLog($"準備停止 Parser PID {existing.Id}：{reason}");
                existing.Kill(entireProcessTree: true);
                await existing.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(15), token);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = startScript,
                WorkingDirectory = parserDirectory,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Minimized
            };
            using var started = Process.Start(startInfo) ?? throw new InvalidOperationException("Windows 未能執行 start_parser.bat。");
            parserRestartCount++;
            parserLastRestart = DateTimeOffset.Now;
            AppendLog($"已執行 start_parser.bat（啟動程序 PID {started.Id}）；原因：{reason}；今日第 {parserRestartCount} 次。");
            return true;
        }
        finally
        {
            parserRestartInProgress = false;
        }
    }

    private sealed record ParserLogAnalysis(int TransientCount, int DataErrorCount, string? FatalReason)
    {
        public static readonly ParserLogAnalysis Empty = new(0, 0, null);
    }

    private async Task<ParserLogAnalysis> TryAnalyzeNewParserLogAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return ParserLogAnalysis.Empty;
        try
        {
            return await AnalyzeNewParserLogAsync(path, token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Log 輪替或寫入中的短暫鎖定不代表 Parser 故障，下次檢查再讀即可。
            parserStatusLabel.Text = $"Parser Log 暫時無法讀取，下次重試：{ex.Message}";
            parserStatusLabel.ForeColor = Color.DarkOrange;
            return ParserLogAnalysis.Empty;
        }
    }

    private async Task<ParserLogAnalysis> AnalyzeNewParserLogAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length < parserLogPosition) parserLogPosition = 0; // 每日輪替或檔案被重建。
        stream.Seek(parserLogPosition, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
        var text = await reader.ReadToEndAsync(token);
        parserLogPosition = stream.Position;

        var transient = 0;
        var dataErrors = 0;
        string? fatal = null;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Contains("[CRITICAL]", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Traceback", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Fatal Python error", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase))
                fatal ??= line;
            else if (line.Contains("MQTT disconnected unexpectedly", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("MQTT connect failed", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("DB reconnect failed", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("DB keepalive failed", StringComparison.OrdinalIgnoreCase))
                transient++;
            else if (line.Contains("DB insert error", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("JSON decode error", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("Queue is full", StringComparison.OrdinalIgnoreCase))
                dataErrors++;
        }
        return new ParserLogAnalysis(transient, dataErrors, fatal);
    }

    private static Process? FindParserProcess(string expectedPython)
    {
        foreach (var processName in new[] { "python", "pythonw" })
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    var executable = process.MainModule?.FileName;
                    if (executable != null && string.Equals(Path.GetFullPath(executable), expectedPython, StringComparison.OrdinalIgnoreCase))
                        return process;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // 程序可能恰好結束或權限不足；略過後繼續檢查其他 Python。
                }
                process.Dispose();
            }
        }
        return null;
    }

    private static DateTime SafeStartTime(Process process)
    {
        try { return process.StartTime; }
        catch { return DateTime.MinValue; }
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age.TotalSeconds < 60) return $"{age.TotalSeconds:F0} 秒";
        if (age.TotalMinutes < 60) return $"{age.TotalMinutes:F0} 分鐘";
        return $"{age.TotalHours:F1} 小時";
    }

    private static async Task<string> ReadLogTailAsync(string path, CancellationToken token)
    {
        const int maxBytes = 128 * 1024;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maxBytes) stream.Seek(-maxBytes, SeekOrigin.End);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        var text = await reader.ReadToEndAsync(token);
        if (stream.Length > maxBytes)
        {
            var firstLine = text.IndexOf('\n');
            if (firstLine >= 0) text = text[(firstLine + 1)..];
        }
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return string.Join(Environment.NewLine, lines.TakeLast(200));
    }

    private static async Task<string> TryReadLogTailAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return "尚未找到 Parser Log：" + path;
        try
        {
            return await ReadLogTailAsync(path, token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Parser Log 正在寫入或輪替，下一輪將重新讀取。{Environment.NewLine}{ex.Message}";
        }
    }
}
