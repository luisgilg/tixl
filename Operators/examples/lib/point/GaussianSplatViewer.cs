namespace Examples.Lib.point;

[Guid("d93d39c1-b79a-4b86-82cd-4dcba13bbbd0")]
internal sealed class GaussianSplatViewer : Instance<GaussianSplatViewer>
{
    [Output(Guid = "2cedb866-1259-4df0-8b52-f61c9cb0a59e")]
    public readonly Slot<Texture2D> Output = new();
}
