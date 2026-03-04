using System.Text;

namespace TabHistorian.Parsing;

/// <summary>
/// Reads values from a Chromium Pickle-encoded byte buffer.
/// All values are aligned to 4-byte boundaries.
/// </summary>
public class PickleReader
{
    private readonly byte[] _data;
    private int _pos;

    public PickleReader(byte[] payload)
    {
        _data = payload;
        // First 4 bytes are the pickle header (payload size) — skip it
        _pos = 4;
    }

    public bool HasData => _pos < _data.Length;
    public int Position => _pos;

    public int ReadInt32()
    {
        EnsureAvailable(4);
        int value = BitConverter.ToInt32(_data, _pos);
        _pos += 4;
        return value;
    }

    public long ReadInt64()
    {
        EnsureAvailable(8);
        long value = BitConverter.ToInt64(_data, _pos);
        _pos += 8;
        return value;
    }

    public bool ReadBool() => ReadInt32() != 0;

    public string ReadString()
    {
        int len = ReadInt32();
        if (len < 0 || len > _data.Length - _pos)
            return string.Empty;

        string value = Encoding.UTF8.GetString(_data, _pos, len);
        _pos += AlignTo4(len);
        return value;
    }

    public string ReadString16()
    {
        int charCount = ReadInt32();
        int byteLen = charCount * 2;
        if (byteLen < 0 || byteLen > _data.Length - _pos)
            return string.Empty;

        string value = Encoding.Unicode.GetString(_data, _pos, byteLen);
        _pos += AlignTo4(byteLen);
        return value;
    }

    public void Skip(int bytes)
    {
        _pos += bytes;
    }

    private void EnsureAvailable(int bytes)
    {
        if (_pos + bytes > _data.Length)
            throw new EndOfStreamException($"PickleReader: need {bytes} bytes at offset {_pos}, but only {_data.Length - _pos} remain.");
    }

    private static int AlignTo4(int n) => (n + 3) & ~3;
}
