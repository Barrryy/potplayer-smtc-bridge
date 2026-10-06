using System.Diagnostics;

namespace SmtcBridge;

/// <summary>
/// 静默后台模式：**没有托盘图标、没有窗口、任务栏也看不到**。
///
/// 首次注册之后程序就以这个形态常驻：每秒看一眼 PotPlayer 是否启动，
/// 发现新进程就注入一次，然后继续待命。用户全程无感。
///
/// 需要改设置时用 `PotPlayerSmtcBridge.exe --ui` 把主界面叫出来——
/// 它通过一个命名事件通知已经在跑的后台实例，自己并不再起一份；
/// 关闭主界面后回到静默状态。
/// </summary>
internal sealed class SilentApp : ApplicationContext
{
    private const string MutexName = @"Local\PotPlayerSmtcBridge.SingleInstance";
    private const string ShowUiEventName = @"Local\PotPlayerSmtcBridge.ShowUi";

    private readonly BridgeConfig _config;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly EventWaitHandle? _showUiEvent;
    private readonly Thread? _uiThread;
    private readonly CancellationTokenSource _stopping = new();

    private MainForm? _mainForm;
    private int _injectedPid;
    private bool _busy;

    public SilentApp(BridgeConfig config, Mutex instanceMutex, bool showWindow)
    {
        _config = config;
        InstanceMutex = instanceMutex;

        _showUiEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowUiEventName);
        _uiThread = new Thread(WaitForUiRequests) { IsBackground = true, Name = "ui-request" };
        _uiThread.Start();

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        // 自愈：程序被移动过、或旧版本写的是另一条命令行时，把自启项校正回指向自己
        if (_config.AutoStart) Injector.SetAutoStart(true);

        if (showWindow) ShowMainWindow();
    }

    public Mutex InstanceMutex { get; }

    private void WaitForUiRequests()
    {
        while (!_stopping.IsCancellationRequested)
        {
            if (_showUiEvent is null) return;
            if (!_showUiEvent.WaitOne(500)) continue;
            if (_stopping.IsCancellationRequested) return;
            ShowMainWindow();
        }
    }

    // ---------------------------------------------------------------- 自动注入

    private void Tick()
    {
        var process = FindPotPlayer();
        if (process is null)
        {
            // 播放器退出了，下次启动要重新注入
            _injectedPid = 0;
            return;
        }
        if (_busy || process.Id == _injectedPid) return;

        _busy = true;
        try
        {
            _injectedPid = process.Id;
            // 传入实际进程名：用户可能装的是 PotPlayer64 / PotPlayerMini 而不是 Mini64
            Injector.RunLoader("--name", process.ProcessName + ".exe");
        }
        finally
        {
            _busy = false;
        }
    }

    private static Process? FindPotPlayer()
    {
        foreach (var name in new[] { "PotPlayerMini64", "PotPlayer64", "PotPlayerMini" })
        {
            var found = Process.GetProcessesByName(name);
            if (found.Length > 0) return found[0];
        }
        return null;
    }

    // ---------------------------------------------------------------- 主界面

    private void ShowMainWindow()
    {
        if (_mainForm is null || _mainForm.IsDisposed)
        {
            _mainForm = new MainForm(_config, onExit: ExitApp);
        }
        _mainForm.Show();
        if (_mainForm.WindowState == FormWindowState.Minimized)
            _mainForm.WindowState = FormWindowState.Normal;
        _mainForm.Activate();
        _mainForm.BringToFront();
    }

    /// <summary>通知已有实例弹出主界面；没有实例时返回 false。</summary>
    public static bool SignalExistingInstance()
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(ShowUiEventName);
            handle.Set();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static Mutex? TryAcquireSingleInstance()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew) return mutex;
        mutex.Dispose();
        return null;
    }

    private void ExitApp()
    {
        _stopping.Cancel();
        _timer.Stop();
        _showUiEvent?.Set();   // 叫醒等待线程让它退出
        try { InstanceMutex.ReleaseMutex(); } catch { /* 未持有则忽略 */ }
        InstanceMutex.Dispose();
        ExitThread();
    }
}
