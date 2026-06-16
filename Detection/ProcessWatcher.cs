using System.Diagnostics;

namespace Loudswitch.Detection;

/// <summary>
/// 대상 프로세스의 실행 여부를 일정 간격으로 셀프 폴링해 시작/종료를 edge 검출한다.
///
/// level 트리거(매 틱 현재 상태를 재평가)라 한 틱을 놓치거나 일시적으로 실패해도
/// 다음 틱이 자동 교정한다(자가 치유). 무권한 + 외부 의존성 없음(순수 BCL).
/// </summary>
internal sealed class ProcessWatcher : IDisposable
{
    private readonly string _processName;   // 확장자 없는 이름 (예: "notepad")
    private readonly int _intervalMs;
    private readonly System.Threading.Timer _timer;

    private bool? _present;                  // null = 첫 틱 전(베이스라인 미정)
    private int _polling;                    // 재진입 방지 플래그

    /// <summary>시작 감지. 인자는 현재 인스턴스 수.</summary>
    public event Action<int>? Started;

    /// <summary>종료 감지(인스턴스가 0이 됨).</summary>
    public event Action? Stopped;

    public ProcessWatcher(string processName, int intervalMs = 1500)
    {
        _processName = StripExe(processName);
        _intervalMs = intervalMs;
        _timer = new System.Threading.Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>폴링 시작(즉시 첫 틱 후 간격 반복).</summary>
    public void Start() => _timer.Change(0, _intervalMs);

    private void Poll()
    {
        // 폴링이 간격보다 길어질 경우 콜백 중첩 방지
        if (Interlocked.Exchange(ref _polling, 1) == 1)
            return;

        try
        {
            int count = CountRunning();
            bool present = count > 0;

            if (_present is null)
            {
                // 첫 틱: 베이스라인 설정. 시작 시 이미 떠 있으면 Started 한 번 발생(상태 동기화용).
                _present = present;
                if (present)
                    Started?.Invoke(count);
                return;
            }

            if (present && _present == false)
                Started?.Invoke(count);
            else if (!present && _present == true)
                Stopped?.Invoke();

            _present = present;
        }
        catch
        {
            // 일시적 실패(예: 열거 중 예외)는 무시 — 다음 틱이 재평가(자가 치유).
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private int CountRunning()
    {
        Process[] procs = Process.GetProcessesByName(_processName);
        try
        {
            return procs.Length;
        }
        finally
        {
            // Process 객체는 OS 핸들을 쥐므로 반드시 해제(핸들 누수 방지).
            foreach (Process p in procs)
                p.Dispose();
        }
    }

    private static string StripExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    public void Dispose() => _timer.Dispose();
}
