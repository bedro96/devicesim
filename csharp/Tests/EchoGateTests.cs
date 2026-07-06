// =============================================================
// devicesim/csharp/tests/EchoGateTests.cs
// 반이중 에코 억제 게이트 단위 테스트
// =============================================================

using DeviceSim;
using Xunit;

namespace DeviceSim.Tests;

public class EchoGateTests
{
    // -------------------------------------------------------
    // 스피커 재생 중에는 업링크를 억제한다
    // -------------------------------------------------------
    [Fact]
    public void 재생_중에는_전송_억제()
    {
        var gate = new EchoGate(hangoverSeconds: 0.25);
        Assert.False(gate.ShouldSend(isPlaying: true, now: 1.0));
    }

    // -------------------------------------------------------
    // 재생이 아니고 hangover 도 지났으면 전송 허용
    // -------------------------------------------------------
    [Fact]
    public void 재생_아니고_hangover_경과시_전송_허용()
    {
        var gate = new EchoGate(hangoverSeconds: 0.25);
        // 초기 상태: 마지막 재생이 아주 과거 → 즉시 허용
        Assert.True(gate.ShouldSend(isPlaying: false, now: 10.0));
    }

    // -------------------------------------------------------
    // 재생 종료 직후 hangover 동안은 계속 억제한다
    // -------------------------------------------------------
    [Fact]
    public void 재생_종료_직후_hangover_동안_억제()
    {
        var gate = new EchoGate(hangoverSeconds: 0.25);
        // 재생 중 → 마지막 재생 시각 = 5.0
        Assert.False(gate.ShouldSend(isPlaying: true, now: 5.0));
        // 재생 종료 후 0.1초 (hangover 0.25초 이내) → 여전히 억제
        Assert.False(gate.ShouldSend(isPlaying: false, now: 5.1));
        // hangover 초과(0.3초) → 전송 허용
        Assert.True(gate.ShouldSend(isPlaying: false, now: 5.3));
    }
}
