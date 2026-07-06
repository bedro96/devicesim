// =============================================================
// devicesim/csharp/Tests/AudioCaptureTests.cs
// TDD: AudioCapture 초기화 및 폴백 로직 테스트
//
// 테스트 대상: AudioCapture 생성자 (폴백 우선순위)
// CI/하드웨어 없는 환경에서는 합성 모드로 폴백해야 함
// =============================================================

using DeviceSim;
using Xunit;

namespace DeviceSim.Tests;

/// <summary>
/// AudioCapture 폴백 로직 단위 테스트.
/// </summary>
public class AudioCaptureTests
{
    // -------------------------------------------------------
    // 하드웨어 없는 환경: 합성 모드로 폴백
    // CI 서버는 마이크 장치가 없으므로 Synthetic 이어야 함
    // -------------------------------------------------------
    [Fact]
    public void Constructor_NoHardware_FallsBackToSynthetic()
    {
        try
        {
            // 실제 마이크가 있는 개발 머신(macOS 등)에서도 합성 폴백을 검증
            Environment.SetEnvironmentVariable("DEVICESIM_FORCE_SYNTHETIC", "1");
            using var capture = new AudioCapture(deviceIndex: 999);

            // 하드웨어가 없으면 반드시 Synthetic 모드여야 함
            Assert.Equal(CaptureMode.Synthetic, capture.Mode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEVICESIM_FORCE_SYNTHETIC", null);
        }
    }

    // -------------------------------------------------------
    // maxChannels=1: 모노 또는 합성 모드 (2채널 시도 건너뜀)
    // -------------------------------------------------------
    [Fact]
    public void Constructor_MaxChannels1_NeverStereo()
    {
        using var capture = new AudioCapture(deviceIndex: 999, maxChannels: 1);

        // 2ch 시도를 건너뛰므로 Stereo 가 아니어야 함
        Assert.NotEqual(CaptureMode.Stereo, capture.Mode);
    }

    // -------------------------------------------------------
    // DEVICESIM_FORCE_MONO 환경 변수: Stereo 건너뜀
    // -------------------------------------------------------
    [Fact]
    public void Constructor_ForceMonoEnvVar_NeverStereo()
    {
        try
        {
            Environment.SetEnvironmentVariable("DEVICESIM_FORCE_MONO", "1");
            using var capture = new AudioCapture(deviceIndex: 999);

            Assert.NotEqual(CaptureMode.Stereo, capture.Mode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEVICESIM_FORCE_MONO", null);
        }
    }

    // -------------------------------------------------------
    // 합성 모드에서 Start → 청크 수신
    // -------------------------------------------------------
    [Fact]
    public async Task Start_SyntheticMode_ReceivesChunks()
    {
        try
        {
            Environment.SetEnvironmentVariable("DEVICESIM_FORCE_SYNTHETIC", "1");
            using var capture = new AudioCapture(deviceIndex: 999); // 합성 모드 강제
            Assert.Equal(CaptureMode.Synthetic, capture.Mode);

            using var cts    = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            var chunks       = new List<byte[]>();

            capture.Start(chunk => chunks.Add(chunk), cts.Token);

            // 150ms 대기 → 최소 1개 청크 수신
            try { await Task.Delay(200, cts.Token); } catch (OperationCanceledException) { }

            Assert.NotEmpty(chunks);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEVICESIM_FORCE_SYNTHETIC", null);
        }
    }

    // -------------------------------------------------------
    // 합성 청크: 2채널 인터리브드 PCM16 (4 bytes/샘플)
    // -------------------------------------------------------
    [Fact]
    public async Task Start_SyntheticMode_ChunksAreStereo()
    {
        try
        {
            Environment.SetEnvironmentVariable("DEVICESIM_FORCE_SYNTHETIC", "1");
            using var capture = new AudioCapture(deviceIndex: 999);
            Assert.Equal(CaptureMode.Synthetic, capture.Mode);

            byte[]? firstChunk = null;
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            capture.Start(chunk =>
            {
                if (firstChunk is null) firstChunk = chunk;
            }, cts.Token);

            try { await Task.Delay(150, cts.Token); } catch (OperationCanceledException) { }

            Assert.NotNull(firstChunk);
            // 2채널 × 2 bytes × SamplesPerChunk = 4 × 480 = 1920 bytes
            int expectedBytes = AudioCapture.SamplesPerChunk * 2 * 2;
            Assert.Equal(expectedBytes, firstChunk!.Length);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEVICESIM_FORCE_SYNTHETIC", null);
        }
    }

    // -------------------------------------------------------
    // AudioCapture 상수 검증
    // -------------------------------------------------------
    [Fact]
    public void Constants_MatchVoiceLiveSpec()
    {
        // VoiceLive 입력: PCM16 / 24 kHz
        Assert.Equal(24_000, AudioCapture.SampleRate);
        Assert.Equal(16,     AudioCapture.BitDepth);
        Assert.Equal(480,    AudioCapture.SamplesPerChunk); // 20ms @ 24kHz
    }
}
