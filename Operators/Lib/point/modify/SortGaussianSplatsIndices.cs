using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using T3.Core.Operator.Interfaces;

namespace Lib.point.modify;

[Guid("b9be73a1-844b-4d71-9a7f-6cddd90e9132")]
internal sealed class SortGaussianSplatsIndices : Instance<SortGaussianSplatsIndices>, ICompoundWithUpdate
{
    [Input(Guid = "17ddc201-2619-4225-a8ff-505db567b8ff")]
    public readonly InputSlot<BufferWithViews> Points = new();

    [Input(Guid = "4ed5d254-aa31-4e11-94da-0206669e662a")]
    public readonly InputSlot<Object> CameraReference = new();

    [Input(Guid = "600c70c8-493d-477f-a2fe-97500f2bad44")]
    public readonly InputSlot<float> SortingSpeed = new();

    [Input(Guid = "0de7820a-52e2-4780-b364-1de819dbd47c")]
    public readonly InputSlot<bool> Ascending = new();

    [Output(Guid = "54d9e6b9-ddc1-4e2c-b17c-210fbc6871a1")]
    public readonly Slot<BufferWithViews> Output = new();

    [Output(Guid = "444b8e98-955c-45ed-bd76-b3d14c61557b")]
    public readonly Slot<Texture2D> DebugView = new();


    public SortGaussianSplatsIndices()
    {
        Output.UpdateAction += Update;
    }

    private void Update(EvaluationContext context)
    {
        // The connected ExecuteBufferUpdate runs main_sort before exposing its buffer.
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
