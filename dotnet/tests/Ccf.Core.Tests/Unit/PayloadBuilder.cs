namespace Ccf.Core.Tests;

/// <summary>Hand-assembles payloads so tests can produce exactly the malformed bytes they need.</summary>
internal sealed class PayloadBuilder
{
    private readonly List<byte> _bytes = new();

    public PayloadBuilder(CcfSchema schema, int rows, byte flags = 0)
    {
        _bytes.AddRange(new byte[] { 0x43, 0x43, 1, flags });
        _bytes.AddRange(schema.IdBytes.ToArray());
        UVarint((ulong)rows);
    }

    public PayloadBuilder UVarint(ulong value)
    {
        Span<byte> tmp = stackalloc byte[10];
        var n = CcfPrimitives.WriteUVarint(tmp, value);
        _bytes.AddRange(tmp[..n].ToArray());
        return this;
    }

    public PayloadBuilder Raw(params byte[] bytes)
    {
        _bytes.AddRange(bytes);
        return this;
    }

    /// <summary>A column block with an explicit mode and the correct length.</summary>
    public PayloadBuilder Block(byte mode, params byte[] body) => BlockWithLength(mode, (ulong)body.Length, body);

    /// <summary>A column block with an explicit (possibly wrong) declared length.</summary>
    public PayloadBuilder BlockWithLength(byte mode, ulong declaredLength, params byte[] body)
    {
        _bytes.Add(mode);
        UVarint(declaredLength);
        _bytes.AddRange(body);
        return this;
    }

    public byte[] ToArray() => _bytes.ToArray();

    public static byte[] UV(ulong value)
    {
        Span<byte> tmp = stackalloc byte[10];
        var n = CcfPrimitives.WriteUVarint(tmp, value);
        return tmp[..n].ToArray();
    }

    public static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}
