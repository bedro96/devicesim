// =============================================================
// devicesim/csharp/Tests/DownmixTests.cs
// TDD: PCM16 스테레오 → 모노 다운믹스 테스트
//
// 테스트 대상: Downmix.StereoToMono()
// =============================================================

using DeviceSim;
using Xunit;

namespace DeviceSim.Tests;

/// <summary>
/// Downmix 유틸리티 단위 테스트.
/// </summary>
public class DownmixTests
{
    // -------------------------------------------------------
    // 기본 다운믹스: 동일한 두 채널 → 평균 = 원본
    // -------------------------------------------------------
    [Fact]
    public void StereoToMono_IdenticalChannels_ReturnsSameSample()
    {
        // 두 채널이 같은 값이면 평균도 같아야 함
        // 샘플: +1000 (L), +1000 (R) → +1000 (Mono)
        short value = 1000;
        var stereo = MakeStereoFrame(value, value);

        var mono = Downmix.StereoToMono(stereo);

        // 출력: 2 bytes (모노 1 샘플)
        Assert.Equal(2, mono.Length);
        short result = ReadInt16LE(mono, 0);
        Assert.Equal(value, result);
    }

    // -------------------------------------------------------
    // 좌우 채널 평균
    // -------------------------------------------------------
    [Fact]
    public void StereoToMono_DifferentChannels_ReturnsAverage()
    {
        // L=2000, R=0 → 평균 = 1000
        var stereo = MakeStereoFrame(2000, 0);

        var mono = Downmix.StereoToMono(stereo);

        short result = ReadInt16LE(mono, 0);
        Assert.Equal(1000, result);
    }

    // -------------------------------------------------------
    // 음수 샘플 처리
    // -------------------------------------------------------
    [Fact]
    public void StereoToMono_NegativeSamples_HandledCorrectly()
    {
        // L=-1000, R=-500 → 평균 = -750
        var stereo = MakeStereoFrame(-1000, -500);

        var mono = Downmix.StereoToMono(stereo);

        short result = ReadInt16LE(mono, 0);
        Assert.Equal(-750, result);
    }

    // -------------------------------------------------------
    // 다중 프레임: 출력 크기 = 입력 크기 / 2
    // -------------------------------------------------------
    [Fact]
    public void StereoToMono_MultipleFrames_OutputHalfSize()
    {
        // 4 스테레오 프레임 (16 bytes) → 4 모노 프레임 (8 bytes)
        int frames = 4;
        var stereo = new byte[frames * 4];
        for (int i = 0; i < frames; i++)
        {
            WriteInt16LE(stereo, i * 4,     (short)(i * 100));
            WriteInt16LE(stereo, i * 4 + 2, (short)(i * 100));
        }

        var mono = Downmix.StereoToMono(stereo);

        Assert.Equal(frames * 2, mono.Length);
        for (int i = 0; i < frames; i++)
        {
            short expected = (short)(i * 100);
            short actual   = ReadInt16LE(mono, i * 2);
            Assert.Equal(expected, actual);
        }
    }

    // -------------------------------------------------------
    // 빈 입력: 빈 출력
    // -------------------------------------------------------
    [Fact]
    public void StereoToMono_EmptyInput_ReturnsEmpty()
    {
        var mono = Downmix.StereoToMono(Array.Empty<byte>());
        Assert.Empty(mono);
    }

    // -------------------------------------------------------
    // 오버플로 없음: 최대 양수 + 최대 양수
    // short.MaxValue = 32767, 평균 = 32767
    // -------------------------------------------------------
    [Fact]
    public void StereoToMono_MaxPositive_NoOverflow()
    {
        var stereo = MakeStereoFrame(short.MaxValue, short.MaxValue);

        var mono = Downmix.StereoToMono(stereo);

        short result = ReadInt16LE(mono, 0);
        Assert.Equal(short.MaxValue, result);
    }

    // -------------------------------------------------------
    // Span 오버로드: 결과 일치
    // -------------------------------------------------------
    [Fact]
    public void StereoToMono_SpanOverload_MatchesArrayOverload()
    {
        var stereo = MakeStereoFrame(300, 700);

        var monoArray = Downmix.StereoToMono(stereo);

        var monoSpan = new byte[2];
        int written = Downmix.StereoToMono(stereo.AsSpan(), monoSpan.AsSpan());

        Assert.Equal(2, written);
        Assert.Equal(monoArray, monoSpan);
    }

    // -------------------------------------------------------
    // MonoBytesFromStereo 도우미
    // -------------------------------------------------------
    [Fact]
    public void MonoBytesFromStereo_ReturnsHalf()
    {
        Assert.Equal(480 * 2, Downmix.MonoBytesFromStereo(480 * 4));
    }

    // -------------------------------------------------------
    // 헬퍼 메서드
    // -------------------------------------------------------

    /// <summary>스테레오 1프레임(4 bytes)을 만듭니다.</summary>
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
