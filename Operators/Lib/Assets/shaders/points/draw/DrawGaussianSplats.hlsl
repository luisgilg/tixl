#include "shared/point.hlsl"
#include "shared/quat-functions.hlsl"

static const float2 Corners[] =
{
    float2(-1, -1),
    float2(1, -1),
    float2(1, 1),
    float2(1, 1),
    float2(-1, 1),
    float2(-1, -1),
};

cbuffer Transforms : register(b0)
{
    float4x4 CameraToClipSpace;
    float4x4 ClipSpaceToCamera;
    float4x4 WorldToCamera;
    float4x4 CameraToWorld;
    float4x4 WorldToClipSpace;
    float4x4 ClipSpaceToWorld;
    float4x4 ObjectToWorld;
    float4x4 WorldToObject;
    float4x4 ObjectToCamera;
    float4x4 ObjectToClipSpace;
};

cbuffer Params : register(b1)
{
    float Scale;
    float SigmaRadius;
    float Alpha;
    float AlphaCutoff;
};

cbuffer FogParams : register(b2)
{
    float4 FogColor;
    float FogDistance;
    float FogBias;
};

struct psInput
{
    float4 position : SV_POSITION;
    float4 color : COLOR;
    float2 gaussianUv : TEXCOORD0;
    float fog : FOG;
};

StructuredBuffer<Point> Points : t0;

float2 ProjectToNdc(float3 objectPosition)
{
    float4 clip = mul(float4(objectPosition, 1), ObjectToClipSpace);
    return clip.xy / max(abs(clip.w), 0.00001);
}

float3x3 RotationMatrixFromQuaternion(float4 q)
{
    q = normalize(q);
    float x = q.x;
    float y = q.y;
    float z = q.z;
    float w = q.w;

    return float3x3(
        1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w),
        2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w),
        2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y));
}

void GetEigenBasis(float a, float b, float c, out float2 axis0, out float2 axis1, out float lambda0, out float lambda1)
{
    float trace = a + c;
    float delta = sqrt(max((a - c) * (a - c) * 0.25 + b * b, 0));
    lambda0 = max(trace * 0.5 + delta, 1e-10);
    lambda1 = max(trace * 0.5 - delta, 1e-10);

    axis0 = abs(b) + abs(lambda0 - a) > 1e-8 ? normalize(float2(b, lambda0 - a)) : float2(1, 0);
    axis1 = float2(-axis0.y, axis0.x);
}

psInput vsMain(uint id : SV_VertexID)
{
    psInput output;

    uint pointId = id / 6;
    uint cornerId = id % 6;
    float2 corner = Corners[cornerId];

    Point p = Points[pointId];
    float3 centerObject = p.Position;
    float2 centerNdc = ProjectToNdc(centerObject);

    float3x3 rotation = RotationMatrixFromQuaternion(p.Rotation);
    float3 scale = max(abs(p.Scale * Scale), 0.000001);

    float2 screenAxisX = ProjectToNdc(centerObject + rotation[0] * scale.x) - centerNdc;
    float2 screenAxisY = ProjectToNdc(centerObject + rotation[1] * scale.y) - centerNdc;
    float2 screenAxisZ = ProjectToNdc(centerObject + rotation[2] * scale.z) - centerNdc;

    float a = dot(float3(screenAxisX.x, screenAxisY.x, screenAxisZ.x), float3(screenAxisX.x, screenAxisY.x, screenAxisZ.x));
    float b = dot(float3(screenAxisX.x, screenAxisY.x, screenAxisZ.x), float3(screenAxisX.y, screenAxisY.y, screenAxisZ.y));
    float c = dot(float3(screenAxisX.y, screenAxisY.y, screenAxisZ.y), float3(screenAxisX.y, screenAxisY.y, screenAxisZ.y));

    float2 axis0;
    float2 axis1;
    float lambda0;
    float lambda1;
    GetEigenBasis(a, b, c, axis0, axis1, lambda0, lambda1);

    float radius0 = sqrt(lambda0) * SigmaRadius;
    float radius1 = sqrt(lambda1) * SigmaRadius;
    float2 ndcOffset = axis0 * corner.x * radius0 + axis1 * corner.y * radius1;

    float4 centerClip = mul(float4(centerObject, 1), ObjectToClipSpace);
    output.position = centerClip + float4(ndcOffset * centerClip.w, 0, 0);
    output.gaussianUv = corner * SigmaRadius;
    output.color = float4(p.Color.rgb, p.Color.a * Alpha);

    float4 centerCamera = mul(float4(centerObject, 1), ObjectToCamera);
    output.fog = pow(saturate(-centerCamera.z / FogDistance), FogBias);

    return output;
}

float4 psMain(psInput input) : SV_TARGET
{
    float falloff = exp(-0.5 * dot(input.gaussianUv, input.gaussianUv));
    float4 color = float4(input.color.rgb, input.color.a * falloff);

    if (color.a < AlphaCutoff)
        discard;

    color.rgb = lerp(color.rgb, FogColor.rgb, input.fog * FogColor.a);
    return saturate(color);
}
