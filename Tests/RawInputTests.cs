using System.Buffers.Binary;
using Xunit;

namespace VoiceSiteLauncher.Tests;

public sealed class RawInputTests
{
    [Theory]
    [InlineData(0x2F, 0, true)]
    [InlineData(0x2F, 1, false)]
    [InlineData(0x40, 0, true)]
    [InlineData(0x40, 1, false)]
    [InlineData(0x2F, 2, true)]
    [InlineData(0x40, 3, false)]
    public void ParsesTrackedKeys(ushort makeCode, ushort flags, bool expectedDown)
    {
        var ok = RawInputParser.TryGetKey(RawKeyboard(makeCode, flags), out var scan, out var down);
        Assert.True(ok);
        Assert.Equal((uint)makeCode, scan);
        Assert.Equal(expectedDown, down);
    }

    [Fact]
    public void IgnoresUntrackedKey()
    {
        Assert.False(RawInputParser.TryGetKey(RawKeyboard(0x1E, 0), out _, out _));
    }

    [Fact]
    public void IgnoresNonKeyboardInput()
    {
        Assert.False(RawInputParser.TryGetKey(RawKeyboard(0x2F, 0, type: 0), out _, out _));
    }

    [Fact]
    public void RejectsTruncatedBuffer()
    {
        Assert.False(RawInputParser.TryGetKey(new byte[39], out _, out _));
    }

    private static byte[] RawKeyboard(ushort makeCode, ushort flags, uint type = 1)
    {
        var data = new byte[40];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), type);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(24, 2), makeCode);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(26, 2), flags);
        return data;
    }
}