namespace DimensionsModManager;

/// <summary>
/// Reader/injector for TT Games .DAT/.HDR archive pairs used by LEGO
/// Dimensions (X360/PS3). Handles two on-disk layouts:
///
/// CLASSIC (disc GAME*.DAT, little-endian, type -7):
///   [u32 sizeAfterField][s32 type][u32 fileCount]
///   fileCount x 16B { u32 offHi, u32 zsize, u32 size, 3B packed, u8 offLo }
///   [u32 nameCount] nameCount x (8|12)B name nodes
///   [u32 crcRelOffset] ... names ... crc table: fileCount x u32 FNV32.
///
/// NEW ".CC40TAD" (installed update PATCH*/DLC*, BIG-endian, type -11 on
/// Dimensions):
///   [u32 hdrSize][8B ".CC40TAD"][s32 type][u32 ver][u32 fileCount]
///   [u32 nameCount][u32 namesSize]
///   names blob (namesSize) ; [u32 dummy] nameCount x (10|12)B name nodes ;
///   [s32 type][u32 fileCount] ; file table ; crc table.
///   type<=-11 -> file entry 16B { u64 off, u32 zsize, u32 size(hi bit=packed) }.
///   crc table is u32 or u64 FNV depending on remaining header space + ver.
///
/// Injection appends raw (uncompressed) data to the .DAT and repoints the
/// entry - the engine detects compression from the data's leading 4-byte
/// signature, so signature-less raw data is read as-is. Undo needs only the
/// original .HDR bytes and the original .DAT length (truncate back).
/// </summary>
public class DatArchive
{
    private const uint Fnv32Offset = 0x811c9dc5;
    private const uint Fnv32Prime = 0x199933;
    private const ulong Fnv64Offset = 0xcbf29ce484222325UL;
    private const ulong Fnv64Prime = 1099511628211UL;

    public string HdrPath { get; }
    public string DatPath { get; }
    public int Type { get; private set; }
    public int FileCount { get; private set; }
    public bool IsNewFormat { get; private set; }

    private byte[] hdr_ = Array.Empty<byte>();

    // Layout, resolved in Parse().
    private long fileTableOffset_;
    private int fileEntrySize_;
    private long crcTableOffset_;
    private int version_;
    private int nameCount_;
    private long nameTreeOffset_;
    private Dictionary<string, int>? nameMap_;
    private bool crc64_;
    private bool crcWidthResolved_;
    private bool offset64_;      // 8-byte offset field (type <= -11)
    private bool packedInSize_;  // packed flag lives in the size high bit
    private bool shiftedOffset_; // classic-style offHi<<8 | offLo

    public DatArchive(string datPath)
    {
        DatPath = datPath;
        HdrPath = Path.ChangeExtension(datPath, ".HDR");
        Parse();
    }

    // --- endian helpers ---
    private uint U32LE(long o) => BitConverter.ToUInt32(hdr_, (int)o);
    private uint U32BE(long o) =>
        ((uint)hdr_[o] << 24) | ((uint)hdr_[o + 1] << 16) |
        ((uint)hdr_[o + 2] << 8) | hdr_[o + 3];
    private ulong U64BE(long o) => ((ulong)U32BE(o) << 32) | U32BE(o + 4);

    private void WriteU32BE(long o, uint v)
    {
        hdr_[o] = (byte)(v >> 24);
        hdr_[o + 1] = (byte)(v >> 16);
        hdr_[o + 2] = (byte)(v >> 8);
        hdr_[o + 3] = (byte)v;
    }

    private void WriteU64BE(long o, ulong v)
    {
        WriteU32BE(o, (uint)(v >> 32));
        WriteU32BE(o + 4, (uint)v);
    }

    // Embedded-header DATs ("install0_.dat" style): no .HDR file; the first
    // u32 (LE) encodes the header offset inside the DAT (negated+shifted when
    // the high bit is set, per ttgames.bms), the second u32 is its size.
    private bool embeddedHdr_;
    private long embeddedHdrOffset_;

    private void Parse()
    {
        if (File.Exists(HdrPath))
        {
            hdr_ = File.ReadAllBytes(HdrPath);
        }
        else
        {
            using var dat = new FileStream(DatPath, FileMode.Open, FileAccess.Read);
            byte[] head = new byte[8];
            dat.ReadExactly(head);
            long infoOff = BitConverter.ToUInt32(head, 0);
            if ((infoOff & 0x80000000) != 0)
            {
                infoOff = ((infoOff ^ 0xffffffff) << 8) + 0x100;
            }
            uint infoSize = BitConverter.ToUInt32(head, 4);
            if (infoOff + infoSize > dat.Length)
            {
                throw new InvalidDataException(
                    $"{Path.GetFileName(DatPath)}: no .HDR and no valid embedded header.");
            }
            embeddedHdr_ = true;
            embeddedHdrOffset_ = infoOff;
            // The embedded header body starts directly with [s32 type], while
            // an external .HDR starts with a leading [u32 size] field — pad 4
            // bytes so the classic parser's offsets line up (see ttgames.bms:
            // external skips a DUMMY long, embedded does not).
            hdr_ = new byte[infoSize + 4];
            dat.Position = infoOff;
            dat.ReadExactly(hdr_, 4, (int)infoSize);
        }
        IsNewFormat = hdr_.Length >= 12 &&
                      System.Text.Encoding.ASCII.GetString(hdr_, 4, 8) == ".CC40TAD";
        if (IsNewFormat)
        {
            ParseNew();
        }
        else
        {
            ParseClassic();
        }
    }

    private void ParseClassic()
    {
        Type = BitConverter.ToInt32(hdr_, 4);
        if (Type >= 0 || Type < -9)
        {
            throw new InvalidDataException(
                $"{Path.GetFileName(HdrPath)}: unsupported classic type {Type}.");
        }
        FileCount = BitConverter.ToInt32(hdr_, 8);
        fileTableOffset_ = 12;
        fileEntrySize_ = 16;
        offset64_ = false;
        packedInSize_ = false;
        shiftedOffset_ = true;

        long pos = fileTableOffset_ + (long)FileCount * 16;
        int nameCount = BitConverter.ToInt32(hdr_, (int)pos);
        pos += 4;
        int nameFieldSize = Type <= -5 ? 12 : 8;
        pos += (long)nameCount * nameFieldSize;
        uint crcRel = BitConverter.ToUInt32(hdr_, (int)pos);
        pos += 4;
        crcTableOffset_ = pos + crcRel;
        crc64_ = false;
        if (crcTableOffset_ + (long)FileCount * 4 > hdr_.Length)
        {
            throw new InvalidDataException(
                $"{Path.GetFileName(HdrPath)}: CRC table out of bounds.");
        }
    }

    private void ParseNew()
    {
        Type = (int)U32BE(12);
        int ver = (int)U32BE(16);
        FileCount = (int)U32BE(20);
        int names = (int)U32BE(24);
        long namesSize = U32BE(28);

        offset64_ = Type <= -11;
        packedInSize_ = Type <= -10;
        shiftedOffset_ = Type > -10;
        fileEntrySize_ = offset64_ ? 16 : (packedInSize_ ? 12 : 16);

        version_ = ver;
        nameCount_ = names;
        int nameEntrySize = ver >= 2 ? 12 : 10;
        long nameTreeStart = 32 + namesSize;
        nameTreeOffset_ = nameTreeStart + 4; // skip [u32 dummy]
        // [u32 dummy] + name nodes + [s32 type][u32 fileCount]
        fileTableOffset_ = nameTreeStart + 4 + (long)names * nameEntrySize + 8;

        crcTableOffset_ = fileTableOffset_ + (long)FileCount * fileEntrySize_;
        // CRC width (u32 vs u64) cannot be told from the layout alone —
        // PATCH.DAT has extra data after the table, so size checks lie.
        // It is resolved empirically in EnsureCrcWidth() by testing which
        // interpretation actually matches the archive's own file names.
        crc64_ = false;
        crcWidthResolved_ = false;
        if (crcTableOffset_ + (long)FileCount * 4 > hdr_.Length)
        {
            throw new InvalidDataException(
                $"{Path.GetFileName(HdrPath)}: CRC table out of bounds (new format).");
        }
    }

    /// <summary>
    /// Picks 32- vs 64-bit CRC entries by sampling file names from the name
    /// tree and counting matches under each interpretation.
    /// </summary>
    private void EnsureCrcWidth()
    {
        if (crcWidthResolved_ || !IsNewFormat)
        {
            crcWidthResolved_ = true;
            return;
        }
        nameMap_ ??= BuildNameMap();
        bool has64Room = version_ >= 2 &&
                         crcTableOffset_ + (long)FileCount * 8 <= hdr_.Length;
        int hits32 = 0, hits64 = 0, sampled = 0;
        var t32 = new HashSet<uint>();
        var t64 = new HashSet<ulong>();
        for (int i = 0; i < FileCount; i++)
        {
            t32.Add(U32BE(crcTableOffset_ + (long)i * 4));
            if (has64Room)
            {
                t64.Add(U64BE(crcTableOffset_ + (long)i * 8));
            }
        }
        foreach (string path in nameMap_.Keys)
        {
            if (++sampled > 200)
            {
                break;
            }
            if (t32.Contains(PathCrc32(path)))
            {
                hits32++;
            }
            if (has64Room && t64.Contains(PathCrc64(path)))
            {
                hits64++;
            }
        }
        crc64_ = hits64 > hits32;
        crcWidthResolved_ = true;
    }

    public static uint PathCrc32(string internalPath)
    {
        string s = internalPath.Replace('/', '\\').ToUpperInvariant();
        uint crc = Fnv32Offset;
        foreach (char c in s)
        {
            crc ^= (byte)c;
            crc *= Fnv32Prime;
        }
        return crc;
    }

    public static ulong PathCrc64(string internalPath)
    {
        string s = internalPath.Replace('/', '\\').ToUpperInvariant();
        ulong crc = Fnv64Offset;
        foreach (char c in s)
        {
            crc ^= (byte)c;
            crc *= Fnv64Prime;
        }
        return crc;
    }

    /// <summary>
    /// Builds path -> file-table-index from the new-format name tree. Needed
    /// because some entries (e.g. PATCH.DAT's TEXT.CSV overrides) have CRCs
    /// that do not match FNV(path), so CRC lookup misses them.
    /// Node: { u32 nameOff (0xffffffff = none), u16 folderId (parent node),
    /// u16 dummy if ver>=2, s16 someId, u16 fileId }. File nodes (fileId != 0)
    /// get sequential file-table indices in node order; folder nodes carry the
    /// path prefix for children referencing them via folderId. The very last
    /// node is always treated as a file (ttgames.bms workaround).
    /// </summary>
    private Dictionary<string, int> BuildNameMap()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var folderPaths = new string[nameCount_ + 1];
        Array.Fill(folderPaths, "");
        int entrySize = version_ >= 2 ? 12 : 10;
        int myId = 0;
        for (int i = 0; i < nameCount_; i++)
        {
            long n = nameTreeOffset_ + (long)i * entrySize;
            uint nameOff = U32BE(n);
            int folderId = (ushort)((hdr_[n + 4] << 8) | hdr_[n + 5]);
            long idPos = n + (version_ >= 2 ? 8 : 6);
            int fileId = (ushort)((hdr_[idPos + 2] << 8) | hdr_[idPos + 3]);
            if (nameOff == 0xffffffff)
            {
                continue;
            }
            long s = 32 + nameOff;
            int len = 0;
            while (s + len < hdr_.Length && hdr_[s + len] != 0)
            {
                len++;
            }
            string name = System.Text.Encoding.ASCII.GetString(hdr_, (int)s, len);
            string full = folderPaths[folderId].Length > 0
                ? folderPaths[folderId] + "\\" + name
                : name;
            if (fileId != 0 || i == nameCount_ - 1)
            {
                map[full] = myId++;
            }
            else
            {
                folderPaths[i] = full;
            }
        }
        return map;
    }

    /// <summary>
    /// Returns the entry index for an internal path, or -1. The CRC table is
    /// authoritative (CRC[i] belongs to file-table entry i); the name tree is
    /// only a last-resort fallback for the few entries whose stored CRC does
    /// not equal FNV(path).
    /// </summary>
    public int FindEntry(string internalPath)
    {
        EnsureCrcWidth();
        int byCrc = FindEntryByCrc(internalPath);
        if (byCrc >= 0 || !IsNewFormat)
        {
            return byCrc;
        }
        nameMap_ ??= BuildNameMap();
        string key = internalPath.Replace('/', '\\').TrimStart('\\');
        return nameMap_.TryGetValue(key, out int idx) ? idx : -1;
    }

    private int FindEntryByCrc(string internalPath)
    {
        if (crc64_)
        {
            ulong want = PathCrc64(internalPath);
            for (int i = 0; i < FileCount; i++)
            {
                if (U64BE(crcTableOffset_ + (long)i * 8) == want)
                {
                    return i;
                }
            }
            return -1;
        }
        uint want32 = PathCrc32(internalPath);
        for (int i = 0; i < FileCount; i++)
        {
            uint stored = IsNewFormat
                ? U32BE(crcTableOffset_ + (long)i * 4)
                : U32LE(crcTableOffset_ + (long)i * 4);
            if (stored == want32)
            {
                return i;
            }
        }
        return -1;
    }

    public (long offset, uint zsize, uint size) GetEntry(int index)
    {
        long e = fileTableOffset_ + (long)index * fileEntrySize_;
        if (IsNewFormat)
        {
            long offset;
            uint zsize, size;
            if (offset64_)
            {
                offset = (long)U64BE(e);
                zsize = U32BE(e + 8);
                size = U32BE(e + 12) & 0x7fffffff;
            }
            else if (packedInSize_)
            {
                offset = U32BE(e);
                zsize = U32BE(e + 4);
                size = U32BE(e + 8) & 0x7fffffff;
            }
            else
            {
                uint offHi = U32BE(e);
                zsize = U32BE(e + 4);
                size = U32BE(e + 8);
                byte offLo = hdr_[e + 15];
                offset = ((long)offHi << 8) | offLo;
            }
            return (offset, zsize, size);
        }
        // classic
        uint cOffHi = U32LE(e);
        uint cz = U32LE(e + 4);
        uint cs = U32LE(e + 8);
        byte cOffLo = hdr_[e + 15];
        long cOffset = Type == -1 ? cOffHi + cOffLo : ((long)cOffHi << 8) | cOffLo;
        return (cOffset, cz, cs);
    }

    /// <summary>
    /// Injects |data| into entry |index|, stored uncompressed. When the data
    /// fits in the entry's existing slot it is written IN PLACE (the archive
    /// layout stays untouched — the engine's streaming reader assumes entries
    /// stay sorted by offset, so appended+repointed entries can crash it).
    /// Larger data falls back to append+repoint. Call SaveHdr afterwards.
    /// Returns what was overwritten so the caller can back it up beforehand
    /// via GetEntry/ReadEntryData.
    /// </summary>
    public bool InjectFile(int index, byte[] data)
    {
        var (curOffset, curZsize, _) = GetEntry(index);
        if (data.Length <= curZsize)
        {
            using (var dat = new FileStream(DatPath, FileMode.Open, FileAccess.Write))
            {
                dat.Position = curOffset;
                dat.Write(data, 0, data.Length);
            }
            WriteEntrySizes(index, curOffset, (uint)data.Length);
            return true;
        }
        long newOffset;
        using (var dat = new FileStream(DatPath, FileMode.Append, FileAccess.Write))
        {
            newOffset = dat.Position;
            dat.Write(data, 0, data.Length);
        }
        WriteEntrySizes(index, newOffset, (uint)data.Length);
        return false;
    }

    /// <summary>Reads an entry's current raw bytes (zsize) from the .DAT.</summary>
    public byte[] ReadEntryData(int index)
    {
        var (offset, zsize, _) = GetEntry(index);
        using var dat = new FileStream(DatPath, FileMode.Open, FileAccess.Read);
        dat.Position = offset;
        byte[] buf = new byte[zsize];
        dat.ReadExactly(buf);
        return buf;
    }

    /// <summary>Rewrites entry |index| to point at |offset| with |len| bytes,
    /// stored uncompressed.</summary>
    private void WriteEntrySizes(int index, long offset, uint len)
    {
        long e = fileTableOffset_ + (long)index * fileEntrySize_;

        if (IsNewFormat)
        {
            if (offset64_)
            {
                WriteU64BE(e, (ulong)offset);
                WriteU32BE(e + 8, len);
                WriteU32BE(e + 12, len); // high bit 0 => uncompressed
            }
            else if (packedInSize_)
            {
                WriteU32BE(e, (uint)offset);
                WriteU32BE(e + 4, len);
                WriteU32BE(e + 8, len);
            }
            else
            {
                WriteU32BE(e, (uint)(offset >> 8));
                WriteU32BE(e + 4, len);
                WriteU32BE(e + 8, len);
                hdr_[e + 12] = 0; // packed
                hdr_[e + 13] = 0;
                hdr_[e + 14] = 0;
                hdr_[e + 15] = (byte)(offset & 0xFF);
            }
            return;
        }

        // classic (little-endian)
        if (Type == -1)
        {
            BitConverter.GetBytes((uint)offset).CopyTo(hdr_, (int)e);
            hdr_[e + 15] = 0;
        }
        else
        {
            BitConverter.GetBytes((uint)(offset >> 8)).CopyTo(hdr_, (int)e);
            hdr_[e + 15] = (byte)(offset & 0xFF);
        }
        BitConverter.GetBytes(len).CopyTo(hdr_, (int)e + 4);
        BitConverter.GetBytes(len).CopyTo(hdr_, (int)e + 8);
        hdr_[e + 12] = 0;
        hdr_[e + 13] = 0;
        hdr_[e + 14] = 0;
    }

    public void SaveHdr()
    {
        if (embeddedHdr_)
        {
            using var dat = new FileStream(DatPath, FileMode.Open, FileAccess.Write);
            dat.Position = embeddedHdrOffset_;
            dat.Write(hdr_, 4, hdr_.Length - 4);
            return;
        }
        File.WriteAllBytes(HdrPath, hdr_);
    }
}
