#nullable enable
using T3.Core.Rendering;
using T3.Core.Utils;
using DepthStencilStateDescription = SharpDX.Direct3D11.DepthStencilStateDescription;
using DepthWriteMask = SharpDX.Direct3D11.DepthWriteMask;
using RasterizerStateDescription = SharpDX.Direct3D11.RasterizerStateDescription;
using RawColor4 = SharpDX.Mathematics.Interop.RawColor4;

namespace Lib.point.draw;

[Guid("85d6ce29-d4f8-4bb3-84ef-05f88ce269c6")]
internal sealed class DrawGaussianSplats : Instance<DrawGaussianSplats>
{
    [Output(Guid = "6bb912b7-46d9-4327-bfa1-786ab2526412")]
    public readonly Slot<Command> Output = new(new Command());

    public DrawGaussianSplats()
    {
        _vertexShader = ResourceManager.CreateShaderResource<VertexShader>(ShaderPath, this, static () => "vsMain");
        _pixelShader = ResourceManager.CreateShaderResource<PixelShader>(ShaderPath, this, static () => "psMain");

        Output.UpdateAction += Update;
        Output.Value.RestoreAction = Restore;
    }

    private void Update(EvaluationContext context)
    {
        var points = GPoints.GetValue(context);
        if (points?.Buffer == null || points.Srv == null)
        {
            return;
        }

        var pointCount = points.Buffer.Description.SizeInBytes / Point.Stride;
        if (pointCount <= 0)
        {
            return;
        }

        EnsureResources();

        if (!_vertexShader.TryGetValue(context, out var vertexShader) ||
            !_pixelShader.TryGetValue(context, out var pixelShader))
        {
            return;
        }

        UpdateConstantBuffers(context);

        var deviceContext = ResourceManager.Device.ImmediateContext;
        var iaStage = deviceContext.InputAssembler;
        var vsStage = deviceContext.VertexShader;
        var psStage = deviceContext.PixelShader;
        var outputMerger = deviceContext.OutputMerger;
        var rasterizer = deviceContext.Rasterizer;

        _previousTopology = iaStage.PrimitiveTopology;
        _previousVertexShader = vsStage.Get();
        _previousPixelShader = psStage.Get();
        _previousVertexShaderResources = vsStage.GetShaderResources(0, 1);
        _previousPixelShaderResources = psStage.GetShaderResources(0, 1);
        _previousVertexConstantBuffers = vsStage.GetConstantBuffers(0, 3);
        _previousPixelConstantBuffers = psStage.GetConstantBuffers(0, 3);
        _previousBlendState = outputMerger.GetBlendState(out _previousBlendFactor, out _previousSampleMask);
        _previousDepthStencilState = outputMerger.DepthStencilState;
        _previousRasterizerState = rasterizer.State;

        iaStage.PrimitiveTopology = PrimitiveTopology.TriangleList;

        vsStage.Set(vertexShader);
        psStage.Set(pixelShader);
        vsStage.SetShaderResource(0, points.Srv);
        psStage.SetShaderResource(0, points.Srv);
        vsStage.SetConstantBuffers(0, 3, _constantBuffers);
        psStage.SetConstantBuffers(0, 3, _constantBuffers);

        outputMerger.SetBlendState(GetBlendState(context), DefaultRenderingStates.DefaultBlendFactor);
        outputMerger.SetDepthStencilState(GetDepthStencilState());
        rasterizer.State = _rasterizerState;

        deviceContext.Draw(pointCount * VerticesPerSplat, 0);
    }

    private void Restore(EvaluationContext context)
    {
        var deviceContext = ResourceManager.Device.ImmediateContext;
        var iaStage = deviceContext.InputAssembler;
        var vsStage = deviceContext.VertexShader;
        var psStage = deviceContext.PixelShader;

        iaStage.PrimitiveTopology = _previousTopology;
        vsStage.Set(_previousVertexShader);
        psStage.Set(_previousPixelShader);

        if (_previousVertexShaderResources.Length > 0)
        {
            vsStage.SetShaderResources(0, _previousVertexShaderResources.Length, _previousVertexShaderResources);
        }

        if (_previousPixelShaderResources.Length > 0)
        {
            psStage.SetShaderResources(0, _previousPixelShaderResources.Length, _previousPixelShaderResources);
        }

        if (_previousVertexConstantBuffers.Length > 0)
        {
            vsStage.SetConstantBuffers(0, _previousVertexConstantBuffers.Length, _previousVertexConstantBuffers);
        }

        if (_previousPixelConstantBuffers.Length > 0)
        {
            psStage.SetConstantBuffers(0, _previousPixelConstantBuffers.Length, _previousPixelConstantBuffers);
        }

        deviceContext.OutputMerger.SetBlendState(_previousBlendState, _previousBlendFactor, _previousSampleMask);
        deviceContext.OutputMerger.SetDepthStencilState(_previousDepthStencilState);
        deviceContext.Rasterizer.State = _previousRasterizerState;
    }

    private void EnsureResources()
    {
        if (_transformBuffer == null || _transformBuffer.IsDisposed)
        {
            _transformBuffer = CreateConstantBuffer<TransformBufferLayout>();
        }

        if (_paramsBuffer == null || _paramsBuffer.IsDisposed)
        {
            _paramsBuffer = CreateConstantBuffer<ParamsBufferLayout>();
        }

        if (_depthReadOnlyState == null || _depthReadOnlyState.IsDisposed)
        {
            var depthStencilDescription = new DepthStencilStateDescription
                                              {
                                                  IsDepthEnabled = true,
                                                  DepthWriteMask = DepthWriteMask.Zero,
                                                  DepthComparison = Comparison.Less,
                                              };
            _depthReadOnlyState = new DepthStencilState(ResourceManager.Device, depthStencilDescription);
        }

        if (_rasterizerState == null || _rasterizerState.IsDisposed)
        {
            var rasterizerDescription = new RasterizerStateDescription
                                            {
                                                FillMode = FillMode.Solid,
                                                CullMode = CullMode.None,
                                                IsDepthClipEnabled = true
                                            };
            _rasterizerState = new RasterizerState(ResourceManager.Device, rasterizerDescription);
        }

        _constantBuffers[0] = _transformBuffer;
        _constantBuffers[1] = _paramsBuffer;
        _constantBuffers[2] = null;
    }

    private void UpdateConstantBuffers(EvaluationContext context)
    {
        var transformData = TryGetCamera(context, out var camera)
                                ? new TransformBufferLayout(camera.CameraToClipSpace, camera.WorldToCamera, context.ObjectToWorld)
                                : new TransformBufferLayout(context.CameraToClipSpace, context.WorldToCamera, context.ObjectToWorld);

        var paramsData = new ParamsBufferLayout
                             {
                                 Scale = Scale.GetValue(context),
                                 SigmaRadius = SigmaRadius.GetValue(context),
                                 Alpha = Alpha.GetValue(context),
                                 AlphaCutoff = AlphaCutoff.GetValue(context),
                                 RenderMode = (float)RenderMode.GetEnumValue<RenderModes>(context),
                                 NearDepth = NearDepth.GetValue(context),
                                 MaxRadiusPixels = MaxRadiusPixels.GetValue(context),
                                 ConstantWorldScale = ConstantWorldScale.GetValue(context),
                                 MaxWorldScale = MaxWorldScale.GetValue(context),
                                 ScreenSize = new Vector2(context.RequestedResolution.Width, context.RequestedResolution.Height),
                             };

        ResourceManager.UpdateConstBuffer(transformData, _transformBuffer!);
        ResourceManager.UpdateConstBuffer(paramsData, _paramsBuffer!);
        _constantBuffers[2] = context.FogParameters;
    }

    private bool TryGetCamera(EvaluationContext context, [NotNullWhen(true)] out ICameraPropertiesProvider? camera)
    {
        camera = null;
        if (!CameraReference.HasInputConnections)
        {
            CameraReference.DirtyFlag.Clear();
            return false;
        }

        if (CameraReference.GetValue(context) is not ICameraPropertiesProvider cameraProvider)
        {
            return false;
        }

        camera = cameraProvider;
        return true;
    }

    private BlendState GetBlendState(EvaluationContext context)
    {
        return BlendMode.GetEnumValue<SharedEnums.BlendModes>(context) switch
                   {
                       SharedEnums.BlendModes.Additive => DefaultRenderingStates.AdditiveBlendState,
                       SharedEnums.BlendModes.None => DefaultRenderingStates.DisabledBlendState,
                       _ => DefaultRenderingStates.DefaultBlendState
                   };
    }

    private DepthStencilState GetDepthStencilState()
    {
        if (!EnableDepthTest.Value)
        {
            return DefaultRenderingStates.DisabledDepthStencilState;
        }

        return EnableDepthWrite.Value ? DefaultRenderingStates.DefaultDepthStencilState : _depthReadOnlyState!;
    }

    private static Buffer CreateConstantBuffer<T>() where T : unmanaged
    {
        var size = Marshal.SizeOf<T>();
        size = (size + 15) & ~15;
        return new Buffer(ResourceManager.Device,
                          size,
                          ResourceUsage.Default,
                          BindFlags.ConstantBuffer,
                          CpuAccessFlags.None,
                          ResourceOptionFlags.None,
                          0);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _vertexShader.Dispose();
            _pixelShader.Dispose();
            _transformBuffer?.Dispose();
            _paramsBuffer?.Dispose();
            _depthReadOnlyState?.Dispose();
            _rasterizerState?.Dispose();
        }

        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ParamsBufferLayout
    {
        public float Scale;
        public float SigmaRadius;
        public float Alpha;
        public float AlphaCutoff;
        public float RenderMode;
        public float NearDepth;
        public float MaxRadiusPixels;
        public float ConstantWorldScale;
        public float MaxWorldScale;
        private float _padding0;
        private float _padding1;
        private float _padding2;
        public Vector2 ScreenSize;
        private Vector2 _padding;
    }

    private enum RenderModes
    {
        ConstantScaleIdentity,
        PointScaleIdentity,
        PointScaleOrientation,
    }

    private const int VerticesPerSplat = 6;
    private const string ShaderPath = "Lib:shaders/points/draw/DrawGaussianSplats.hlsl";

    private readonly Resource<VertexShader> _vertexShader;
    private readonly Resource<PixelShader> _pixelShader;
    private Buffer? _transformBuffer;
    private Buffer? _paramsBuffer;
    private DepthStencilState? _depthReadOnlyState;
    private RasterizerState? _rasterizerState;
    private readonly Buffer?[] _constantBuffers = new Buffer?[3];

    private PrimitiveTopology _previousTopology;
    private SharpDX.Direct3D11.VertexShader? _previousVertexShader;
    private SharpDX.Direct3D11.PixelShader? _previousPixelShader;
    private ShaderResourceView[] _previousVertexShaderResources = [];
    private ShaderResourceView[] _previousPixelShaderResources = [];
    private Buffer[] _previousVertexConstantBuffers = [];
    private Buffer[] _previousPixelConstantBuffers = [];
    private BlendState? _previousBlendState;
    private RawColor4 _previousBlendFactor;
    private int _previousSampleMask;
    private DepthStencilState? _previousDepthStencilState;
    private RasterizerState? _previousRasterizerState;

    [Input(Guid = "43b623de-9edb-4bad-a633-880170d624ce")]
    public readonly InputSlot<BufferWithViews> GPoints = new();

    [Input(Guid = "48a01244-bdcd-40c4-b2bc-8c5cafcf6098")]
    public readonly InputSlot<Object> CameraReference = new();

    [Input(Guid = "c9dfc67c-b89e-4762-8203-718dedaf223b")]
    public readonly InputSlot<float> Scale = new();

    [Input(Guid = "283c5376-fcc2-4aae-a1cb-4ad5876b5678")]
    public readonly InputSlot<float> SigmaRadius = new();

    [Input(Guid = "138a7e18-6cfe-4afb-8401-02e28a27087a")]
    public readonly InputSlot<float> Alpha = new();

    [Input(Guid = "3c35907c-029b-48eb-9863-eaf37eef4d7f")]
    public readonly InputSlot<float> AlphaCutoff = new();

    [Input(Guid = "6b49399a-51c7-4b70-b4b2-5e6021280ada", MappedType = typeof(SharedEnums.BlendModes))]
    public readonly InputSlot<int> BlendMode = new();

    [Input(Guid = "ef885146-5ca2-4f9c-8e95-86b09819324c")]
    public readonly InputSlot<bool> EnableDepthWrite = new();

    [Input(Guid = "7e17fb74-0585-498b-bd1e-8c17d3de5098")]
    public readonly InputSlot<bool> EnableDepthTest = new();

    [Input(Guid = "9ed71782-d249-4d59-9f85-6dc4c33aeb11", MappedType = typeof(RenderModes))]
    public readonly InputSlot<int> RenderMode = new();

    [Input(Guid = "61d0f95c-36a8-444a-91d4-cb2103a9526e")]
    public readonly InputSlot<float> NearDepth = new();

    [Input(Guid = "5c1d7c14-fd03-4c2c-9bb2-b1077e2e78de")]
    public readonly InputSlot<float> MaxRadiusPixels = new();

    [Input(Guid = "9147b324-5063-49a6-b8ec-61f21d168478")]
    public readonly InputSlot<float> ConstantWorldScale = new();

    [Input(Guid = "3cee476c-973e-44d0-bce0-817371206e1a")]
    public readonly InputSlot<float> MaxWorldScale = new();
}
