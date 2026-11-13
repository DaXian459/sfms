using SFMS.Domain;
using SFMS.Protocols;
using SFMS.Shared;

namespace SFMS.Collector;

/// <summary>点位在一次块读中的切片位置。</summary>
public sealed record TagSlice(Tag Tag, int SpanIndex, int ByteOffset, int BitOffset, bool BitArea);

/// <summary>
/// 地址聚合（计划书 3.4.3）：按 (从站, 区域, 块号) 分组、按偏移排序，
/// 间隙小于阈值且不超单次上限就合并成块读，读取后本地按偏移切分。
/// 40 个离散的 16 位寄存器通常 1~2 次请求即可读完。
/// </summary>
public static class SpanBuilder
{
    public static (List<ReadSpan> Spans, List<TagSlice> Slices) Build(
        IReadOnlyList<(Tag Tag, int Unit)> tags,
        ProtocolType protocol,
        int mergeGapBytes,
        int maxBlockBytes)
    {
        var spans = new List<ReadSpan>();
        var slices = new List<TagSlice>();

        foreach (var group in tags.GroupBy(x => (x.Unit, x.Tag.Area, x.Tag.AreaNo)))
        {
            bool bitArea = IsBitArea(protocol, group.Key.Area);
            int gapUnits = bitArea
                ? Math.Max(1, mergeGapBytes * 8)
                : protocol == ProtocolType.S7 ? mergeGapBytes : Math.Max(1, mergeGapBytes / 2);
            int maxUnits = bitArea
                ? DriverLimits.ModbusMaxReadBits
                : protocol == ProtocolType.S7 ? maxBlockBytes : DriverLimits.ModbusMaxReadRegisters;

            var ordered = group.Select(x => x.Tag)
                .OrderBy(t => t.ByteOffset)
                .ThenBy(t => t.BitOffset)
                .ToList();

            int curStart = -1, curEnd = -1;
            var pending = new List<Tag>();

            void Flush()
            {
                if (pending.Count == 0) return;
                int index = spans.Count;
                spans.Add(new ReadSpan(group.Key.Unit, group.Key.Area, group.Key.AreaNo, curStart, curEnd - curStart + 1));
                foreach (var t in pending)
                {
                    int byteOffset = bitArea
                        ? 0
                        : (t.ByteOffset - curStart) * (protocol == ProtocolType.S7 ? 1 : 2);
                    int bitOffset = bitArea ? t.ByteOffset - curStart : t.BitOffset;
                    slices.Add(new TagSlice(t, index, byteOffset, bitOffset, bitArea));
                }
                pending.Clear();
                curStart = curEnd = -1;
            }

            foreach (var t in ordered)
            {
                int start = t.ByteOffset;
                int end = bitArea
                    ? t.ByteOffset
                    : protocol == ProtocolType.S7
                        ? t.ByteOffset + ValueCodec.ByteLength(t.DataType, t.StrLen) - 1
                        : t.ByteOffset + ValueCodec.RegisterCount(t.DataType, t.StrLen) - 1;

                if (pending.Count == 0)
                {
                    curStart = start;
                    curEnd = end;
                    pending.Add(t);
                    continue;
                }

                bool mergeable = start - curEnd - 1 <= gapUnits
                                 && Math.Max(curEnd, end) - curStart + 1 <= maxUnits;
                if (mergeable)
                {
                    curEnd = Math.Max(curEnd, end);
                    pending.Add(t);
                }
                else
                {
                    Flush();
                    curStart = start;
                    curEnd = end;
                    pending.Add(t);
                }
            }
            Flush();
        }

        return (spans, slices);
    }

    public static bool IsBitArea(ProtocolType protocol, TagArea area) =>
        protocol != ProtocolType.S7 && area is TagArea.Coil or TagArea.DiscreteInput;

    /// <summary>从块读结果里取出该点位的原始字节（位区取 1 字节包装）。</summary>
    public static bool TryExtract(byte[]? spanData, TagSlice slice, TagArea area, int byteLength,
        out byte[] raw, out bool bitValue)
    {
        raw = Array.Empty<byte>();
        bitValue = false;
        if (spanData is null) return false;

        if (slice.BitArea)
        {
            int bitIndex = slice.BitOffset;
            int byteIndex = bitIndex / 8;
            if (byteIndex >= spanData.Length) return false;
            bitValue = ((spanData[byteIndex] >> (bitIndex % 8)) & 1) == 1;
            raw = [bitValue ? (byte)1 : (byte)0];
            return true;
        }

        if (slice.ByteOffset + byteLength > spanData.Length) return false;
        raw = spanData.AsSpan(slice.ByteOffset, byteLength).ToArray();

        // S7 位：读所在字节后按位号取位
        if ((area is TagArea.Db or TagArea.Memory or TagArea.Input or TagArea.Output) && slice.BitOffset > 0)
        {
            bitValue = ((raw[0] >> slice.BitOffset) & 1) == 1;
            raw = [bitValue ? (byte)1 : (byte)0];
        }
        return true;
    }
}
