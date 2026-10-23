namespace QuizMMO.Server.Networking;

/// <summary>
/// 세션당 하나인 수신 버퍼. 생성 시 한 번만 할당하고 ReceiveAsync 가 <see cref="WritePos"/> 위치부터 바로 쓴다.
/// 완전한 패킷을 소비한 뒤 <see cref="Clean"/> 으로 남은 바이트를 앞으로 당겨 다음 수신 공간을 확보한다.
/// 단일 스레드 전용: 한 세션의 수신 완료는 한 번에 하나만 처리된다 (수신 1개 규칙).
/// </summary>
public sealed class RecvBuffer
{
    private readonly byte[] _buffer;
    private int _readPos;
    private int _writePos;

    public RecvBuffer(int capacity)
    {
        _buffer = new byte[capacity];
    }

    public byte[] Buffer => _buffer;
    public int Capacity => _buffer.Length;
    public int ReadPos => _readPos;
    public int WritePos => _writePos;
    public int DataSize => _writePos - _readPos;
    public int FreeSize => _buffer.Length - _writePos;

    /// <summary>아직 소비하지 않은 데이터.</summary>
    public ReadOnlySpan<byte> Data => _buffer.AsSpan(_readPos, DataSize);

    /// <summary>n 바이트가 수신되었다. 남은 공간보다 크면 false (버퍼 오염 방지).</summary>
    public bool OnWrite(int n)
    {
        if (n < 0 || n > FreeSize) return false;
        _writePos += n;
        return true;
    }

    /// <summary>n 바이트를 소비했다. 쌓인 데이터보다 크면 false.</summary>
    public bool OnRead(int n)
    {
        if (n < 0 || n > DataSize) return false;
        _readPos += n;
        return true;
    }

    /// <summary>비어 있으면 포지션을 0 으로, 아니면 남은 데이터를 앞으로 당긴다.</summary>
    public void Clean()
    {
        var remaining = DataSize;
        if (remaining == 0)
        {
            _readPos = 0;
            _writePos = 0;
            return;
        }
        if (_readPos > 0)
        {
            System.Buffer.BlockCopy(_buffer, _readPos, _buffer, 0, remaining);
            _readPos = 0;
            _writePos = remaining;
        }
    }
}
