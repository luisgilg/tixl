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
    float RenderMode;
    float NearDepth;
    float MaxRadiusPixels;
    float ConstantWorldScale;
    float MaxWorldScale;
    float _padding0;
    float _padding1;
    float _padding2;
    float2 ScreenSize;
    float2 _padding;
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
    return clip.xy / clip.w;
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

bool IsValidFinite(float4 value)
{
    return all(value == value) && all(abs(value) < 1e20);
}

psInput vsMain(uint id : SV_VertexID)
{
    psInput output;

    uint pointId = id / 6;
    uint cornerId = id % 6;
    float2 corner = Corners[cornerId];

    Point p = Points[pointId];
    float3 centerObject = p.Position;
    float4 centerCamera = mul(float4(centerObject, 1), ObjectToCamera);
    bool isVisibleDepth = centerCamera.z < -max(NearDepth, 0.0001);

    if (!isVisibleDepth)
    {
        output.position = float4(0, 0, 0, 0);
        output.gaussianUv = 1000;
        output.color = 0;
        output.fog = 0;
        return output;
    }

    float2 centerNdc = ProjectToNdc(centerObject);

    float3 axisX = float3(1, 0, 0);
    float3 axisY = float3(0, 1, 0);
    float3 axisZ = float3(0, 0, 1);
    if (RenderMode > 1.5)
    {
        axisX = qRotateVec3(axisX, p.Rotation);
        axisY = qRotateVec3(axisY, p.Rotation);
        axisZ = qRotateVec3(axisZ, p.Rotation);
    }

    float3 scale = RenderMode < 0.5 ? ConstantWorldScale.xxx : p.Scale;
    scale = abs(scale);
    if (MaxWorldScale > 0)
    {
        scale = min(scale, MaxWorldScale.xxx);
    }
    scale = max(scale * Scale, 0.000001);

    float2 screenAxisX = ProjectToNdc(centerObject + axisX * scale.x) - centerNdc;
    float2 screenAxisY = ProjectToNdc(centerObject + axisY * scale.y) - centerNdc;
    float2 screenAxisZ = ProjectToNdc(centerObject + axisZ * scale.z) - centerNdc;

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
    float maxRadiusNdc = MaxRadiusPixels * 2 / max(min(ScreenSize.x, ScreenSize.y), 1);
    radius0 = min(radius0, maxRadiusNdc);
    radius1 = min(radius1, maxRadiusNdc);

    bool isFinite = IsValidFinite(float4(centerNdc, radius0, radius1)) && IsValidFinite(float4(axis0, axis1));
    if (!isFinite || radius0 <= 0 || radius1 <= 0)
    {
        output.position = float4(0, 0, 0, 0);
        output.gaussianUv = 1000;
        output.color = 0;
        output.fog = 0;
        return output;
    }

    float2 ndcOffset = axis0 * corner.x * radius0 + axis1 * corner.y * radius1;

    float4 centerClip = mul(float4(centerObject, 1), ObjectToClipSpace);
    output.position = centerClip + float4(ndcOffset * centerClip.w, 0, 0);
    output.gaussianUv = corner * SigmaRadius;
    output.color = float4(p.Color.rgb, p.Color.a * Alpha);

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
