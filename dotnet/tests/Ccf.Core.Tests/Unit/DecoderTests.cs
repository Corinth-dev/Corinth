using Ccf.Core.Tests.Conformance;
using Xunit;

namespace Ccf.Core.Tests;

/// <summary>Decoder behaviour including the SPEC §14 clarifications that the golden vectors do not cover.</summary>
public class DecoderTests
{
    private static CcfSchema S(string fieldsJson) => CcfSchema.Parse($$"""{"name":"T","fields":[{{fieldsJson}}]}""");

    private static CcfErrorCode CodeOf(Func<object?> action) => Assert.Throws<CcfFormatException>(action).Code;

    private static CcfErrorCode DecodeCode(CcfSchema schema, byte[] payload, CcfLimits? limits = null) =>
        CodeOf(() => CcfDecoder.DecodeValue(payload, schema, limits));

    private static readonly CcfSchema OneInt32 = S("""{"name":"a","type":"int32"}""");

    // ---- header ----------------------------------------------------------------------------------------

    [Fact]
    public void Empty_payload_and_one_byte_payload_are_Truncated()
    {
        Assert.Equal(CcfErrorCode.Truncated, DecodeCode(OneInt32, Array.Empty<byte>()));
        Assert.Equal(CcfErrorCode.Truncated, DecodeCode(OneInt32, new byte[] { 0x43 }));
    }

    [Fact]
    public void Header_truncated_after_version_flags_id_or_row_count_is_Truncated()
    {
        var full = new PayloadBuilder(OneInt32, 0).Block(0).ToArray();
        for (var cut = 2; cut < 9; cut++)
        {
            Assert.Equal(CcfErrorCode.Truncated, DecodeCode(OneInt32, full[..cut]));
        }
    }

    [Theory]
    [InlineData(0x04)]
    [InlineData(0x08)]
    [InlineData(0x80)]
    [InlineData(0xFC)]
    public void Any_reserved_flag_bit_is_ReservedFlags(int flags) =>
        Assert.Equal(CcfErrorCode.ReservedFlags, DecodeCode(OneInt32, new PayloadBuilder(OneInt32, 0, (byte)flags).Block(0).ToArray()));

    [Fact]
    public void Single_object_flag_with_row_count_other_than_one_is_SingleObjectRowCount()
    {
        Assert.Equal(CcfErrorCode.SingleObjectRowCount, DecodeCode(OneInt32, new PayloadBuilder(OneInt32, 0, 2).Block(0).ToArray()));
        Assert.Equal(CcfErrorCode.SingleObjectRowCount, DecodeCode(OneInt32, new PayloadBuilder(OneInt32, 2, 2).Block(0, 0, 0).ToArray()));
    }

    [Fact]
    public void Single_object_decodes_to_an_object_not_an_array()
    {
        var payload = new PayloadBuilder(OneInt32, 1, 2).Block(0, 0x04).ToArray(); // zigzag(2)=4 → 2
        var o = Assert.IsType<CcfObject>(CcfDecoder.DecodeValue(payload, OneInt32));
        Assert.Equal(2, o["a"]);
    }

    [Fact]
    public void Header_checks_run_in_spec_order_magic_version_flags_schema()
    {
        var bad = new byte[] { 0x58, 0x43, 2, 0x80, 0, 0, 0, 0, 0, 0 };
        Assert.Equal(CcfErrorCode.BadMagic, DecodeCode(OneInt32, bad));
        bad[0] = 0x43;
        Assert.Equal(CcfErrorCode.UnsupportedVersion, DecodeCode(OneInt32, bad));
        bad[2] = 1;
        Assert.Equal(CcfErrorCode.ReservedFlags, DecodeCode(OneInt32, bad));
        bad[3] = 0;
        Assert.Equal(CcfErrorCode.SchemaMismatch, DecodeCode(OneInt32, bad));
    }

    [Fact]
    public void PeekSchemaId_returns_the_hex_id_and_validates_the_header()
    {
        var payload = new PayloadBuilder(OneInt32, 0).Block(0).ToArray();
        Assert.Equal(OneInt32.IdHex, CcfDecoder.PeekSchemaId(payload));
        Assert.Equal(CcfErrorCode.BadMagic, CodeOf(() => CcfDecoder.PeekSchemaId(new byte[] { 1, 2, 1, 0, 0, 0, 0, 0, 0 })));
        Assert.Equal(CcfErrorCode.Truncated, CodeOf(() => CcfDecoder.PeekSchemaId(payload[..6])));
    }

    // ---- SPEC §14.1 ------------------------------------------------------------------------------------

    [Fact]
    public void Block_length_larger_than_the_remaining_payload_is_Truncated()
    {
        var payload = new PayloadBuilder(OneInt32, 1).BlockWithLength(0, 5, 0x04).ToArray();
        Assert.Equal(CcfErrorCode.Truncated, DecodeCode(OneInt32, payload));
    }

    [Fact]
    public void Block_missing_its_mode_or_length_byte_is_Truncated()
    {
        Assert.Equal(CcfErrorCode.Truncated, DecodeCode(OneInt32, new PayloadBuilder(OneInt32, 1).ToArray()));
        Assert.Equal(CcfErrorCode.Truncated, DecodeCode(OneInt32, new PayloadBuilder(OneInt32, 1).Raw(0).ToArray()));
    }

    [Fact]
    public void Reading_past_the_end_of_the_block_body_is_LengthOverrun_even_when_more_payload_follows()
    {
        // int32 column with 2 rows: block body holds one value; the second read would run into the next
        // column's bytes. The payload has enough bytes, the block does not.
        var schema = S("""{"name":"a","type":"int32"},{"name":"b","type":"int32"}""");
        var payload = new PayloadBuilder(schema, 2).Block(0, 0x02).Block(0, 0x02, 0x02).ToArray();
        Assert.Equal(CcfErrorCode.LengthOverrun, DecodeCode(schema, payload));
    }

    [Fact]
    public void A_varint_that_runs_off_the_end_of_its_block_is_LengthOverrun()
    {
        var payload = new PayloadBuilder(OneInt32, 1).Block(0, 0x80).Raw(0x01).ToArray();
        Assert.Equal(CcfErrorCode.LengthOverrun, DecodeCode(OneInt32, payload));
    }

    [Fact]
    public void Nested_block_length_larger_than_the_enclosing_block_is_LengthOverrun()
    {
        var schema = S("""{"name":"o","type":"object","fields":[{"name":"x","type":"int32"}]}""");
        // Outer block has L=3: inner mode, inner length 9 (too long), one filler byte.
        var payload = new PayloadBuilder(schema, 1).Block(0, 0x00, 0x09, 0x00).Raw(0, 0, 0, 0, 0, 0, 0, 0, 0, 0).ToArray();
        Assert.Equal(CcfErrorCode.LengthOverrun, DecodeCode(schema, payload));
    }

    [Fact]
    public void Nested_block_missing_its_header_inside_the_enclosing_block_is_LengthOverrun()
    {
        var schema = S("""{"name":"o","type":"object","fields":[{"name":"x","type":"int32"}]}""");
        var payload = new PayloadBuilder(schema, 1).Block(0).Raw(0x00, 0x00).ToArray(); // outer body empty → no inner header
        Assert.Equal(CcfErrorCode.LengthOverrun, DecodeCode(schema, payload));
    }

    [Fact]
    public void BlockLengthMismatch_only_when_a_body_ends_with_bytes_left_over()
    {
        var payload = new PayloadBuilder(OneInt32, 1).Block(0, 0x04, 0x00).ToArray(); // one extra body byte
        Assert.Equal(CcfErrorCode.BlockLengthMismatch, DecodeCode(OneInt32, payload));
    }

    [Fact]
    public void Leftover_bytes_in_a_nested_block_are_BlockLengthMismatch_not_a_parent_error()
    {
        var schema = S("""{"name":"o","type":"object","fields":[{"name":"x","type":"int32"}]}""");
        var inner = new byte[] { 0x00, 0x02, 0x04, 0x00 }; // mode, L=2, value 2, one stray byte
        var payload = new PayloadBuilder(schema, 1).Block(0, inner).ToArray();
        Assert.Equal(CcfErrorCode.BlockLengthMismatch, DecodeCode(schema, payload));
    }

    [Fact]
    public void Bytes_after_the_root_table_are_TrailingData()
    {
        var payload = new PayloadBuilder(OneInt32, 1).Block(0, 0x04).Raw(0).ToArray();
        Assert.Equal(CcfErrorCode.TrailingData, DecodeCode(OneInt32, payload));
    }

    // ---- modes -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(2)]
    [InlineData(0xFF)]
    public void Unknown_mode_byte_is_InvalidMode(int mode) =>
        Assert.Equal(CcfErrorCode.InvalidMode, DecodeCode(OneInt32, new PayloadBuilder(OneInt32, 0).Block((byte)mode).ToArray()));

    [Fact]
    public void Dictionary_mode_on_a_non_string_field_is_InvalidMode() =>
        Assert.Equal(CcfErrorCode.InvalidMode, DecodeCode(OneInt32, new PayloadBuilder(OneInt32, 0).Block(1).ToArray()));

    // ---- SPEC §14.2 delta ------------------------------------------------------------------------------

    private static byte[] ZZ(long v) => PayloadBuilder.UV(CcfPrimitives.ZigZag(v));

    [Fact]
    public void Int64_delta_overflow_is_ValueOutOfRange()
    {
        var schema = S("""{"name":"a","type":"int64","delta":true}""");
        var body = PayloadBuilder.Cat(ZZ(long.MaxValue), ZZ(1));
        Assert.Equal(CcfErrorCode.ValueOutOfRange, DecodeCode(schema, new PayloadBuilder(schema, 2).Block(0, body).ToArray()));
        body = PayloadBuilder.Cat(ZZ(long.MinValue), ZZ(-1));
        Assert.Equal(CcfErrorCode.ValueOutOfRange, DecodeCode(schema, new PayloadBuilder(schema, 2).Block(0, body).ToArray()));
    }

    [Fact]
    public void Int64_delta_reaching_the_extremes_exactly_is_accepted()
    {
        var schema = S("""{"name":"a","type":"int64","delta":true}""");

        // MaxValue, then -MaxValue (→ 0), then MinValue (→ MinValue): every running value fits.
        var body = PayloadBuilder.Cat(ZZ(long.MaxValue), ZZ(-long.MaxValue), ZZ(long.MinValue));
        var rows = (object?[])CcfDecoder.DecodeValue(new PayloadBuilder(schema, 3).Block(0, body).ToArray(), schema);
        Assert.Equal(new object[] { long.MaxValue, 0L, long.MinValue }, rows.Select(r => ((CcfObject)r!)["a"]).ToArray());

        // MinValue, +MaxValue (→ -1), +MinValue overflows.
        body = PayloadBuilder.Cat(ZZ(long.MinValue), ZZ(long.MaxValue), ZZ(long.MinValue));
        Assert.Equal(CcfErrorCode.ValueOutOfRange, DecodeCode(schema, new PayloadBuilder(schema, 3).Block(0, body).ToArray()));
    }

    [Fact]
    public void Int32_delta_leaving_the_int32_range_is_ValueOutOfRange()
    {
        var schema = S("""{"name":"a","type":"int32","delta":true}""");
        var body = PayloadBuilder.Cat(ZZ(int.MaxValue), ZZ(1));
        Assert.Equal(CcfErrorCode.ValueOutOfRange, DecodeCode(schema, new PayloadBuilder(schema, 2).Block(0, body).ToArray()));
        body = PayloadBuilder.Cat(ZZ(int.MinValue), ZZ(-1));
        Assert.Equal(CcfErrorCode.ValueOutOfRange, DecodeCode(schema, new PayloadBuilder(schema, 2).Block(0, body).ToArray()));
    }

    [Fact]
    public void Uint32_delta_going_negative_or_above_range_is_ValueOutOfRange()
    {
        var schema = S("""{"name":"a","type":"uint32","delta":true}""");
        Assert.Equal(CcfErrorCode.ValueOutOfRange, DecodeCode(schema, new PayloadBuilder(schema, 2).Block(0, PayloadBuilder.Cat(ZZ(1), ZZ(-2))).ToArray()));
        Assert.Equal(CcfErrorCode.ValueOutOfRange, DecodeCode(schema, new PayloadBuilder(schema, 2).Block(0, PayloadBuilder.Cat(ZZ(uint.MaxValue), ZZ(1))).ToArray()));
        var ok = new PayloadBuilder(schema, 2).Block(0, PayloadBuilder.Cat(ZZ(uint.MaxValue), ZZ(-(long)uint.MaxValue))).ToArray();
        Assert.Equal(new object[] { uint.MaxValue, 0u }, ((object?[])CcfDecoder.DecodeValue(ok, schema)).Select(r => ((CcfObject)r!)["a"]).ToArray());
    }

    [Fact]
    public void Uint64_delta_overflow_and_underflow_are_ValueOutOfRange_and_the_full_range_is_reachable()
    {
        var schema = S("""{"name":"a","type":"uint64","delta":true}""");
        Assert.Equal(CcfErrorCode.ValueOutOfRange, DecodeCode(schema, new PayloadBuilder(schema, 1).Block(0, ZZ(-1)).ToArray()));
        // 2^63-1 + (2^63-1) = 2^64-2 fits; +2 more overflows.
        var body = PayloadBuilder.Cat(ZZ(long.MaxValue), ZZ(long.MaxValue));
        var rows = (object?[])CcfDecoder.DecodeValue(new PayloadBuilder(schema, 2).Block(0, body).ToArray(), schema);
        Assert.Equal(ulong.MaxValue - 1, ((CcfObject)rows[1]!)["a"]);
        body = PayloadBuilder.Cat(ZZ(long.MaxValue), ZZ(long.MaxValue), ZZ(2));
        Assert.Equal(CcfErrorCode.ValueOutOfRange, DecodeCode(schema, new PayloadBuilder(schema, 3).Block(0, body).ToArray()));
    }

    [Fact]
    public void Datetime_delta_and_decimal_delta_use_int64_checked_arithmetic()
    {
        var dt = S("""{"name":"a","type":"datetime","delta":true}""");
        Assert.Equal(CcfErrorCode.ValueOutOfRange, DecodeCode(dt, new PayloadBuilder(dt, 2).Block(0, PayloadBuilder.Cat(ZZ(long.MaxValue), ZZ(1))).ToArray()));
        var dec = S("""{"name":"a","type":"decimal","scale":2,"delta":true}""");
        Assert.Equal(CcfErrorCode.ValueOutOfRange, DecodeCode(dec, new PayloadBuilder(dec, 2).Block(0, PayloadBuilder.Cat(ZZ(long.MaxValue), ZZ(1))).ToArray()));
    }

    [Fact]
    public void Nulls_are_skipped_in_delta_chains()
    {
        var schema = S("""{"name":"a","type":"int64","nullable":true,"delta":true}""");
        // rows: 10, null, 13 → bitmap 0b101, diffs +10, +3
        var payload = new PayloadBuilder(schema, 3).Block(0, PayloadBuilder.Cat(new byte[] { 0b101 }, ZZ(10), ZZ(3))).ToArray();
        var rows = (object?[])CcfDecoder.DecodeValue(payload, schema);
        Assert.Equal(new object?[] { 10L, null, 13L }, rows.Select(r => ((CcfObject)r!)["a"]).ToArray());
    }

    [Fact]
    public void Int32_and_uint32_plain_values_outside_their_range_are_ValueOutOfRange()
    {
        var i32 = S("""{"name":"a","type":"int32"}""");
        Assert.Equal(CcfErrorCode.ValueOutOfRange, DecodeCode(i32, new PayloadBuilder(i32, 1).Block(0, ZZ(int.MaxValue + 1L)).ToArray()));
        Assert.Equal(CcfErrorCode.ValueOutOfRange, DecodeCode(i32, new PayloadBuilder(i32, 1).Block(0, ZZ(int.MinValue - 1L)).ToArray()));
        var u32 = S("""{"name":"a","type":"uint32"}""");
        Assert.Equal(CcfErrorCode.ValueOutOfRange, DecodeCode(u32, new PayloadBuilder(u32, 1).Block(0, PayloadBuilder.UV(uint.MaxValue + 1UL)).ToArray()));
        Assert.Equal(CcfErrorCode.ValueOutOfRange, DecodeCode(u32, new PayloadBuilder(u32, 1).Block(0, PayloadBuilder.UV(ulong.MaxValue)).ToArray()));
    }

    // ---- SPEC §14.5 dictionary -------------------------------------------------------------------------

    private static readonly CcfSchema OneString = S("""{"name":"s","type":"string"}""");
    private static readonly CcfSchema NullableString = S("""{"name":"s","type":"string","nullable":true}""");

    [Fact]
    public void Dictionary_with_k_0_and_p_0_is_valid()
    {
        var payload = new PayloadBuilder(OneString, 0).Block(1, 0x00).ToArray();
        Assert.Empty((object?[])CcfDecoder.DecodeValue(payload, OneString));

        // all rows null in a nullable column: p = 0, k = 0, bitmap only
        var allNull = new PayloadBuilder(NullableString, 3).Block(1, 0x00, 0x00).ToArray();
        var rows = (object?[])CcfDecoder.DecodeValue(allNull, NullableString);
        Assert.All(rows, r => Assert.Null(((CcfObject)r!)["s"]));
    }

    [Fact]
    public void Dictionary_with_k_0_and_p_greater_than_0_fails_IndexOutOfRange_on_the_first_index()
    {
        var payload = new PayloadBuilder(OneString, 2).Block(1, 0x00, 0x00, 0x00).ToArray();
        Assert.Equal(CcfErrorCode.IndexOutOfRange, DecodeCode(OneString, payload));
    }

    [Fact]
    public void Dictionary_index_equal_to_k_is_IndexOutOfRange()
    {
        var entry = new byte[] { 0x01, (byte)'a' };
        var payload = new PayloadBuilder(OneString, 1).Block(1, PayloadBuilder.Cat(new byte[] { 0x01 }, entry, new byte[] { 0x01 })).ToArray();
        Assert.Equal(CcfErrorCode.IndexOutOfRange, DecodeCode(OneString, payload));
    }

    [Fact]
    public void Decoder_validates_dictionary_structure_only_not_the_encoder_rule()
    {
        // k = p = 2 (all distinct) and k = 1, p = 1: an encoder would use plain mode here, but both decode.
        var body = PayloadBuilder.Cat(new byte[] { 0x02, 0x01, (byte)'x', 0x01, (byte)'y', 0x01, 0x00 });
        var rows = (object?[])CcfDecoder.DecodeValue(new PayloadBuilder(OneString, 2).Block(1, body).ToArray(), OneString);
        Assert.Equal(new object?[] { "y", "x" }, rows.Select(r => ((CcfObject)r!)["s"]).ToArray());

        // duplicate dictionary entries are structurally fine too
        body = PayloadBuilder.Cat(new byte[] { 0x02, 0x01, (byte)'x', 0x01, (byte)'x', 0x00, 0x01 });
        rows = (object?[])CcfDecoder.DecodeValue(new PayloadBuilder(OneString, 2).Block(1, body).ToArray(), OneString);
        Assert.Equal(new object?[] { "x", "x" }, rows.Select(r => ((CcfObject)r!)["s"]).ToArray());
    }

    [Fact]
    public void Dictionary_larger_than_65536_entries_is_LimitExceeded()
    {
        var payload = new PayloadBuilder(OneString, 1).Block(1, PayloadBuilder.UV(65_537)).ToArray();
        Assert.Equal(CcfErrorCode.LimitExceeded, DecodeCode(OneString, payload));
    }

    [Fact]
    public void Dictionary_with_257_entries_uses_two_byte_little_endian_indexes()
    {
        var entries = Enumerable.Range(0, 257).Select(i => PayloadBuilder.Cat(new byte[] { 0x01 }, new[] { (byte)('a' + i % 26) })).ToArray();
        var body = PayloadBuilder.Cat(PayloadBuilder.UV(257), PayloadBuilder.Cat(entries), new byte[] { 0x00, 0x01 });
        var rows = (object?[])CcfDecoder.DecodeValue(new PayloadBuilder(OneString, 1).Block(1, body).ToArray(), OneString);
        Assert.Equal("w", ((CcfObject)rows[0]!)["s"]); // index 0x0100 = 256 → 256 % 26 = 22 → 'w'
    }

    // ---- SPEC §14.6 limits -----------------------------------------------------------------------------

    [Fact]
    public void Row_limit_applies_to_each_table_including_list_child_tables()
    {
        var schema = S("""{"name":"l","type":"list","fields":[{"name":"$value","type":"int32"}]}""");

        // One root row holding a list of three elements: counts [3], total 3, then the child column block.
        var childBlock = new byte[] { 0x00, 0x03, 0x02, 0x02, 0x02 }; // three values of zigzag(1) = 2
        var body = PayloadBuilder.Cat(PayloadBuilder.UV(3), PayloadBuilder.UV(3), childBlock);
        var payload = new PayloadBuilder(schema, 1).Block(0, body).ToArray();

        Assert.Equal(CcfErrorCode.LimitExceeded, DecodeCode(schema, payload, new CcfLimits { MaxRows = 2 }));
        var rows = (object?[])CcfDecoder.DecodeValue(payload, schema, new CcfLimits { MaxRows = 3 });
        Assert.Equal(3, ((object?[])((CcfObject)rows[0]!)["l"]!).Length);
    }

    [Fact]
    public void Root_row_count_above_the_limit_is_LimitExceeded_before_any_allocation()
    {
        var payload = new PayloadBuilder(OneInt32, 100_001).Block(0).ToArray();
        Assert.Equal(CcfErrorCode.LimitExceeded, DecodeCode(OneInt32, payload));
        // Raised limit: the (empty) body is now simply too short.
        Assert.Equal(CcfErrorCode.LengthOverrun, DecodeCode(OneInt32, payload, new CcfLimits { MaxRows = 200_000 }));
    }

    [Fact]
    public void Payload_larger_than_the_payload_limit_is_LimitExceeded()
    {
        var payload = new PayloadBuilder(OneInt32, 1).Block(0, 0x04).ToArray();
        Assert.Equal(CcfErrorCode.LimitExceeded, DecodeCode(OneInt32, payload, new CcfLimits { MaxPayloadBytes = payload.Length - 1 }));
        Assert.NotNull(CcfDecoder.DecodeValue(payload, OneInt32, new CcfLimits { MaxPayloadBytes = payload.Length }));
    }

    [Fact]
    public void String_longer_than_the_string_limit_is_LimitExceeded()
    {
        var payload = new PayloadBuilder(OneString, 1).Block(0, PayloadBuilder.Cat(PayloadBuilder.UV(5), "hello"u8.ToArray())).ToArray();
        Assert.Equal(CcfErrorCode.LimitExceeded, DecodeCode(OneString, payload, new CcfLimits { MaxStringBytes = 4 }));
        Assert.NotNull(CcfDecoder.DecodeValue(payload, OneString, new CcfLimits { MaxStringBytes = 5 }));
    }

    [Fact]
    public void Dictionary_entry_longer_than_the_string_limit_is_LimitExceeded()
    {
        var body = PayloadBuilder.Cat(new byte[] { 0x01 }, PayloadBuilder.UV(5), "hello"u8.ToArray(), new byte[] { 0x00 });
        var payload = new PayloadBuilder(OneString, 1).Block(1, body).ToArray();
        Assert.Equal(CcfErrorCode.LimitExceeded, DecodeCode(OneString, payload, new CcfLimits { MaxStringBytes = 4 }));
    }

    [Fact]
    public void Schema_deeper_than_8_is_LimitExceeded_on_decode()
    {
        var deep = CcfSchema.Parse(NestObjects(9));
        Assert.Equal(9, deep.Depth);
        var payload = new PayloadBuilder(deep, 0).Block(0).ToArray();
        Assert.Equal(CcfErrorCode.LimitExceeded, DecodeCode(deep, payload));

        Assert.Equal(8, CcfSchema.Parse(NestObjects(8)).Depth);
    }

    internal static string NestObjects(int depth)
    {
        var inner = """{"name":"leaf","type":"int32"}""";
        for (var i = 1; i < depth; i++)
        {
            inner = $$"""{"name":"o{{i}}","type":"object","fields":[{{inner}}]}""";
        }

        return $$"""{"name":"Deep","fields":[{{inner}}]}""";
    }

    // ---- misc decoding rules ---------------------------------------------------------------------------

    [Fact]
    public void Invalid_utf8_is_InvalidUtf8_including_overlong_surrogate_and_truncated_sequences()
    {
        byte[][] bad =
        {
            new byte[] { 0xFF },
            new byte[] { 0xC0, 0x80 },             // overlong NUL
            new byte[] { 0xED, 0xA0, 0x80 },       // UTF-16 surrogate encoded
            new byte[] { 0xE2, 0x82 },             // truncated 3-byte sequence
            new byte[] { 0xF4, 0x90, 0x80, 0x80 }, // above U+10FFFF
        };
        foreach (var b in bad)
        {
            var payload = new PayloadBuilder(OneString, 1).Block(0, PayloadBuilder.Cat(PayloadBuilder.UV((ulong)b.Length), b)).ToArray();
            Assert.Equal(CcfErrorCode.InvalidUtf8, DecodeCode(OneString, payload));
        }
    }

    [Fact]
    public void Strings_keep_bom_nul_and_supplementary_characters()
    {
        var text = "﻿a\0b😀";
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var payload = new PayloadBuilder(OneString, 1).Block(0, PayloadBuilder.Cat(PayloadBuilder.UV((ulong)bytes.Length), bytes)).ToArray();
        var row = (CcfObject)((object?[])CcfDecoder.DecodeValue(payload, OneString))[0]!;
        Assert.Equal(text, row["s"]);
    }

    [Fact]
    public void List_total_not_equal_to_the_sum_of_counts_is_CountMismatch()
    {
        var schema = S("""{"name":"l","type":"list","fields":[{"name":"$value","type":"int32"}]}""");
        var payload = new PayloadBuilder(schema, 2).Block(0, PayloadBuilder.Cat(new byte[] { 0x01, 0x01 }, PayloadBuilder.UV(3))).Raw().ToArray();
        Assert.Equal(CcfErrorCode.CountMismatch, DecodeCode(schema, payload));
    }

    [Fact]
    public void List_counts_whose_sum_overflows_64_bits_are_CountMismatch()
    {
        var schema = S("""{"name":"l","type":"list","fields":[{"name":"$value","type":"int32"}]}""");
        var body = PayloadBuilder.Cat(PayloadBuilder.UV(ulong.MaxValue), PayloadBuilder.UV(ulong.MaxValue), PayloadBuilder.UV(5));
        Assert.Equal(CcfErrorCode.CountMismatch, DecodeCode(schema, new PayloadBuilder(schema, 2).Block(0, body).ToArray()));
    }

    [Fact]
    public void Enum_index_at_or_above_the_count_is_IndexOutOfRange_even_when_it_fits_the_bit_width()
    {
        // 3 values → 2 bits → index 3 fits the width but is out of range.
        var three = S("""{"name":"e","type":"enum","values":["a","b","c"]}""");
        Assert.Equal(CcfErrorCode.IndexOutOfRange, DecodeCode(three, new PayloadBuilder(three, 1).Block(0, 0b11).ToArray()));

        // 5 values → 3 bits → indexes 5, 6 and 7 are out of range.
        var five = S("""{"name":"e","type":"enum","values":["a","b","c","d","e"]}""");
        foreach (var bad in new byte[] { 5, 6, 7 })
        {
            Assert.Equal(CcfErrorCode.IndexOutOfRange, DecodeCode(five, new PayloadBuilder(five, 1).Block(0, bad).ToArray()));
        }

        Assert.NotNull(CcfDecoder.DecodeValue(new PayloadBuilder(five, 1).Block(0, 4).ToArray(), five));
    }

    [Fact]
    public void Unused_high_bits_of_packed_bytes_are_ignored_by_decoders()
    {
        var schema = S("""{"name":"b","type":"bool"}""");
        var rows = (object?[])CcfDecoder.DecodeValue(new PayloadBuilder(schema, 3).Block(0, 0b1111_1101).ToArray(), schema);
        Assert.Equal(new object[] { true, false, true }, rows.Select(r => ((CcfObject)r!)["b"]).ToArray());
    }

    [Fact]
    public void Non_shortest_uvarints_are_accepted_by_decoders()
    {
        var rows = (object?[])CcfDecoder.DecodeValue(new PayloadBuilder(OneInt32, 1).Block(0, 0x84, 0x80, 0x00).ToArray(), OneInt32);
        Assert.Equal(2, ((CcfObject)rows[0]!)["a"]);
    }

    [Fact]
    public void Decoders_accept_any_float64_bit_pattern_including_nan_infinity_and_negative_zero()
    {
        var schema = S("""{"name":"f","type":"float64"}""");
        var values = new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0.0, double.Epsilon };
        var body = values.SelectMany(v => BitConverter.GetBytes(BitConverter.DoubleToInt64Bits(v))).ToArray();
        var rows = (object?[])CcfDecoder.DecodeValue(new PayloadBuilder(schema, 5).Block(0, body).ToArray(), schema);
        var decoded = rows.Select(r => (double)((CcfObject)r!)["f"]!).ToArray();
        Assert.True(double.IsNaN(decoded[0]));
        Assert.True(double.IsPositiveInfinity(decoded[1]));
        Assert.True(double.IsNegativeInfinity(decoded[2]));
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0.0), BitConverter.DoubleToInt64Bits(decoded[3]));
        Assert.Equal(double.Epsilon, decoded[4]);
    }

    [Fact]
    public void Datetime_accepts_at_least_0001_01_01_to_9999_12_31_UTC()
    {
        // SPEC §14.8: every platform must accept this whole range.
        var schema = S("""{"name":"d","type":"datetime"}""");
        const long first = -62135596800000L;   // 0001-01-01T00:00:00.000Z
        const long last = 253402300799999L;    // 9999-12-31T23:59:59.999Z
        var rows = (object?[])CcfDecoder.DecodeValue(new PayloadBuilder(schema, 2).Block(0, PayloadBuilder.Cat(ZZ(first), ZZ(last))).ToArray(), schema);
        Assert.Equal(new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc), ((CcfObject)rows[0]!)["d"]);
        Assert.Equal(new DateTime(9999, 12, 31, 23, 59, 59, 999, DateTimeKind.Utc), ((CcfObject)rows[1]!)["d"]);
    }

    [Fact]
    public void Datetime_outside_the_dotnet_range_is_ValueOutOfRange()
    {
        var schema = S("""{"name":"d","type":"datetime"}""");
        Assert.Equal(CcfErrorCode.ValueOutOfRange, DecodeCode(schema, new PayloadBuilder(schema, 1).Block(0, ZZ(CcfConvert.MaxDateTimeMs + 1)).ToArray()));
        Assert.Equal(CcfErrorCode.ValueOutOfRange, DecodeCode(schema, new PayloadBuilder(schema, 1).Block(0, ZZ(CcfConvert.MinDateTimeMs - 1)).ToArray()));
        var ok = (object?[])CcfDecoder.DecodeValue(new PayloadBuilder(schema, 2).Block(0, PayloadBuilder.Cat(ZZ(CcfConvert.MinDateTimeMs), ZZ(CcfConvert.MaxDateTimeMs))).ToArray(), schema);
        Assert.Equal(DateTime.MinValue, ((CcfObject)ok[0]!)["d"]);
    }

    [Fact]
    public void Unsigned_decimal_going_negative_through_deltas_is_ValueOutOfRange()
    {
        var schema = S("""{"name":"d","type":"decimal","scale":2,"unsigned":true,"delta":true}""");
        Assert.Equal(CcfErrorCode.ValueOutOfRange, DecodeCode(schema, new PayloadBuilder(schema, 2).Block(0, PayloadBuilder.Cat(ZZ(5), ZZ(-6))).ToArray()));
    }

    [Fact]
    public void Null_value_tree_for_nested_nulls_matches_the_expected_files()
    {
        // v04 contains a null object, an empty list and a null list; the canonical comparison covers values,
        // here we additionally check the CLR shapes of the tree.
        var v = VectorManifest.Get("v04-orders-nested");
        var rows = (object?[])CcfDecoder.DecodeValue(v.ReadPayload(), CcfSchema.Parse(v.ReadSchemaText()));
        var second = (CcfObject)rows[1]!;
        Assert.Null(second["customer"]);
        Assert.Empty((object?[])second["lines"]!);
        Assert.Null(second["tags"]);
        Assert.Empty((object?[])((CcfObject)rows[2]!)["tags"]!);
    }
}
