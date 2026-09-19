// Share c2k, GetSwapPair and constant-buffer layouts with the reference sorter.
#include "points/modify/SortGaussianSplats.hlsl"

StructuredBuffer<float> DepthKeys : t0;
RWStructuredBuffer<float> GeneratedDepthKeys : u0;

[numthreads(64, 1, 1)]
void GenerateDepthKeys(uint3 i : SV_DispatchThreadID)
{
    uint count, stride;
    SourcePoints.GetDimensions(count, stride);
    if (i.x >= count)
        return;

    GeneratedDepthKeys[i.x] = c2k(SourcePoints[i.x]);
}

[numthreads(64, 1, 1)] void main_sort_cached(uint3 i : SV_DispatchThreadID)
{
    uint numStructs, stride;
    IndexBuffer.GetDimensions(numStructs, stride);
    if (i.x >= numStructs)
        return;

    uint TotalPoints=numStructs;
    if(i.x*2+1>=numStructs)return;
    
    uint PassIndex=uint(CallIndex);
    uint pid=max(0,FrameIndex*CallCount+PassIndex);


    int2 pair=GetSwapPair(i.x,pid,TotalPoints);
    int idx1=pair.x;
    int idx2=pair.y;

    uint pi1=IndexBuffer[idx1].x;
    uint pi2=IndexBuffer[idx2].x;
    //reset on first dispatch only
    if(Reset&&CallIndex==0){pi1=idx1;pi2=idx2;}

    if(uint(pi2)>=TotalPoints)return;
    if(uint(idx2)>=TotalPoints)return;

    //dont swap elements outsize of input point buffer
    //still need to write index value for first dispatch
    uint InputPointCount;
    DepthKeys.GetDimensions(InputPointCount, stride);
    if(max(pi1,pi2)>=InputPointCount){
        IndexBuffer[idx1]=pi1;
        IndexBuffer[idx2]=pi2;
        return;
    }

    float k1=DepthKeys[pi1];
    float k2=DepthKeys[pi2];


    // uint totalsteps=uint(round(log2(TotalPoints)));
    // totalsteps=totalsteps*(totalsteps+1)/2;

    PassIndex=(PassIndex+1);
    if(k1<k2){
        IndexBuffer[idx1]=pi2;
        IndexBuffer[idx2]=pi1;
        // IndexBuffer[idx1]=uint2(pi2,0);
        // IndexBuffer[idx2]=uint2(pi1,0);
    }else{
        // if(Reset){IndexBuffer[idx1]=pi1;IndexBuffer[idx2]=pi2;}
        //may not need to write this? only if reset
        IndexBuffer[idx1]=pi1;
        IndexBuffer[idx2]=pi2;
        // IndexBuffer[idx1]=uint2(pi1,0);
        // IndexBuffer[idx2]=uint2(pi2,0);
    }
    
}
