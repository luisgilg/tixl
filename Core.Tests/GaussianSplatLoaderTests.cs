using Lib.point.io;
using Xunit;

namespace Core.Tests;

public sealed class GaussianSplatLoaderTests
{
    [Fact]
    public void DetectsPlyByMagic()
    {
        using var stream = new MemoryStream("ply\nformat binary_little_endian 1.0\n"u8.ToArray());

        Assert.True(LoadGaussianSplat.TryDetectFormat(stream, "anything.bin", out var format, out var failureReason));
        Assert.Equal(LoadGaussianSplat.GaussianSplatFormat.Ply, format);
        Assert.Null(failureReason);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void DetectsSpzByMagic()
    {
        using var stream = new MemoryStream([0x4e, 0x47, 0x53, 0x50]);

        Assert.True(LoadGaussianSplat.TryDetectFormat(stream, "anything.bin", out var format, out var failureReason));
        Assert.Equal(LoadGaussianSplat.GaussianSplatFormat.Spz, format);
        Assert.Null(failureReason);
        Assert.Equal(0, stream.Position);
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 0x00, 0x00 }, 0)]
    [InlineData(new byte[] { 0x01, 0x00, 0x00 }, 1)]
    [InlineData(new byte[] { 0xff, 0xff, 0x7f }, 8388607)]
    [InlineData(new byte[] { 0xff, 0xff, 0xff }, -1)]
    [InlineData(new byte[] { 0x00, 0x00, 0x80 }, -8388608)]
    public void ReadsSignedInt24LittleEndian(byte[] bytes, int expected)
    {
        Assert.Equal(expected, LoadGaussianSplat.ReadSignedInt24LittleEndian(bytes));
    }

    [Fact]
    public void DecodesSpzFixedPointPositionComponent()
    {
        Assert.Equal(-2.5f, LoadGaussianSplat.DecodeSpzPositionComponent([0x00, 0xd8, 0xff], 12));
    }

    [Fact]
    public void DecodesSpzIdentityQuaternion()
    {
        var rotation = LoadGaussianSplat.DecodeSpzRotation([0x00, 0x00, 0x00, 0xc0]);

        Assert.Equal(0, rotation.X, 6);
        Assert.Equal(0, rotation.Y, 6);
        Assert.Equal(0, rotation.Z, 6);
        Assert.Equal(1, rotation.W, 6);
    }

    [Fact]
    public void ReadsReferenceSpzHeaderWhenFixtureExists()
    {
        var path = FindRepositoryFile("3DGS", "splat.spz");
        if (path == null)
        {
            return;
        }

        var bytes = File.ReadAllBytes(path);

        Assert.True(LoadGaussianSplat.TryReadSpzHeader(bytes, out var header, out var failureReason));
        Assert.Null(failureReason);
        Assert.Equal(213120, header.NumPoints);
        Assert.Equal(3, header.ShDegree);
        Assert.Equal(12, header.FractionalBits);
        Assert.Equal(0, header.Flags);
        Assert.Equal(6, header.NumStreams);
        Assert.Equal(32, header.TocByteOffset);
    }

    [Theory]
    [InlineData("ns_splat.ply", 355061)]
    [InlineData("splat.ply", 213120)]
    [InlineData("splat.spz", 213120)]
    public void ReadsReferencePointCountsWhenFixturesExist(string fileName, int expectedCount)
    {
        var path = FindRepositoryFile("3DGS", fileName);
        if (path == null)
        {
            return;
        }

        using var stream = File.OpenRead(path);

        Assert.True(LoadGaussianSplat.TryReadPoints(stream, path, out var points, out _, out var failureReason), failureReason);
        Assert.NotNull(points);
        Assert.Equal(expectedCount, points.Length);
    }

    private static string? FindRepositoryFile(params string[] pathParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var path = Path.Combine(new[] { directory.FullName }.Concat(pathParts).ToArray());
            if (File.Exists(path))
            {
                return path;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
