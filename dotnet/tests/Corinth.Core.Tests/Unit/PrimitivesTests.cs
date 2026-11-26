using Xunit;

namespace Corinth.Core.Tests;

public class PrimitivesTests
{
    [Theory]
    [InlineData(0UL, "00")]
    [InlineData(127UL, "7f")]
    [InlineData(128UL, "80 01")]
    [InlineData(500UL, "f4 03")]
    [InlineData(16384UL, "80 80 01")]
    [InlineData(ulong.MaxValue, "ff ff ff ff ff ff ff ff ff 01")]
    [InlineData(1UL << 63, "80 80 80 80 80 80 80 80 80 01")]
    [InlineData((1UL << 63) - 1, "ff ff ff ff ff ff ff ff 7f")]
    public void UVarint_writes_the_shortest_form_and_reads_it_back(ulong value, string hex)
    {
        Span<byte> buffer = stackalloc byte[10];
        var n = CcfPrimitives.WriteUVarint(buffer, value);
        Assert.Equal(Hex.Bytes(hex), buffer[..n].ToArray());
        Assert.Equal(n, CcfPrimitives.UVarintSize(value));

        var r = new CcfReader(Hex.Bytes(hex));
        Assert.Equal(value, r.ReadUVarint());
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void UVarint_with_an_11th_byte_is_VarintOverflow()
    {
        var bytes = Hex.Bytes("80 80 80 80 80 80 80 80 80 80 01");
        var ex = Assert.Throws<CcfFormatException>(() =>
        {
            var r = new CcfReader(bytes);
            return r.ReadUVarint();
        });
        Assert.Equal(CcfErrorCode.VarintOverflow, ex.Code);
    }

    [Fact]
    public void UVarint_above_2_pow_64_minus_1_is_VarintOverflow()
    {
        // 10th byte 0x02 would be bit 64.
        var bytes = Hex.Bytes("ff ff ff ff ff ff ff ff ff 02");
        var ex = Assert.Throws<CcfFormatException>(() =>
        {
            var r = new CcfReader(bytes);
            return r.ReadUVarint();
        });
        Assert.Equal(CcfErrorCode.VarintOverflow, ex.Code);
    }

    [Theory]
    [InlineData(0L, 0UL)]
    [InlineData(-1L, 1UL)]
    [InlineData(1L, 2UL)]
    [InlineData(1001L, 2002UL)]
    [InlineData(long.MaxValue, ulong.MaxValue - 1)]
    [InlineData(long.MinValue, ulong.MaxValue)]
    [InlineData(-64L, 127UL)]
    public void ZigZag_matches_the_spec_and_round_trips(long value, ulong zigzag)
    {
        Assert.Equal(zigzag, CcfPrimitives.ZigZag(value));
        Assert.Equal(value, CcfPrimitives.UnZigZag(zigzag));
    }

    [Fact]
    public void ZigZag_of_1001_is_d2_0f()
    {
        Span<byte> buffer = stackalloc byte[10];
        var n = CcfPrimitives.WriteUVarint(buffer, CcfPrimitives.ZigZag(1001));
        Assert.Equal("d20f", Hex.Of(buffer[..n]));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    [InlineData(256, 8)]
    [InlineData(257, 9)]
    [InlineData(65_536, 16)]
    public void Enum_bit_width_is_the_smallest_w_of_at_least_one(int count, int width) =>
        Assert.Equal(width, CcfPrimitives.EnumBitWidth(count));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(13)]
    [InlineData(16)]
    public void Bit_packing_is_LSB_first_and_round_trips(int width)
    {
        var count = 37;
        var max = (1 << width) - 1;
        var values = Enumerable.Range(0, count).Select(i => (i * 2654435761u >> 7) % (max + 1u)).Select(v => (int)v).ToArray();
        var packed = new byte[CcfPrimitives.PackedByteCount(count, width)];
        for (var i = 0; i < count; i++)
        {
            CcfPrimitives.WritePacked(packed, i, width, values[i]);
        }

        for (var i = 0; i < count; i++)
        {
            Assert.Equal(values[i], CcfPrimitives.ReadPacked(packed, i, width));
        }

        // Unused high bits of the last byte are zero.
        var usedBits = count * width;
        if (usedBits % 8 != 0)
        {
            Assert.Equal(0, packed[^1] >> (usedBits % 8));
        }
    }

    [Fact]
    public void Bit_packing_layout_matches_SPEC_5_3()
    {
        // 3-bit values 1,2,7: stream bits 0-2 = 1, 3-5 = 2, 6-8 = 7 → byte0 = 0b11_010_001, byte1 = 0b0000_0001
        var packed = new byte[2];
        CcfPrimitives.WritePacked(packed, 0, 3, 1);
        CcfPrimitives.WritePacked(packed, 1, 3, 2);
        CcfPrimitives.WritePacked(packed, 2, 3, 7);
        Assert.Equal("d101", Hex.Of(packed));
    }

    [Fact]
    public void PackedByteCount_rounds_up()
    {
        Assert.Equal(63, CcfPrimitives.PackedByteCount(500, 1));
        Assert.Equal(0, CcfPrimitives.PackedByteCount(0, 7));
        Assert.Equal(1, CcfPrimitives.PackedByteCount(1, 16) - 1);
    }

    [Fact]
    public void Decimal_scaled_conversion_is_exact()
    {
        Assert.Equal(361.48m, CcfConvert.FromScaled(36148, 2));
        Assert.Equal("0.0000", CcfConvert.FromScaled(0, 4).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(-12.3456m, CcfConvert.FromScaled(-123456, 4));
        Assert.Equal("-9.223372036854775808", CcfConvert.FromScaled(long.MinValue, 18).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(CcfConvert.TryToScaled(99999999.9999m, 4, out var s));
        Assert.Equal(999999999999L, s);
        Assert.False(CcfConvert.TryToScaled(1.234m, 2, out _));
        Assert.True(CcfConvert.TryToScaled(1.230m, 2, out var t) && t == 123);
        Assert.False(CcfConvert.TryToScaled(decimal.MaxValue, 18, out _));
        Assert.False(CcfConvert.TryToScaled(10_000_000_000m, 18, out _)); // 1e28 > int64
    }

    [Theory]
    [InlineData(0L, "1970-01-01T00:00:00.000Z")]
    [InlineData(-1L, "1969-12-31T23:59:59.999Z")]
    [InlineData(1L, "1970-01-01T00:00:00.001Z")]
    public void Epoch_milliseconds_round_trip(long ms, string iso)
    {
        var dt = CcfConvert.FromEpochMs(ms);
        Assert.Equal(DateTimeKind.Utc, dt.Kind);
        Assert.Equal(iso, dt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(ms, CcfConvert.ToEpochMs(dt));
    }

    [Fact]
    public void Datetime_to_milliseconds_floors_sub_millisecond_ticks()
    {
        var before = new DateTime(1969, 12, 31, 23, 59, 59, DateTimeKind.Utc).AddTicks(9_999_999); // .9999999
        Assert.Equal(-1, CcfConvert.ToEpochMs(before)); // floor of -0.0000001 s → -1 ms
        var after = DateTime.UnixEpoch.AddTicks(9_999); // 0.9999 ms
        Assert.Equal(0, CcfConvert.ToEpochMs(after));
    }
}
