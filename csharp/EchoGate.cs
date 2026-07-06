// =============================================================
// devicesim/csharp/EchoGate.cs
// 반이중(half-duplex) 음향 에코 억제 게이트
//
// 문제: 이 PoC 는 하드웨어 AEC(음향 에코 제거)가 없다. 마이크가 장치 자신의
// 스피커 출력을 다시 잡으면, 서버 VAD 가 이를 사용자 발화로 오인해 바지인
// (audio_clear)을 트리거하고 에이전트 음성을 도중에 끊는 피드백 루프가 생긴다.
// ("대화 시작이 잘리고, 자기 말을 사용자 응답으로 인식")
//
// 해결: 스피커가 재생 중일 때(그리고 재생 종료 후 짧은 hangover 동안) 마이크
// 업링크를 억제한다. 실제 ThinQ 장치에는 하드웨어 AEC 가 있으므로 이 억제는
// PoC 전용 근사이며, --allow-barge-in 으로 비활성화할 수 있다.
// =============================================================

using System.Diagnostics;

namespace DeviceSim;

/// <summary>
/// 반이중 에코 억제 게이트. 스피커 재생 중 + hangover 동안 업링크를 억제한다.
/// </summary>
public sealed class EchoGate
{
    private readonly double _hangoverSeconds;
    // 마지막으로 "재생 중"이었던 시각(초). 초기값은 충분히 과거로 둔다.
    private double _lastPlayingTime = double.NegativeInfinity;

    /// <summary>
    /// 게이트를 생성한다.
    /// </summary>
    /// <param name="hangoverSeconds">
    /// 재생이 멈춘 뒤에도 업링크를 계속 억제할 여유 시간(초). 스피커 꼬리음과
    /// 룸 리버브가 마이크에 남는 것을 흡수한다. 기본 0.25초.
    /// </param>
    public EchoGate(double hangoverSeconds = 0.25)
    {
        _hangoverSeconds = hangoverSeconds;
    }

    /// <summary>
    /// 현재 마이크 청크를 서버로 전송해도 되는지 판단한다.
    /// </summary>
    /// <param name="isPlaying">스피커가 현재 재생 중인지 여부.</param>
    /// <param name="now">단조 증가 시각(초). 예: Stopwatch 경과 시간.</param>
    /// <returns>전송 허용이면 true, 억제해야 하면 false.</returns>
    public bool ShouldSend(bool isPlaying, double now)
    {
        if (isPlaying)
        {
            _lastPlayingTime = now;
            return false;
        }
        // 재생이 끝났어도 hangover 동안은 계속 억제
        if (now - _lastPlayingTime < _hangoverSeconds)
            return false;
        return true;
    }

    /// <summary>
    /// 단조 증가 시계를 만드는 헬퍼. UplinkLoop 에서 now 값으로 사용한다.
    /// </summary>
    public static Func<double> MonotonicClock()
    {
        var sw = Stopwatch.StartNew();
        return () => sw.Elapsed.TotalSeconds;
    }
}
