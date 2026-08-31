#nullable enable
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Core.Tests")]

namespace Lib.point.io;

[Guid("740960bb-9029-463f-9543-fda43e48ff17")]
internal sealed class LoadGaussianSplat : Instance<LoadGaussianSplat>, IDescriptiveFilename, IStatusProvider
{
    [Output(Guid = "14e4a7de-13ce-49bb-95c9-91db0058872e")]
    public readonly Slot<BufferWithViews?> PointBuffer = new();

    public LoadGaussianSplat()
    {
        _resource = new Resource<GaussianSplatData>(Path, TryLoad);
        _resource.AddDependentSlots(PointBuffer);
        PointBuffer.UpdateAction += Update;
    }

    private bool TryLoad(FileResource file,
                         GaussianSplatData? currentValue,
                         [NotNullWhen(true)] out GaussianSplatData? newValue,
                         [NotNullWhen(false)] out string? failureReason)
    {
        currentValue?.Dispose();
        newValue = null;

        if (!file.TryOpenFileStream(out var stream, out failureReason, FileAccess.Read))
        {
            failureReason ??= $"Could not open Gaussian splat file: {file.AbsolutePath}";
            return false;
        }

        try
        {
            using var fileStream = stream;
            if (!TryReadPoints(fileStream, file.AbsolutePath, out var points, out var formatName, out failureReason))
            {
                failureReason ??= $"Failed loading {file.AbsolutePath}";
                _warningMessage = failureReason;
                return false;
            }

            newValue = new GaussianSplatData(points);
            failureReason = null;
            _warningMessage = string.Empty;
            Log.Debug($"Loaded {points.Length} Gaussian splats from '{file.AbsolutePath}' as {formatName}.", this);
            return true;
        }
        catch (Exception e)
        {
            failureReason = $"Failed to load Gaussian splat file: {e.Message}";
            _warningMessage = failureReason;
            return false;
        }
    }

    private void Update(EvaluationContext context)
    {
        if (_resource.TryGetValue(context, out var data))
        {
            PointBuffer.Value = data.PointBuffer;
            _warningMessage = string.Empty;
        }
        else
        {
            PointBuffer.Value = null;
            _warningMessage = $"Failed loading {Path.Value}";
        }
    }

    internal static bool TryReadPoints(Stream stream,
                                       string path,
                                       [NotNullWhen(true)] out Point[]? points,
                                       [NotNullWhen(true)] out string? formatName,
                                       [NotNullWhen(false)] out string? failureReason)
    {
        points = null;
        formatName = null;

        if (!TryDetectFormat(stream, path, out var format, out failureReason))
        {
            return false;
        }

        stream.Position = 0;
        switch (format)
        {
            case GaussianSplatFormat.Ply:
                formatName = "binary little-endian PLY";
                return TryReadPlyPoints(stream, out points, out failureReason);

            case GaussianSplatFormat.Spz:
                formatName = "SPZ v4";
                return TryReadSpzPoints(stream, out points, out failureReason);

            default:
                failureReason = "Unsupported Gaussian splat format.";
                return false;
        }
    }

    internal static bool TryDetectFormat(Stream stream,
                                         string path,
                                         out GaussianSplatFormat format,
                                         [NotNullWhen(false)] out string? failureReason)
    {
        format = GaussianSplatFormat.Unknown;
        failureReason = null;

        if (!stream.CanSeek)
        {
            failureReason = "Gaussian splat stream must be seekable.";
            return false;
        }

        var originalPosition = stream.Position;
        Span<byte> magic = stackalloc byte[4];
        var bytesRead = stream.Read(magic);
        stream.Position = originalPosition;

        if (bytesRead >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(magic) == SpzMagic)
        {
            format = GaussianSplatFormat.Spz;
            return true;
        }

        if (bytesRead >= 3 && magic[0] == 'p' && magic[1] == 'l' && magic[2] == 'y')
        {
            format = GaussianSplatFormat.Ply;
            return true;
        }

        var extension = System.IO.Path.GetExtension(path);
        if (extension.Equals(".spz", StringComparison.OrdinalIgnoreCase))
        {
            format = GaussianSplatFormat.Spz;
            return true;
        }

        if (extension.Equals(".ply", StringComparison.OrdinalIgnoreCase))
        {
            format = GaussianSplatFormat.Ply;
            return true;
        }

        failureReason = "Unsupported Gaussian splat file. Supported formats are binary PLY and SPZ v4.";
        return false;
    }

    private static bool TryReadPlyPoints(Stream stream,
                                         [NotNullWhen(true)] out Point[]? points,
                                         [NotNullWhen(false)] out string? failureReason)
    {
        points = null;

        if (!TryReadPlyHeader(stream, out var header, out failureReason))
        {
            return false;
        }

        var elementRecordBuffer = Array.Empty<byte>();
        foreach (var element in header.Elements)
        {
            if (element.Stride < 0)
            {
                failureReason = $"PLY element '{element.Name}' has an invalid stride.";
                return false;
            }

            if (element.Name != "vertex")
            {
                if (!TrySkipExactly(stream, (long)element.Count * element.Stride))
                {
                    failureReason = $"PLY file ended while skipping element '{element.Name}'.";
                    return false;
                }

                continue;
            }

            points = new Point[element.Count];
            elementRecordBuffer = elementRecordBuffer.Length >= element.Stride ? elementRecordBuffer : new byte[element.Stride];

            for (var pointIndex = 0; pointIndex < points.Length; pointIndex++)
            {
                stream.ReadExactly(elementRecordBuffer.AsSpan(0, element.Stride));
                points[pointIndex] = CreatePointFromPlyRecord(elementRecordBuffer, header.VertexLayout);
            }
        }

        if (points == null)
        {
            failureReason = "PLY file does not contain a vertex element.";
            return false;
        }

        failureReason = null;
        return true;
    }

    private static bool TryReadPlyHeader(Stream stream,
                                         out PlyHeader header,
                                         [NotNullWhen(false)] out string? failureReason)
    {
        header = default;
        failureReason = null;

        if (!TryReadAsciiLine(stream, out var line) || line != "ply")
        {
            failureReason = "Expected PLY magic header.";
            return false;
        }

        if (!TryReadAsciiLine(stream, out line) || line != "format binary_little_endian 1.0")
        {
            failureReason = "Only binary_little_endian PLY 1.0 is supported.";
            return false;
        }

        var elements = new List<PlyElement>();
        PlyElementBuilder? currentElement = null;

        while (TryReadAsciiLine(stream, out line))
        {
            if (line == "end_header")
            {
                if (currentElement != null)
                {
                    elements.Add(currentElement.Build());
                }

                if (!TryCreateVertexLayout(elements, out var vertexLayout, out failureReason))
                {
                    return false;
                }

                header = new PlyHeader(elements, vertexLayout);
                return true;
            }

            if (line.StartsWith("comment ", StringComparison.Ordinal) ||
                line.StartsWith("obj_info ", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.StartsWith("element ", StringComparison.Ordinal))
            {
                if (currentElement != null)
                {
                    elements.Add(currentElement.Build());
                }

                if (!TryReadElement(line, out currentElement, out failureReason))
                {
                    return false;
                }

                continue;
            }

            if (line.StartsWith("property ", StringComparison.Ordinal))
            {
                if (currentElement == null)
                {
                    failureReason = $"PLY property declared before any element: {line}";
                    return false;
                }

                if (!TryReadProperty(line, currentElement, out failureReason))
                {
                    return false;
                }

                continue;
            }

            failureReason = $"Unsupported PLY header line: {line}";
            return false;
        }

        failureReason = "PLY header ended before end_header.";
        return false;
    }

    private static bool TryReadElement(string line,
                                       [NotNullWhen(true)] out PlyElementBuilder? element,
                                       [NotNullWhen(false)] out string? failureReason)
    {
        element = null;
        failureReason = null;

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            failureReason = $"Invalid PLY element declaration: {line}";
            return false;
        }

        if (!int.TryParse(parts[2], out var count) || count < 0)
        {
            failureReason = $"Invalid PLY element count: {parts[2]}";
            return false;
        }

        element = new PlyElementBuilder(parts[1], count);
        return true;
    }

    private static bool TryReadProperty(string line,
                                        PlyElementBuilder element,
                                        [NotNullWhen(false)] out string? failureReason)
    {
        failureReason = null;

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            failureReason = $"Invalid PLY property declaration: {line}";
            return false;
        }

        if (parts[1] == "list")
        {
            failureReason = "PLY list properties are not supported for Gaussian splats.";
            return false;
        }

        if (!TryGetPlyScalarSize(parts[1], out var size))
        {
            failureReason = $"Unsupported PLY scalar property type '{parts[1]}'.";
            return false;
        }

        element.AddProperty(new PlyProperty(parts[2], parts[1], element.Stride, size));
        return true;
    }

    private static bool TryCreateVertexLayout(IReadOnlyList<PlyElement> elements,
                                              out PlyVertexLayout vertexLayout,
                                              [NotNullWhen(false)] out string? failureReason)
    {
        vertexLayout = default;
        failureReason = null;

        var vertex = elements.FirstOrDefault(element => element.Name == "vertex");
        if (vertex.Name != "vertex")
        {
            failureReason = "PLY header does not declare a vertex element.";
            return false;
        }

        if (vertex.Count <= 0)
        {
            failureReason = "PLY header does not declare a positive vertex count.";
            return false;
        }

        var propertyByName = vertex.Properties.ToDictionary(property => property.Name, StringComparer.Ordinal);
        foreach (var requiredName in RequiredPlyFloatProperties)
        {
            if (!propertyByName.TryGetValue(requiredName, out var property))
            {
                failureReason = $"PLY vertex element is missing required property '{requiredName}'.";
                return false;
            }

            if (property.Type != "float" && property.Type != "float32")
            {
                failureReason = $"PLY vertex property '{requiredName}' must be float, found '{property.Type}'.";
                return false;
            }
        }

        vertexLayout = new PlyVertexLayout(propertyByName["x"].Offset,
                                           propertyByName["y"].Offset,
                                           propertyByName["z"].Offset,
                                           propertyByName["f_dc_0"].Offset,
                                           propertyByName["f_dc_1"].Offset,
                                           propertyByName["f_dc_2"].Offset,
                                           propertyByName["opacity"].Offset,
                                           propertyByName["scale_0"].Offset,
                                           propertyByName["scale_1"].Offset,
                                           propertyByName["scale_2"].Offset,
                                           propertyByName["rot_0"].Offset,
                                           propertyByName["rot_1"].Offset,
                                           propertyByName["rot_2"].Offset,
                                           propertyByName["rot_3"].Offset);
        return true;
    }

    private static Point CreatePointFromPlyRecord(ReadOnlySpan<byte> recordBytes, PlyVertexLayout layout)
    {
        var rotW = ReadFloat(recordBytes, layout.Rot0Offset);
        var rotX = ReadFloat(recordBytes, layout.Rot1Offset);
        var rotY = ReadFloat(recordBytes, layout.Rot2Offset);
        var rotZ = ReadFloat(recordBytes, layout.Rot3Offset);

        var rawScale0 = ReadFloat(recordBytes, layout.Scale0Offset);
        var rawScale1 = ReadFloat(recordBytes, layout.Scale1Offset);
        var rawScale2 = ReadFloat(recordBytes, layout.Scale2Offset);

        return new Point
                   {
                       Position = new Vector3(ReadFloat(recordBytes, layout.XOffset),
                                              ReadFloat(recordBytes, layout.YOffset),
                                              ReadFloat(recordBytes, layout.ZOffset)),
                       Orientation = NormalizeOrIdentity(new Quaternion(rotX, rotY, rotZ, rotW)),
                       Scale = new Vector3(MathF.Exp(rawScale0),
                                           MathF.Exp(rawScale1),
                                           MathF.Exp(rawScale2)),
                       Color = new Vector4(ToRgb(ReadFloat(recordBytes, layout.FDc0Offset)),
                                           ToRgb(ReadFloat(recordBytes, layout.FDc1Offset)),
                                           ToRgb(ReadFloat(recordBytes, layout.FDc2Offset)),
                                           Sigmoid(ReadFloat(recordBytes, layout.OpacityOffset))),
                       F1 = 1,
                       F2 = 1
                   };
    }

    private static bool TryGetPlyScalarSize(string scalarType, out int size)
    {
        size = scalarType switch
                   {
                       "char" or "int8" or "uchar" or "uint8" => 1,
                       "short" or "int16" or "ushort" or "uint16" => 2,
                       "int" or "int32" or "uint" or "uint32" or "float" or "float32" => 4,
                       "double" or "float64" => 8,
                       _ => 0
                   };
        return size > 0;
    }

    private static bool TryReadSpzPoints(Stream stream,
                                         [NotNullWhen(true)] out Point[]? points,
                                         [NotNullWhen(false)] out string? failureReason)
    {
        points = null;
        using var memoryStream = new MemoryStream();
        stream.CopyTo(memoryStream);
        var fileBytes = memoryStream.ToArray();

        if (!TryReadSpzHeader(fileBytes, out var header, out failureReason))
        {
            return false;
        }

        if (header.Flags != 0)
        {
            failureReason = $"SPZ v4 file uses unsupported flags 0x{header.Flags:x2}.";
            return false;
        }

        if (header.NumStreams < RequiredSpzStreamCount)
        {
            failureReason = $"SPZ v4 file declares {header.NumStreams} streams; at least {RequiredSpzStreamCount} are required.";
            return false;
        }

        if (header.NumStreams > AllSpzStreamCount)
        {
            failureReason = $"SPZ v4 file declares {header.NumStreams} streams; this loader supports up to {AllSpzStreamCount}.";
            return false;
        }

        var tocBytes = checked(header.NumStreams * SpzTocEntrySize);
        if (header.TocByteOffset + tocBytes > fileBytes.Length)
        {
            failureReason = "SPZ v4 TOC extends beyond end of file.";
            return false;
        }

        var streams = new SpzStreamEntry[header.NumStreams];
        var compressedOffset = header.TocByteOffset + tocBytes;
        for (var streamIndex = 0; streamIndex < streams.Length; streamIndex++)
        {
            var tocOffset = header.TocByteOffset + streamIndex * SpzTocEntrySize;
            var compressedSize = ReadUlongAsInt(fileBytes.AsSpan(tocOffset, sizeof(ulong)), "compressed stream size");
            var uncompressedSize = ReadUlongAsInt(fileBytes.AsSpan(tocOffset + sizeof(ulong), sizeof(ulong)), "uncompressed stream size");
            if (compressedSize < 0 || uncompressedSize < 0)
            {
                failureReason = "SPZ v4 stream size exceeds the supported in-memory size.";
                return false;
            }

            if (compressedOffset + compressedSize > fileBytes.Length)
            {
                failureReason = $"SPZ v4 stream {streamIndex} extends beyond end of file.";
                return false;
            }

            streams[streamIndex] = new SpzStreamEntry(compressedOffset, compressedSize, uncompressedSize);
            compressedOffset += compressedSize;
        }

        if (!ValidateSpzStreamSizes(header, streams, out failureReason))
        {
            return false;
        }

        byte[] positions;
        byte[] alphas;
        byte[] colors;
        byte[] scales;
        byte[] rotations;
        try
        {
            positions = DecompressSpzStream(fileBytes, streams[(int)SpzStreamKind.Positions]);
            alphas = DecompressSpzStream(fileBytes, streams[(int)SpzStreamKind.Alphas]);
            colors = DecompressSpzStream(fileBytes, streams[(int)SpzStreamKind.Colors]);
            scales = DecompressSpzStream(fileBytes, streams[(int)SpzStreamKind.Scales]);
            rotations = DecompressSpzStream(fileBytes, streams[(int)SpzStreamKind.Rotations]);
        }
        catch (Exception e)
        {
            failureReason = $"Failed to decompress SPZ v4 stream: {e.Message}";
            return false;
        }

        points = new Point[header.NumPoints];
        for (var pointIndex = 0; pointIndex < points.Length; pointIndex++)
        {
            points[pointIndex] = CreatePointFromSpzStreams(pointIndex, header.FractionalBits, positions, alphas, colors, scales, rotations);
        }

        failureReason = null;
        return true;
    }

    internal static bool TryReadSpzHeader(ReadOnlySpan<byte> bytes,
                                          out SpzHeader header,
                                          [NotNullWhen(false)] out string? failureReason)
    {
        header = default;
        failureReason = null;

        if (bytes.Length < SpzHeaderSize)
        {
            failureReason = "SPZ v4 file is shorter than its 32-byte header.";
            return false;
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes[..4]);
        if (magic != SpzMagic)
        {
            failureReason = "Expected SPZ magic header.";
            return false;
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4));
        if (version != SupportedSpzVersion)
        {
            failureReason = $"Only SPZ v{SupportedSpzVersion} is supported, found v{version}.";
            return false;
        }

        var numPoints = ReadUintAsInt(bytes.Slice(8, 4), "point count");
        if (numPoints <= 0)
        {
            failureReason = "SPZ v4 header does not declare a positive point count.";
            return false;
        }

        var shDegree = bytes[12];
        if (shDegree > MaxSpzShDegree)
        {
            failureReason = $"SPZ v4 SH degree {shDegree} is not supported.";
            return false;
        }

        var fractionalBits = bytes[13];
        if (fractionalBits > MaxSpzFractionalBits)
        {
            failureReason = $"SPZ v4 fractional bits {fractionalBits} are not supported.";
            return false;
        }

        var flags = bytes[14];
        var numStreams = bytes[15];
        var tocByteOffset = ReadUintAsInt(bytes.Slice(16, 4), "TOC byte offset");
        if (tocByteOffset < SpzHeaderSize || tocByteOffset > bytes.Length)
        {
            failureReason = "SPZ v4 header contains an invalid TOC byte offset.";
            return false;
        }

        for (var i = 20; i < SpzHeaderSize; i++)
        {
            if (bytes[i] != 0)
            {
                failureReason = "SPZ v4 reserved header bytes must be zero.";
                return false;
            }
        }

        header = new SpzHeader(numPoints, shDegree, fractionalBits, flags, numStreams, tocByteOffset);
        return true;
    }

    private static bool ValidateSpzStreamSizes(SpzHeader header,
                                               IReadOnlyList<SpzStreamEntry> streams,
                                               [NotNullWhen(false)] out string? failureReason)
    {
        var expectedSizes = new int[AllSpzStreamCount];
        expectedSizes[(int)SpzStreamKind.Positions] = checked(header.NumPoints * 9);
        expectedSizes[(int)SpzStreamKind.Alphas] = header.NumPoints;
        expectedSizes[(int)SpzStreamKind.Colors] = checked(header.NumPoints * 3);
        expectedSizes[(int)SpzStreamKind.Scales] = checked(header.NumPoints * 3);
        expectedSizes[(int)SpzStreamKind.Rotations] = checked(header.NumPoints * 4);
        expectedSizes[(int)SpzStreamKind.Sh] = checked(header.NumPoints * GetSpzShComponentCount(header.ShDegree));

        for (var streamIndex = 0; streamIndex < streams.Count; streamIndex++)
        {
            var expectedSize = expectedSizes[streamIndex];
            if (streams[streamIndex].UncompressedSize != expectedSize)
            {
                failureReason = $"SPZ v4 stream {streamIndex} declares {streams[streamIndex].UncompressedSize} uncompressed bytes; expected {expectedSize}.";
                return false;
            }
        }

        if (header.ShDegree > 0 && streams.Count <= (int)SpzStreamKind.Sh)
        {
            failureReason = "SPZ v4 file declares spherical harmonics but does not include the SH stream.";
            return false;
        }

        failureReason = null;
        return true;
    }

    private static Point CreatePointFromSpzStreams(int pointIndex,
                                                   byte fractionalBits,
                                                   ReadOnlySpan<byte> positions,
                                                   ReadOnlySpan<byte> alphas,
                                                   ReadOnlySpan<byte> colors,
                                                   ReadOnlySpan<byte> scales,
                                                   ReadOnlySpan<byte> rotations)
    {
        var positionOffset = pointIndex * 9;
        var colorOffset = pointIndex * 3;
        var rotationOffset = pointIndex * 4;

        var rawScale0 = DecodeSpzScale(scales[colorOffset]);
        var rawScale1 = DecodeSpzScale(scales[colorOffset + 1]);
        var rawScale2 = DecodeSpzScale(scales[colorOffset + 2]);

        return new Point
                   {
                       Position = new Vector3(DecodeSpzPositionComponent(positions.Slice(positionOffset, 3), fractionalBits),
                                              DecodeSpzPositionComponent(positions.Slice(positionOffset + 3, 3), fractionalBits),
                                              DecodeSpzPositionComponent(positions.Slice(positionOffset + 6, 3), fractionalBits)),
                       Orientation = DecodeSpzRotation(rotations.Slice(rotationOffset, 4)),
                       Scale = new Vector3(MathF.Exp(rawScale0),
                                           MathF.Exp(rawScale1),
                                           MathF.Exp(rawScale2)),
                       Color = new Vector4(DecodeSpzColor(colors[colorOffset]),
                                           DecodeSpzColor(colors[colorOffset + 1]),
                                           DecodeSpzColor(colors[colorOffset + 2]),
                                           alphas[pointIndex] / 255f),
                       F1 = 1,
                       F2 = 1
                   };
    }

    internal static float DecodeSpzPositionComponent(ReadOnlySpan<byte> bytes, byte fractionalBits)
    {
        return ReadSignedInt24LittleEndian(bytes) / (float)(1 << fractionalBits);
    }

    internal static int ReadSignedInt24LittleEndian(ReadOnlySpan<byte> bytes)
    {
        var value = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);
        if ((value & 0x800000) != 0)
        {
            value |= unchecked((int)0xff000000);
        }

        return value;
    }

    internal static Quaternion DecodeSpzRotation(ReadOnlySpan<byte> bytes)
    {
        var packed = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var largestIndex = (int)(packed >> 30);
        packed &= 0x3fffffff;

        Span<float> components = stackalloc float[4];
        var sum = 0f;
        for (var componentIndex = 3; componentIndex >= 0; componentIndex--)
        {
            if (componentIndex == largestIndex)
            {
                continue;
            }

            var magnitude = packed & SpzRotationMagnitudeMask;
            var isNegative = (packed & SpzRotationSignBit) != 0;
            packed >>= SpzRotationComponentBits;

            var value = SpzRotationScale * magnitude / SpzRotationMagnitudeMask;
            if (isNegative)
            {
                value = -value;
            }

            components[componentIndex] = value;
            sum += value * value;
        }

        components[largestIndex] = MathF.Sqrt(Math.Max(0, 1 - sum));
        return NormalizeOrIdentity(new Quaternion(components[0], components[1], components[2], components[3]));
    }

    private static byte[] DecompressSpzStream(byte[] fileBytes, SpzStreamEntry streamEntry)
    {
        using var decompressor = new ZstdSharp.Decompressor();
        var compressedBytes = fileBytes.AsSpan(streamEntry.CompressedOffset, streamEntry.CompressedSize).ToArray();
        var uncompressedBytes = decompressor.Unwrap(compressedBytes);
        if (uncompressedBytes.Length != streamEntry.UncompressedSize)
        {
            throw new InvalidDataException($"expected {streamEntry.UncompressedSize} bytes, got {uncompressedBytes.Length}");
        }

        return uncompressedBytes.ToArray();
    }

    private static float DecodeSpzScale(byte value)
    {
        return value / 16f - 10f;
    }

    private static float DecodeSpzColor(byte value)
    {
        var dc = (value / 255f - 0.5f) / SpzColorScale;
        return ToRgb(dc);
    }

    private static int GetSpzShComponentCount(byte shDegree)
    {
        return shDegree == 0 ? 0 : ((shDegree + 1) * (shDegree + 1) - 1) * 3;
    }

    private static int ReadUintAsInt(ReadOnlySpan<byte> bytes, string name)
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (value > int.MaxValue)
        {
            throw new InvalidDataException($"SPZ {name} exceeds the supported in-memory size.");
        }

        return (int)value;
    }

    private static int ReadUlongAsInt(ReadOnlySpan<byte> bytes, string name)
    {
        var value = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        if (value > int.MaxValue)
        {
            return -1;
        }

        return (int)value;
    }

    private static bool TryReadAsciiLine(Stream stream, [NotNullWhen(true)] out string? line)
    {
        _headerLineBuffer.Clear();

        while (true)
        {
            var value = stream.ReadByte();
            if (value < 0)
            {
                line = _headerLineBuffer.Count == 0 ? null : System.Text.Encoding.ASCII.GetString(_headerLineBuffer.ToArray());
                return line != null;
            }

            if (value == '\n')
            {
                line = System.Text.Encoding.ASCII.GetString(_headerLineBuffer.ToArray());
                return true;
            }

            if (value != '\r')
            {
                _headerLineBuffer.Add((byte)value);
            }
        }
    }

    private static bool TrySkipExactly(Stream stream, long byteCount)
    {
        if (byteCount < 0)
        {
            return false;
        }

        if (stream.CanSeek)
        {
            stream.Seek(byteCount, SeekOrigin.Current);
            return stream.Position <= stream.Length;
        }

        Span<byte> buffer = stackalloc byte[1024];
        var remaining = byteCount;
        while (remaining > 0)
        {
            var readSize = (int)Math.Min(buffer.Length, remaining);
            var bytesRead = stream.Read(buffer[..readSize]);
            if (bytesRead == 0)
            {
                return false;
            }

            remaining -= bytesRead;
        }

        return true;
    }

    private static float ReadFloat(ReadOnlySpan<byte> recordBytes, int byteOffset)
    {
        return BinaryPrimitives.ReadSingleLittleEndian(recordBytes.Slice(byteOffset, sizeof(float)));
    }

    private static Quaternion NormalizeOrIdentity(Quaternion orientation)
    {
        return orientation.LengthSquared() > 0 ? Quaternion.Normalize(orientation) : Quaternion.Identity;
    }

    private static float ToRgb(float dc)
    {
        return Math.Clamp(0.5f + C0 * dc, 0, 1);
    }

    private static float Sigmoid(float value)
    {
        if (value >= 0)
        {
            return 1 / (1 + MathF.Exp(-value));
        }

        var exp = MathF.Exp(value);
        return exp / (1 + exp);
    }

    public IStatusProvider.StatusLevel GetStatusLevel()
    {
        return string.IsNullOrEmpty(_warningMessage) ? IStatusProvider.StatusLevel.Success : IStatusProvider.StatusLevel.Warning;
    }

    public string GetStatusMessage()
    {
        return _warningMessage;
    }

    public IEnumerable<string> FileFilter => FileFilterDefault;
    public InputSlot<string> SourcePathSlot => Path;

    internal enum GaussianSplatFormat
    {
        Unknown,
        Ply,
        Spz
    }

    internal readonly record struct SpzHeader(int NumPoints, byte ShDegree, byte FractionalBits, byte Flags, byte NumStreams, int TocByteOffset);

    private enum SpzStreamKind
    {
        Positions,
        Alphas,
        Colors,
        Scales,
        Rotations,
        Sh
    }

    private sealed class GaussianSplatData : IDisposable
    {
        public readonly BufferWithViews PointBuffer;

        public GaussianSplatData(Point[] points)
        {
            PointBuffer = new BufferWithViews();
            Buffer? buffer = null;

            ResourceManager.SetupStructuredBuffer(points,
                                                  Point.Stride * points.Length,
                                                  Point.Stride,
                                                  ref buffer);
            ResourceManager.CreateStructuredBufferSrv(buffer, ref PointBuffer.Srv);
            ResourceManager.CreateStructuredBufferUav(buffer, UnorderedAccessViewBufferFlags.None, ref PointBuffer.Uav);
            PointBuffer.Buffer = buffer;
        }

        public void Dispose()
        {
            PointBuffer.Dispose();
        }
    }

    private readonly record struct PlyHeader(IReadOnlyList<PlyElement> Elements, PlyVertexLayout VertexLayout);
    private readonly record struct PlyElement(string Name, int Count, IReadOnlyList<PlyProperty> Properties, int Stride);
    private readonly record struct PlyProperty(string Name, string Type, int Offset, int Size);
    private readonly record struct PlyVertexLayout(int XOffset,
                                                   int YOffset,
                                                   int ZOffset,
                                                   int FDc0Offset,
                                                   int FDc1Offset,
                                                   int FDc2Offset,
                                                   int OpacityOffset,
                                                   int Scale0Offset,
                                                   int Scale1Offset,
                                                   int Scale2Offset,
                                                   int Rot0Offset,
                                                   int Rot1Offset,
                                                   int Rot2Offset,
                                                   int Rot3Offset);

    private readonly record struct SpzStreamEntry(int CompressedOffset, int CompressedSize, int UncompressedSize);

    private sealed class PlyElementBuilder
    {
        public readonly string Name;
        public readonly int Count;
        public readonly List<PlyProperty> Properties = new();
        public int Stride { get; private set; }

        public PlyElementBuilder(string name, int count)
        {
            Name = name;
            Count = count;
        }

        public void AddProperty(PlyProperty property)
        {
            Properties.Add(property);
            Stride += property.Size;
        }

        public PlyElement Build()
        {
            return new PlyElement(Name, Count, Properties.ToArray(), Stride);
        }
    }

    private readonly Resource<GaussianSplatData> _resource;
    private string _warningMessage = string.Empty;

    private const uint SpzMagic = 0x5053474e;
    private const uint SupportedSpzVersion = 4;
    private const int SpzHeaderSize = 32;
    private const int SpzTocEntrySize = 16;
    private const int RequiredSpzStreamCount = 5;
    private const int AllSpzStreamCount = 6;
    private const int MaxSpzShDegree = 4;
    private const int MaxSpzFractionalBits = 24;
    private const int SpzRotationComponentBits = 10;
    private const uint SpzRotationMagnitudeMask = 511;
    private const uint SpzRotationSignBit = 512;
    private const float SpzRotationScale = 0.7071067811865476f;
    private const float SpzColorScale = 0.15f;
    private const float C0 = 0.28209479177387814f;
    private static readonly List<byte> _headerLineBuffer = new(128);
    private static readonly string[] FileFilterDefault = ["ply", "spz"];

    private static readonly string[] RequiredPlyFloatProperties =
        [
            "x", "y", "z",
            "f_dc_0", "f_dc_1", "f_dc_2",
            "opacity",
            "scale_0", "scale_1", "scale_2",
            "rot_0", "rot_1", "rot_2", "rot_3"
        ];

    [Input(Guid = "5cc67374-1114-48f6-be01-b2edb4770b55")]
    public readonly InputSlot<string> Path = new();
}
