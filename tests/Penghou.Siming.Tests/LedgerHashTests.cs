namespace Penghou.Siming.Tests;

public sealed class LedgerHashTests
{
    [Fact]
    public void Constructor_CopiesExactlyThirtyTwoBytes()
    {
        var source = Enumerable.Range(0, LedgerHash.Size)
            .Select(value => (byte)value)
            .ToArray();

        var hash = new LedgerHash(source);
        source[0] = byte.MaxValue;

        Assert.Equal(0, hash.Bytes.Span[0]);
        Assert.Equal(LedgerHash.Size, hash.Bytes.Length);
    }

    [Fact]
    public void Constructor_RejectsWrongLength() =>
        Assert.Throws<ArgumentException>(() => new LedgerHash(new byte[31]));
}
