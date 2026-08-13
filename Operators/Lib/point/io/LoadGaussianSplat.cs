#nullable enable
using System.Buffers.Binary;

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
            return false;
        }

        try
        {
            using var fileStream = stream;
            if (!TryReadHeader(fileStream, out var vertexCount, out failureReason))
            {
                return false;
            }

            var expectedDataBytes = (long)vertexCount * RecordSizeInBytes;
            if (fileStream.Length - fileStream.Position < expectedDataBytes)
            {
                failureReason = $"File is shorter than the declared Gaussian data: {file.AbsolutePath}";
                return false;
            }

            var points = new Point[vertexCount];
            ReadPoints(fileStream, points, out var scaleStats, out var rawScaleStats);
            newValue = new GaussianSplatData(points);
            failureReason = null;
            _warningMessage = string.Empty;
            Log.Debug($"Loaded {vertexCount} Gaussian splats from '{file.AbsolutePath}'.", this);
            Log.Debug($"Point.Scale stats: {scaleStats}", this);
            Log.Debug($"Raw log-scale stats: {rawScaleStats}", this);
            return true;
        }
        catch (Exception e)
        {
            failureReason = $"Failed to load Gaussian splat PLY: {e.Message}";
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

    private static void ReadPoints(Stream stream, Point[] points, out ScaleStats scaleStats, out ScaleStats rawScaleStats)
    {
        var recordBytes = new byte[RecordSizeInBytes];
        var scaleValues = new float[points.Length * 3];
        var rawScaleValues = new float[points.Length * 3];

        for (var pointIndex = 0; pointIndex < points.Length; pointIndex++)
        {
            stream.ReadExactly(recordBytes);

            var rotW = ReadFloat(recordBytes, 58);
            var rotX = ReadFloat(recordBytes, 59);
            var rotY = ReadFloat(recordBytes, 60);
            var rotZ = ReadFloat(recordBytes, 61);

            var orientation = new Quaternion(rotX, rotY, rotZ, rotW);
            orientation = orientation.LengthSquared() > 0 ? Quaternion.Normalize(orientation) : Quaternion.Identity;

            var rawScale0 = ReadFloat(recordBytes, 55);
            var rawScale1 = ReadFloat(recordBytes, 56);
            var rawScale2 = ReadFloat(recordBytes, 57);
            var scale0 = MathF.Exp(rawScale0);
            var scale1 = MathF.Exp(rawScale1);
            var scale2 = MathF.Exp(rawScale2);

            var scaleValueIndex = pointIndex * 3;
            rawScaleValues[scaleValueIndex] = rawScale0;
            rawScaleValues[scaleValueIndex + 1] = rawScale1;
            rawScaleValues[scaleValueIndex + 2] = rawScale2;
            scaleValues[scaleValueIndex] = scale0;
            scaleValues[scaleValueIndex + 1] = scale1;
            scaleValues[scaleValueIndex + 2] = scale2;

            points[pointIndex] = new Point
                                     {
                                         Position = new Vector3(ReadFloat(recordBytes, 0),
                                                                ReadFloat(recordBytes, 1),
                                                                ReadFloat(recordBytes, 2)),
                                         Orientation = orientation,
                                         Scale = new Vector3(scale0, scale1, scale2),
                                         Color = new Vector4(ToRgb(ReadFloat(recordBytes, 6)),
                                                             ToRgb(ReadFloat(recordBytes, 7)),
                                                             ToRgb(ReadFloat(recordBytes, 8)),
                                                             Sigmoid(ReadFloat(recordBytes, 54))),
                                         F1 = 1,
                                         F2 = 1
                                     };
        }

        scaleStats = ScaleStats.Create(scaleValues);
        rawScaleStats = ScaleStats.Create(rawScaleValues);
    }

    private static bool TryReadHeader(Stream stream, out int vertexCount, [NotNullWhen(false)] out string? failureReason)
    {
        vertexCount = 0;
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

        var propertyCount = 0;
        var isReadingVertexProperties = false;

        while (TryReadAsciiLine(stream, out line))
        {
            if (line == "end_header")
            {
                return ValidateHeader(vertexCount, propertyCount, out failureReason);
            }

            if (line.StartsWith("element ", StringComparison.Ordinal))
            {
                isReadingVertexProperties = TryReadVertexElement(line, out vertexCount, out failureReason);
                if (failureReason != null)
                {
                    return false;
                }

                continue;
            }

            if (!isReadingVertexProperties || !line.StartsWith("property ", StringComparison.Ordinal))
            {
                continue;
            }

            if (propertyCount >= ExpectedProperties.Length)
            {
                failureReason = "Unexpected extra vertex property in Gaussian splat PLY.";
                return false;
            }

            if (!IsExpectedFloatProperty(line, ExpectedProperties[propertyCount]))
            {
                failureReason = $"Expected vertex property '{ExpectedProperties[propertyCount]}' at index {propertyCount}.";
                return false;
            }

            propertyCount++;
        }

        failureReason = "PLY header ended before end_header.";
        return false;
    }

    private static bool TryReadVertexElement(string line, out int vertexCount, out string? failureReason)
    {
        vertexCount = 0;
        failureReason = null;

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            failureReason = $"Invalid PLY element declaration: {line}";
            return false;
        }

        if (parts[1] != "vertex")
        {
            return false;
        }

        if (!int.TryParse(parts[2], out vertexCount) || vertexCount <= 0)
        {
            failureReason = $"Invalid PLY vertex count: {parts[2]}";
            return false;
        }

        return true;
    }

    private static bool ValidateHeader(int vertexCount, int propertyCount, [NotNullWhen(false)] out string? failureReason)
    {
        if (vertexCount <= 0)
        {
            failureReason = "PLY header does not declare a positive vertex count.";
            return false;
        }

        if (propertyCount != ExpectedProperties.Length)
        {
            failureReason = $"Expected {ExpectedProperties.Length} float vertex properties, found {propertyCount}.";
            return false;
        }

        failureReason = null;
        return true;
    }

    private static bool IsExpectedFloatProperty(string line, string expectedName)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3
               && parts[0] == "property"
               && parts[1] == "float"
               && parts[2] == expectedName;
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

    private static float ReadFloat(byte[] recordBytes, int floatIndex)
    {
        return BinaryPrimitives.ReadSingleLittleEndian(recordBytes.AsSpan(floatIndex * sizeof(float), sizeof(float)));
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

    private readonly record struct ScaleStats(float Min, float Median, float P95, float P99, float Max)
    {
        public static ScaleStats Create(float[] values)
        {
            Array.Sort(values);
            return new ScaleStats(values[0],
                                  Sample(values, 0.5f),
                                  Sample(values, 0.95f),
                                  Sample(values, 0.99f),
                                  values[^1]);
        }

        public override string ToString()
        {
            return $"min={Min:0.########e+0}, median={Median:0.########e+0}, p95={P95:0.########e+0}, p99={P99:0.########e+0}, max={Max:0.########e+0}";
        }

        private static float Sample(float[] values, float percentile)
        {
            var index = (int)MathF.Round((values.Length - 1) * percentile);
            index = Math.Clamp(index, 0, values.Length - 1);
            return values[index];
        }
    }

    private readonly Resource<GaussianSplatData> _resource;
    private string _warningMessage = string.Empty;

    private const int FloatCount = 62;
    private const int RecordSizeInBytes = FloatCount * sizeof(float);
    private const float C0 = 0.28209479177387814f;
    private static readonly List<byte> _headerLineBuffer = new(128);
    private static readonly string[] FileFilterDefault = ["ply"];

    private static readonly string[] ExpectedProperties =
        [
            "x", "y", "z",
            "nx", "ny", "nz",
            "f_dc_0", "f_dc_1", "f_dc_2",
            "f_rest_0", "f_rest_1", "f_rest_2", "f_rest_3", "f_rest_4",
            "f_rest_5", "f_rest_6", "f_rest_7", "f_rest_8", "f_rest_9",
            "f_rest_10", "f_rest_11", "f_rest_12", "f_rest_13", "f_rest_14",
            "f_rest_15", "f_rest_16", "f_rest_17", "f_rest_18", "f_rest_19",
            "f_rest_20", "f_rest_21", "f_rest_22", "f_rest_23", "f_rest_24",
            "f_rest_25", "f_rest_26", "f_rest_27", "f_rest_28", "f_rest_29",
            "f_rest_30", "f_rest_31", "f_rest_32", "f_rest_33", "f_rest_34",
            "f_rest_35", "f_rest_36", "f_rest_37", "f_rest_38", "f_rest_39",
            "f_rest_40", "f_rest_41", "f_rest_42", "f_rest_43", "f_rest_44",
            "opacity",
            "scale_0", "scale_1", "scale_2",
            "rot_0", "rot_1", "rot_2", "rot_3"
        ];

    [Input(Guid = "5cc67374-1114-48f6-be01-b2edb4770b55")]
    public readonly InputSlot<string> Path = new();
}
