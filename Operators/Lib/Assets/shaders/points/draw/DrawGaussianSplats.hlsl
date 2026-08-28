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

float4 InvalidPosition()
{
    return float4(0, 0, 2, 1);
}

void GetEigenBasis(float a, float b, float c, out float2 axis0, out float2 axis1, out float lambda0, out float lambda1)
{
    float mid = 0.5 * (a + c);
    float radius = length(float2(0.5 * (a - c), b));
    lambda0 = mid + radius;
    lambda1 = max(mid - radius, 0.1);

    axis0 = abs(b) + abs(lambda0 - a) > 1e-8 ? normalize(float2(b, lambda0 - a)) : float2(1, 0);
    axis1 = float2(axis0.y, -axis0.x);
}

bool IsValidFinite(float4 value)
{
    return all(value == value) && all(abs(value) < 1e20);
}

float NormalizedExp(float x)
{
    static const float Exp4 = 0.01831563888873418;
    static const float InvExp4 = 1.018657360363774;
    return (exp(x * -4) - Exp4) * InvExp4;
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
        output.position = InvalidPosition();
        output.gaussianUv = 1000;
        output.color = 0;
        output.fog = 0;
        return output;
    }

    float3 scale = RenderMode < 0.5 ? ConstantWorldScale.xxx : p.Scale;
    scale = abs(scale);
    if (MaxWorldScale > 0)
    {
        scale = min(scale, MaxWorldScale.xxx);
    }
    scale = max(scale * Scale, 0.000001);

    float3 covAxisX = float3(1, 0, 0) * scale.x;
    float3 covAxisY = float3(0, 1, 0) * scale.y;
    float3 covAxisZ = float3(0, 0, 1) * scale.z;
    if (RenderMode > 1.5)
    {
        float4 rotation = length(p.Rotation) > 0.00001 ? normalize(p.Rotation) : QUATERNION_IDENTITY;
        covAxisX = qRotateVec3(float3(1, 0, 0), rotation) * scale.x;
        covAxisY = qRotateVec3(float3(0, 1, 0), rotation) * scale.y;
        covAxisZ = qRotateVec3(float3(0, 0, 1), rotation) * scale.z;
    }

    float viewZ = min(centerCamera.z, -max(NearDepth, 0.0001));
    float focal = ScreenSize.x * CameraToClipSpace[0][0];
    float j1 = focal / viewZ;
    float2 j2 = -j1 / viewZ * centerCamera.xy;

    float3 cameraAxisX = mul(float4(1, 0, 0, 0), ObjectToCamera).xyz;
    float3 cameraAxisY = mul(float4(0, 1, 0, 0), ObjectToCamera).xyz;
    float3 cameraAxisZ = mul(float4(0, 0, 1, 0), ObjectToCamera).xyz;

    float3 tangentX = j1 * cameraAxisX + j2.x * cameraAxisZ;
    float3 tangentY = j1 * cameraAxisY + j2.y * cameraAxisZ;

    float3 covarianceX = float3(dot(covAxisX, tangentX), dot(covAxisY, tangentX), dot(covAxisZ, tangentX));
    float3 covarianceY = float3(dot(covAxisX, tangentY), dot(covAxisY, tangentY), dot(covAxisZ, tangentY));

    float aRaw = dot(covarianceX, covarianceX);
    float b = dot(covarianceX, covarianceY);
    float cRaw = dot(covarianceY, covarianceY);
    float a = aRaw + 0.3;
    float c = cRaw + 0.3;
    float determinant = a * c - b * b;
    if (determinant <= 0 || !IsValidFinite(float4(a, b, c, determinant)))
    {
        output.position = InvalidPosition();
        output.gaussianUv = 1000;
        output.color = 0;
        output.fog = 0;
        return output;
    }

    float2 axis0;
    float2 axis1;
    float lambda0;
    float lambda1;
    GetEigenBasis(a, b, c, axis0, axis1, lambda0, lambda1);

    float vmin = min(1024, min(ScreenSize.x, ScreenSize.y));
    float maxRadiusPixels = MaxRadiusPixels > 0 ? min(MaxRadiusPixels, vmin) : vmin;
    float radius0 = 2 * min(sqrt(2 * lambda0), maxRadiusPixels);
    float radius1 = 2 * min(sqrt(2 * lambda1), maxRadiusPixels);

    float alpha = saturate(p.Color.a * Alpha);
    float alphaCutoff = max(AlphaCutoff, 0.000001);
    if (alpha <= alphaCutoff || max(radius0, radius1) < 2 || !IsValidFinite(float4(axis0, axis1)) || !IsValidFinite(float4(radius0, radius1, alpha, alphaCutoff)))
    {
        output.position = InvalidPosition();
        output.gaussianUv = 1000;
        output.color = 0;
        output.fog = 0;
        return output;
    }

    float clipScale = min(1, sqrt(max(0, log(alpha / alphaCutoff))) * 0.5);
    float2 clippedCorner = corner * clipScale;

    float4 centerClip = mul(float4(centerObject, 1), ObjectToClipSpace);
    float2 centerNdc = centerClip.xy / centerClip.w;
    float2 screenCenter = (centerNdc * 0.5 + 0.5) * ScreenSize;
    float screenCullRadius = max(radius0, radius1) * clipScale;

    if (screenCenter.x + screenCullRadius < 0 || screenCenter.x - screenCullRadius > ScreenSize.x ||
        screenCenter.y + screenCullRadius < 0 || screenCenter.y - screenCullRadius > ScreenSize.y)
    {
        output.position = InvalidPosition();
        output.gaussianUv = 1000;
        output.color = 0;
        output.fog = 0;
        return output;
    }

    float2 pixelOffset = axis0 * clippedCorner.x * radius0 + axis1 * clippedCorner.y * radius1;
    float2 clipOffset = pixelOffset * centerClip.w / max(ScreenSize, 1);

    output.position = centerClip + float4(clipOffset, 0, 0);
    output.gaussianUv = clippedCorner;
    output.color = float4(max(p.Color.rgb, 0), alpha);

    output.fog = pow(saturate(-centerCamera.z / FogDistance), FogBias);

    return output;
}

float4 psMain(psInput input) : SV_TARGET
{
    float a = dot(input.gaussianUv, input.gaussianUv);
    if (a > 1)
        discard;

    float alpha = NormalizedExp(a) * input.color.a;

    if (alpha < AlphaCutoff)
        discard;

    float3 color = lerp(input.color.rgb, FogColor.rgb, input.fog * FogColor.a);
    return saturate(float4(color * alpha, alpha));
}
