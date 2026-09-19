using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using T3.Core.Operator.Interfaces;

namespace Lib.point.modify;

[Guid("5929a71d-ff97-4cf7-a06d-1b5ab481831c")]
internal sealed class SortGaussianSplatsIndicesCachedKeys : Instance<SortGaussianSplatsIndicesCachedKeys>, ICompoundWithUpdate
{
    [Input(Guid = "d1702ce3-be10-4885-a83d-47f0b254c198")]
    public readonly InputSlot<BufferWithViews> Points = new();

    [Input(Guid = "dbfdf017-3072-4a22-bd37-bc24342d30de")]
    public readonly InputSlot<Object> CameraReference = new();

    [Input(Guid = "e34e21a8-751c-458a-a080-26185ae68dac")]
    public readonly InputSlot<float> SortingSpeed = new();

    [Input(Guid = "0f4ad016-a352-44c6-a7a3-32e67adbc332")]
    public readonly InputSlot<bool> Ascending = new();

    [Output(Guid = "c625b3ed-927b-4bae-ae5b-7b7f23904751")]
    public readonly Slot<BufferWithViews> Output = new();

    [Output(Guid = "faff4dde-43bb-4d7f-9f0a-cbfa7f92c7ca")]
    public readonly Slot<Texture2D> DebugView = new();


    public SortGaussianSplatsIndicesCachedKeys()
    {
        Output.UpdateAction += Update;
    }

    private void Update(EvaluationContext context)
    {
        // The connected ExecuteBufferUpdate generates keys and runs main_sort_cached before exposing its buffer.
        Output.ConnectedUpdate(context);
        var sorted = Output.Value;
        var points = Points.GetValue(context);
        if (sorted?.Buffer == null || points?.Srv == null)
        {
            Output.Value = null;
            return;
        }

        var count = points.Srv.Description.Buffer.ElementCount;
        if (count <= 0 || count > sorted.Buffer.Description.SizeInBytes / sizeof(uint))
        {
            Output.Value = null;
            return;
        }

        if (_view.Buffer != sorted.Buffer || _count != count)
        {
            _view.Srv?.Dispose();
            _view.Uav?.Dispose();
            _view.Buffer = sorted.Buffer;
            _view.Srv = new ShaderResourceView(ResourceManager.Device, sorted.Buffer,
                new ShaderResourceViewDescription
                {
                    Dimension = ShaderResourceViewDimension.Buffer,
                    Format = Format.Unknown,
                    Buffer = new ShaderResourceViewDescription.BufferResource
                    {
                        FirstElement = 0,
                        ElementCount = count,
                    },
                });
            _view.Uav = new UnorderedAccessView(ResourceManager.Device, sorted.Buffer,
                new UnorderedAccessViewDescription
                {
                    Dimension = UnorderedAccessViewDimension.Buffer,
                    Format = Format.Unknown,
                    Buffer = new UnorderedAccessViewDescription.BufferResource
                    {
                        FirstElement = 0,
                        ElementCount = count,
                    },
                });
            _count = count;
        }
        Output.Value = _view;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // The graph owns the underlying buffer; only these bounded views are owned here.
            _view.Srv?.Dispose();
            _view.Uav?.Dispose();
            _view.Srv = null;
            _view.Uav = null;
            _view.Buffer = null;
        }
        base.Dispose(disposing);
    }

    private readonly BufferWithViews _view = new();
    private int _count;
}
