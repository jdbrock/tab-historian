using System.Buffers.Binary;
using System.Text;
using Snappier;

namespace TabHistorian.Parsing;

/// <summary>
/// Minimal read-only LevelDB reader that handles Snappy-compressed .ldb table files
/// and .log WAL files. Used to read Chrome's Sync Data LevelDB.
/// </summary>
public static class LevelDbReader
{
    private const ulong TableMagicNumber = 0xdb4775248b80fb57;
    private const int BlockTrailerSize = 5; // 1 type + 4 crc
    private const int FooterSize = 48;
    private const int WalBlockSize = 32768;
    private const int WalHeaderSize = 7; // checksum(4) + length(2) + type(1)

    /// <summary>
    /// Reads all key-value pairs from a LevelDB directory where the key starts with the given prefix.
    /// Handles Snappy compression, deduplication by sequence number, and deletion markers.
    /// </summary>
    public static Dictionary<string, byte[]> ReadAllWithPrefix(string dbPath, string keyPrefix)
    {
        var entries = new Dictionary<string, (byte[] Value, ulong Sequence, bool IsDelete)>();

        foreach (var file in Directory.GetFiles(dbPath, "*.ldb").OrderBy(f => f))
        {
            try
            {
                foreach (var (userKey, value, seq, type) in ReadTableFile(File.ReadAllBytes(file)))
                {
                    var key = Encoding.UTF8.GetString(userKey);
                    if (!key.StartsWith(keyPrefix)) continue;
                    if (!entries.TryGetValue(key, out var existing) || seq > existing.Sequence)
                        entries[key] = (value, seq, type == 0);
                }
            }
            catch { /* skip unreadable table files */ }
        }

        // Read WAL (.log) files for most recent un-compacted writes
        foreach (var file in Directory.GetFiles(dbPath, "*.log").OrderBy(f => f))
        {
            try
            {
                foreach (var (userKey, value, seq, type) in ReadWalFile(File.ReadAllBytes(file)))
                {
                    var key = Encoding.UTF8.GetString(userKey);
                    if (!key.StartsWith(keyPrefix)) continue;
                    if (!entries.TryGetValue(key, out var existing) || seq > existing.Sequence)
                        entries[key] = (value ?? [], seq, type == 0);
                }
            }
            catch { /* skip corrupted WAL files */ }
        }

        return entries
            .Where(kv => !kv.Value.IsDelete)
            .ToDictionary(kv => kv.Key, kv => kv.Value.Value);
    }

    #region Table File (.ldb)

    private static IEnumerable<(byte[] UserKey, byte[] Value, ulong Sequence, byte Type)> ReadTableFile(byte[] data)
    {
        if (data.Length < FooterSize)
            yield break;

        ulong magic = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(data.Length - 8));
        if (magic != TableMagicNumber)
            yield break;

        int pos = data.Length - FooterSize;
        SkipBlockHandle(data, ref pos); // metaindex handle
        var indexHandle = DecodeBlockHandle(data, ref pos);

        var indexBlock = ReadBlock(data, indexHandle);
        if (indexBlock == null) yield break;

        foreach (var (_, handleBytes) in ParseBlockEntries(indexBlock))
        {
            int hPos = 0;
            var dataHandle = DecodeBlockHandle(handleBytes, ref hPos);
            var dataBlock = ReadBlock(data, dataHandle);
            if (dataBlock == null) continue;

            foreach (var (key, value) in ParseBlockEntries(dataBlock))
            {
                if (key.Length < 8) continue;
                var userKey = key[..^8];
                ulong seqAndType = BinaryPrimitives.ReadUInt64LittleEndian(key.AsSpan(key.Length - 8));
                yield return (userKey, value, seqAndType >> 8, (byte)(seqAndType & 0xFF));
            }
        }
    }

    private static byte[]? ReadBlock(byte[] data, BlockHandle handle)
    {
        long end = handle.Offset + handle.Size + BlockTrailerSize;
        if (handle.Offset < 0 || end > data.Length)
            return null;

        byte compressionType = data[handle.Offset + handle.Size];
        var raw = data.AsSpan((int)handle.Offset, (int)handle.Size);

        if (compressionType == 0)
            return raw.ToArray();

        if (compressionType == 1) // Snappy
        {
            try
            {
                int uncompressedLen = Snappy.GetUncompressedLength(raw);
                var output = new byte[uncompressedLen];
                Snappy.Decompress(raw, output);
                return output;
            }
            catch { return null; }
        }

        return null;
    }

    private static List<(byte[] Key, byte[] Value)> ParseBlockEntries(byte[] block)
    {
        var entries = new List<(byte[] Key, byte[] Value)>();
        if (block.Length < 4) return entries;

        int numRestarts = BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(block.Length - 4));
        int dataEnd = block.Length - 4 - numRestarts * 4;
        if (dataEnd < 0) return entries;

        int pos = 0;
        byte[] prevKey = [];

        while (pos < dataEnd)
        {
            int shared = DecodeVarint32(block, ref pos);
            int nonShared = DecodeVarint32(block, ref pos);
            int valueLen = DecodeVarint32(block, ref pos);
            if (pos + nonShared + valueLen > dataEnd) break;

            var key = new byte[shared + nonShared];
            if (shared > 0 && shared <= prevKey.Length)
                Array.Copy(prevKey, 0, key, 0, shared);
            Array.Copy(block, pos, key, shared, nonShared);
            pos += nonShared;

            var value = new byte[valueLen];
            Array.Copy(block, pos, value, 0, valueLen);
            pos += valueLen;

            entries.Add((key, value));
            prevKey = key;
        }

        return entries;
    }

    #endregion

    #region WAL File (.log)

    private static IEnumerable<(byte[] UserKey, byte[]? Value, ulong Sequence, byte Type)> ReadWalFile(byte[] data)
    {
        int pos = 0;
        var pending = new MemoryStream();
        bool inFragment = false;

        while (pos + WalHeaderSize <= data.Length)
        {
            // Skip block padding
            int blockOffset = pos % WalBlockSize;
            if (WalBlockSize - blockOffset < WalHeaderSize)
            {
                pos += WalBlockSize - blockOffset;
                continue;
            }

            ushort length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 4));
            byte recordType = data[pos + 6];
            pos += WalHeaderSize;

            if (length == 0 && recordType == 0) break;
            if (pos + length > data.Length) break;

            var fragment = data.AsSpan(pos, length);
            pos += length;

            switch (recordType)
            {
                case 1: // FULL
                    foreach (var entry in ParseWriteBatch(fragment.ToArray()))
                        yield return entry;
                    break;
                case 2: // FIRST
                    pending.SetLength(0);
                    pending.Write(fragment);
                    inFragment = true;
                    break;
                case 3: // MIDDLE
                    if (inFragment) pending.Write(fragment);
                    break;
                case 4: // LAST
                    if (inFragment)
                    {
                        pending.Write(fragment);
                        foreach (var entry in ParseWriteBatch(pending.ToArray()))
                            yield return entry;
                        inFragment = false;
                    }
                    break;
            }
        }
    }

    private static IEnumerable<(byte[] UserKey, byte[]? Value, ulong Sequence, byte Type)> ParseWriteBatch(byte[] data)
    {
        if (data.Length < 12) yield break;

        ulong sequence = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0));
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
        int pos = 12;

        for (uint i = 0; i < count && pos < data.Length; i++)
        {
            byte type = data[pos++];
            int keyLen = DecodeVarint32(data, ref pos);
            if (pos + keyLen > data.Length) break;
            var key = new byte[keyLen];
            Array.Copy(data, pos, key, 0, keyLen);
            pos += keyLen;

            byte[]? value = null;
            if (type == 1) // kTypeValue (Put)
            {
                int valLen = DecodeVarint32(data, ref pos);
                if (pos + valLen > data.Length) break;
                value = new byte[valLen];
                Array.Copy(data, pos, value, 0, valLen);
                pos += valLen;
            }

            yield return (key, value, sequence + i, type);
        }
    }

    #endregion

    #region Helpers

    private record struct BlockHandle(long Offset, long Size);

    private static BlockHandle DecodeBlockHandle(byte[] data, ref int pos)
    {
        long offset = DecodeVarint64(data, ref pos);
        long size = DecodeVarint64(data, ref pos);
        return new BlockHandle(offset, size);
    }

    private static void SkipBlockHandle(byte[] data, ref int pos)
    {
        DecodeVarint64(data, ref pos);
        DecodeVarint64(data, ref pos);
    }

    private static int DecodeVarint32(byte[] data, ref int pos)
        => (int)DecodeVarint64(data, ref pos);

    private static long DecodeVarint64(byte[] data, ref int pos)
    {
        long result = 0;
        int shift = 0;
        while (pos < data.Length)
        {
            byte b = data[pos++];
            result |= (long)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
        }
        return result;
    }

    #endregion
}
