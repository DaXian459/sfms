using System.Runtime.InteropServices;

namespace SFMS.Collector;

/// <summary>
/// Windows 默认定时器粒度约 15.6ms，导致 <c>Task.Delay(50)</c> 实际返回在 62.5ms，
/// 50ms 采集组会被拉到 62.5ms，偏离计划书 3.4 的"最小采集周期 50ms"。
/// 这里在采集服务启动期间把系统定时器精度提到 1ms，退出时恢复，属于工控采集的常规做法。
/// 非 Windows 平台为空实现（Linux 定时器精度本就是 1ms 级）。
/// </summary>
internal static class TimerResolution
{
    private const uint TargetMs = 1;
    private static int _depth;

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uPeriod);

    /// <summary>提升精度；引用计数，可重复调用。</summary>
    public static void Raise()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Interlocked.Increment(ref _depth) != 1) return;
        try { TimeBeginPeriod(TargetMs); }
        catch (DllNotFoundException) { Interlocked.Decrement(ref _depth); }
    }

    /// <summary>恢复精度。</summary>
    public static void Restore()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Interlocked.Decrement(ref _depth) != 0) return;
        Interlocked.Exchange(ref _depth, 0);
        try { TimeEndPeriod(TargetMs); }
        catch (DllNotFoundException) { /* 无 winmm 时忽略 */ }
    }
}
