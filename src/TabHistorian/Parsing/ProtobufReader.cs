namespace TabHistorian.Parsing;

/// <summary>
/// Minimal protobuf wire-format reader for decoding Chrome sync session data.
/// Supports varint, length-delimited, 64-bit, and 32-bit wire types.
/// </summary>
public class ProtobufReader
{
    private readonly byte[] _data;
    private int _pos;

    public ProtobufReader(byte[] data) : this(data, 0, data.Length) { }

    public ProtobufReader(byte[] data, int offset, int length)
    {
        _data = data;
        _pos = offset;
        End = offset + length;
    }

    public int End { get; }
    public bool HasData => _pos < End;

    public (int fieldNumber, int wireType) ReadTag()
    {
        uint tag = (uint)ReadVarint();
        return ((int)(tag >> 3), (int)(tag & 0x7));
    }

    public ulong ReadVarint()
    {
        ulong result = 0;
        int shift = 0;
        while (_pos < End)
        {
            byte b = _data[_pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return result;
            shift += 7;
            if (shift >= 64)
                throw new InvalidDataException("Varint too long");
        }
        throw new EndOfStreamException("Unexpected end reading varint");
    }

    public int ReadInt32() => (int)ReadVarint();
    public long ReadInt64() => (long)ReadVarint();
    public bool ReadBool() => ReadVarint() != 0;

    public string ReadString()
    {
        int length = (int)ReadVarint();
        if (length < 0 || _pos + length > End)
            throw new InvalidDataException($"Invalid string length: {length}");
        string value = System.Text.Encoding.UTF8.GetString(_data, _pos, length);
        _pos += length;
        return value;
    }

    public byte[] ReadBytes()
    {
        int length = (int)ReadVarint();
        if (length < 0 || _pos + length > End)
            throw new InvalidDataException($"Invalid bytes length: {length}");
        byte[] value = new byte[length];
        Array.Copy(_data, _pos, value, 0, length);
        _pos += length;
        return value;
    }

    /// <summary>
    /// Returns a sub-reader scoped to a length-delimited embedded message.
    /// </summary>
    public ProtobufReader ReadMessage()
    {
        int length = (int)ReadVarint();
        if (length < 0 || _pos + length > End)
            throw new InvalidDataException($"Invalid message length: {length}");
        var reader = new ProtobufReader(_data, _pos, length);
        _pos += length;
        return reader;
    }

    public void SkipField(int wireType)
    {
        switch (wireType)
        {
            case 0: ReadVarint(); break;
            case 1: _pos += 8; break;
            case 2:
                int len = (int)ReadVarint();
                _pos += len;
                break;
            case 3: // start group (deprecated but valid)
                while (HasData)
                {
                    var (_, wt) = ReadTag();
                    if (wt == 4) break; // end group
                    SkipField(wt);
                }
                break;
            case 4: break; // end group
            case 5: _pos += 4; break;
            default:
                throw new InvalidDataException($"Unknown wire type: {wireType}");
        }
        if (_pos > End)
            throw new EndOfStreamException("SkipField exceeded bounds");
    }
}
