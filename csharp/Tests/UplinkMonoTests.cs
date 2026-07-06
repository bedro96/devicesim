// =============================================================
// devicesim/csharp/tests/UplinkMonoTests.cs
// TDD: 업링크 모노 정규화 테스트
//
// 요구사항: Azure Voice Live 입력은 PCM16 모노만 허용한다.
//   - --channels 2 (스테레오/합성 2ch) 캡처는 전송 전에 반드시 모노로
//     다운믹스되어야 한다. 그대로 2채널을 보내면 서버가 인터리브드 바이트를
//     모노로 오해석해 오디오가 깨진다.
//   - 기본값은 모노(1채널)이며 이 경로는 그대로 통과한다.
//
// 테스트 대상:
//   - Downmix.ToMono(byte[], int channels)
//   - WsSession.SourceChannels(CaptureMode)
// =============================================================

using DeviceSim;
using Xunit;

namespace DeviceSim.Tests;

/// <summary>
/// 업링크가 항상 모노 PCM16 으로 정규화되는지 검증합니다.
/// </summary>
public class UplinkMonoTests
{
    // -------------------------------------------------------
    // Downmix.ToMono: 2채널이면 다운믹스, 1채널이면 그대로
    // -------------------------------------------------------

    [Fact]
    public void ToMono_TwoChannels_DownmixesToHalfSize()
    {
        // L=2000, R=0 → 평균 1000, 크기 절반
        var stereo = MakeStereoFrame(2000, 0);

        var mono = Downmix.ToMono(stereo, channels: 2);

        Assert.Equal(2, mono.Length);
        Assert.Equal(1000, ReadInt16LE(mono, 0));
    }

    [Fact]
    public void ToMono_OneChannel_ReturnsInputUnchanged()
    {
        // 이미 모노인 청크는 변형 없이 그대로 통과해야 한다
        var mono = new byte[] { 0x10, 0x20, 0x30, 0x40 };

        var result = Downmix.ToMono(mono, channels: 1);

        Assert.Equal(mono, result);
    }

    [Fact]
    public void ToMono_TwoChannels_HalvesByteCountForManyFrames()
    {
        // 100 스테레오 프레임(400 bytes) → 100 모노 프레임(200 bytes)
        var stereo = new byte[100 * 4];

        var mono = Downmix.ToMono(stereo, channels: 2);

        Assert.Equal(200, mono.Length);
    }

    // -------------------------------------------------------
    // WsSession.SourceChannels: 캡처 모드 → 소스 채널 수
    //   Stereo/Synthetic 은 2채널을 생성하므로 다운믹스 대상이다.
    // -------------------------------------------------------

    [Theory]
    [InlineData(CaptureMode.Stereo,    2)]
    [InlineData(CaptureMode.Synthetic, 2)]
    [InlineData(CaptureMode.Mono,      1)]
    [InlineData(CaptureMode.File,      1)]
    public void SourceChannels_MapsModeToChannelCount(CaptureMode mode, int expected)
    {
        Assert.Equal(expected, WsSession.SourceChannels(mode));
    }

    // -------------------------------------------------------
    // 헬퍼
    // -------------------------------------------------------

    private static byte[] MakeStereoFrame(short left, short right)
    {
        var buf = new byte[4];
        WriteInt16LE(buf, 0, left);
        WriteInt16LE(buf, 2, right);
        return buf;
    }

    private static void WriteInt16LE(byte[] buf, int offset, short value)
    {
        buf[offset]     = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    private static short ReadInt16LE(byte[] buf, int offset)
        => (short)(buf[offset] | (buf[offset + 1] << 8));
}
