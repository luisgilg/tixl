using T3.Core.Operator;
using T3.Core.Operator.Attributes;
using T3.Core.Operator.Slots;
using System.Runtime.InteropServices;

namespace Lib.point.modify{
    [Guid("8b3a194b-1afb-46cd-b5a9-5dfe781525dc")]
    internal sealed class SortGaussianSplats :Instance<SortGaussianSplats>    {

        [Input(Guid = "6e6b2f80-76c4-4f65-b994-ada4f3248d0e")]
        public readonly InputSlot<T3.Core.DataTypes.BufferWithViews> Points = new InputSlot<T3.Core.DataTypes.BufferWithViews>();

        [Input(Guid = "9370c5d1-a187-4eb2-b90f-655d1f32e25c")]
        public readonly InputSlot<Object> CameraReference = new InputSlot<Object>();

        [Input(Guid = "a138c621-5f56-4f31-9d9d-5e4e2c6ad0d6")]
        public readonly InputSlot<float> SortingSpeed = new InputSlot<float>();

        [Input(Guid = "0055e9b7-c9de-469b-a8be-e865f5ad4532")]
        public readonly InputSlot<bool> Ascending = new InputSlot<bool>();

        [Output(Guid = "ec7dc2b8-5ab0-45a5-b6d4-dd0961ec3155")]
        public readonly Slot<T3.Core.DataTypes.BufferWithViews> Output = new Slot<T3.Core.DataTypes.BufferWithViews>();

        [Output(Guid = "df294458-08b5-4f1d-89f7-cf90fed96a10")]
        public readonly Slot<T3.Core.DataTypes.Texture2D> DebugView = new Slot<T3.Core.DataTypes.Texture2D>();

    }
}

