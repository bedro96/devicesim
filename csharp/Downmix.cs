// =============================================================
// devicesim/csharp/Downmix.cs
// PCM16 스테레오 → 모노 다운믹스 유틸리티
//
// ARM 2× MEMS 마이크 어레이 → Azure VoiceLive 전달 경로:
//   마이크L + 마이크R (인터리브드 PCM16 2ch)
//   → DownmixStereoToMono()
//   → PCM16 모노 24kHz → WebSocket 바이너리 프레임
//
// Azure VoiceLive Realtime API 는 PCM16 모노만 허용합니다.
// (devicesim/ms_ref/foundry_LV_Csharp_ref.cs 조사 결과 참조)
// =============================================================

namespace DeviceSim;

/// <summary>
/// PCM16 오디오 채널 처리 유틸리티.
/// 네이티브 C 스타일: 정적 메서드, 명시적 버퍼, 최소 추상화.
/// </summary>
public static class Downmix
{
    // -------------------------------------------------------
    // 인터리브드 스테레오 PCM16 → 모노 PCM16 다운믹스
    // 입력: L0 R0 L1 R1 ... (2 bytes per sample, little-endian)
    // 출력: (L+R)/2 모노 스트림
    // -------------------------------------------------------
    /// <summary>
    /// 인터리브드 스테레오 PCM16 바이트 배열을 모노로 다운믹스합니다.
    /// 각 스테레오 프레임(4 bytes)을 왼쪽·오른쪽 채널의 평균(2 bytes)으로 줄입니다.
    /// </summary>
    public static byte[] StereoToMono(byte[] stereo)
    {
        // 스테레오 프레임 수: 4 bytes per frame (L int16 + R int16)
        int frameCount = stereo.Length / 4;
        var mono = new byte[frameCount * 2];

        for (int i = 0; i < frameCount; i++)
        {
            // 리틀엔디안 int16 디코딩
            short left  = (short)(stereo[i * 4]     | (stereo[i * 4 + 1] << 8));
            short right = (short)(stereo[i * 4 + 2] | (stereo[i * 4 + 3] << 8));

            // 평균 계산 – int 로 중간 계산하여 오버플로 방지
            int avg = (left + right) / 2;
            short sample = (short)avg;

            // 리틀엔디안 int16 인코딩
            mono[i * 2]     = (byte)(sample & 0xFF);
            mono[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }

        return mono;
    }

    // -------------------------------------------------------
    // 홀수 바이트 수 처리: 마지막 불완전 스테레오 프레임 무시
    // (실제 MEMS 마이크 어레이에서 발생하지 않지만 안전 처리)
    // -------------------------------------------------------
    /// <summary>
    /// 스테레오 → 모노 다운믹스 (Span 오버로드, 제로 할당).
    /// </summary>
    public static int StereoToMono(ReadOnlySpan<byte> stereo, Span<byte> mono)
    {
        // 출력 버퍼 최소 크기 확인
        int frameCount = stereo.Length / 4;
        if (mono.Length < frameCount * 2)
            throw new ArgumentException("출력 버퍼가 너무 작습니다.", nameof(mono));

        for (int i = 0; i < frameCount; i++)
        {
            short left  = (short)(stereo[i * 4]     | (stereo[i * 4 + 1] << 8));
            short right = (short)(stereo[i * 4 + 2] | (stereo[i * 4 + 3] << 8));
            int avg = (left + right) / 2;
            short sample = (short)avg;
            mono[i * 2]     = (byte)(sample & 0xFF);
            mono[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }

        // 출력에 쓴 바이트 수 반환
        return frameCount * 2;
    }

    // -------------------------------------------------------
    // 모노 PCM16 청크 크기 계산 도우미
    // 채널 수에 따라 스테레오 버퍼를 모노 버퍼로 줄인 경우의 바이트 수 반환
    // -------------------------------------------------------
    /// <summary>
    /// 주어진 스테레오 바이트 수에 해당하는 모노 바이트 수를 반환합니다.
    /// </summary>
    public static int MonoBytesFromStereo(int stereoBytes) => stereoBytes / 2;

    // -------------------------------------------------------
    // 업링크 정규화: 소스 채널 수에 따라 모노로 변환
    //   Azure Voice Live 입력은 PCM16 모노만 허용하므로, 2채널 캡처는
    //   반드시 전송 전에 모노로 다운믹스해야 한다. (2채널을 그대로 보내면
    //   서버가 인터리브드 바이트를 모노로 오해석해 오디오가 깨진다.)
    // -------------------------------------------------------
    /// <summary>
    /// 소스 채널 수에 따라 PCM16 청크를 모노로 변환합니다.
    /// channels가 1이면 그대로 반환하고, 2이면 스테레오→모노 다운믹스합니다.
    /// </summary>
    public static byte[] ToMono(byte[] pcm, int channels)
        => channels >= 2 ? StereoToMono(pcm) : pcm;
}
