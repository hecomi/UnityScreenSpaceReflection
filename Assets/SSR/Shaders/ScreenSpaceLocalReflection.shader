Shader "Hidden/ScreenSpaceLocalReflection" 
{

Properties
{
    _MainTex("Base (RGB)", 2D) = "" {}
}

SubShader 
{

Blend Off
ZTest Always
ZWrite Off
Cull Off

CGINCLUDE

#include "UnityCG.cginc"

sampler2D _MainTex;
sampler2D _CameraGBufferTexture0; // rgb: diffuse,  a: occlusion
sampler2D _CameraGBufferTexture1; // rgb: specular, a: smoothness
sampler2D _CameraGBufferTexture2; // rgb: normal,   a: unused
sampler2D _CameraGBufferTexture3; // rgb: emission, a: unused
sampler2D _CameraDepthTexture;

inline float4 GetAlbedo(float2 uv)     { return tex2D(_CameraGBufferTexture0, uv); }
inline float4 GetOcculusion(float2 uv) { return tex2D(_CameraGBufferTexture0, uv).w; }
inline float3 GetSpecular(float2 uv)   { return tex2D(_CameraGBufferTexture1, uv).xyz; }
inline float3 GetSmoothness(float2 uv) { return tex2D(_CameraGBufferTexture1, uv).w; }
inline float3 GetNormal(float2 uv)     { return tex2D(_CameraGBufferTexture2, uv).xyz * 2.0 - 1.0; }
inline float3 GetEmission(float2 uv)   { return tex2D(_CameraGBufferTexture3, uv).xyz; }
inline float  GetDepth(float2 uv)      { return SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv); }

sampler2D _ReflectionTexture;
sampler2D _PreDepthTexture;
sampler2D _PreAccumulationTexture;
sampler2D _AccumulationTexture;
sampler2D _SmoothnessTexture;
float4 _ReflectionTexture_TexelSize;

#if QUALITY_HIGH
    #define RAYTRACE_LOOP_NUM 50
#elif QUALITY_MIDDLE
    #define RAYTRACE_LOOP_NUM 30
#else // QUALITY_LOW
    #define RAYTRACE_LOOP_NUM 10
#endif

float4 _Params1;
#define _RaytraceMaxLength        _Params1.x
#define _RaytraceMaxThickness     _Params1.y
#define _ReflectionEnhancer       _Params1.z
#define _AccumulationBlendRatio   _Params1.w

float4 _BlurParams;
#define _BlurOffset _BlurParams.xy
#define _BlurNum (int)(_BlurParams.z)

float4x4 _InvViewProj;
float4x4 _ViewProj;
float4x4 _PreViewProj;

float ComputeDepth(float4 clippos)
{
#if defined(SHADER_TARGET_GLSL) || defined(SHADER_API_GLES) || defined(SHADER_API_GLES3)
    return (clippos.z / clippos.w) * 0.5 + 0.5;
#else
    return clippos.z / clippos.w;
#endif
}

float noise(float2 seed)
{
    return frac(sin(dot(seed.xy, float2(12.9898, 78.233))) * 43758.5453);
}

struct appdata
{
    float4 vertex : POSITION;
    float2 uv : TEXCOORD0;
};

struct v2f
{
    float4 vertex : SV_POSITION;
    float4 screenPos : TEXCOORD0;
};

v2f vert(appdata v)
{
    v2f o;
    o.vertex = mul(UNITY_MATRIX_MVP, v.vertex);
    o.screenPos = ComputeScreenPos(o.vertex);
    return o;
}

v2f vert_fullscreen(appdata v)
{
    v2f o;
    o.vertex = v.vertex;
    o.screenPos = ComputeScreenPos(o.vertex);
    return o;
}

float4 frag_reflection(v2f i) : SV_Target
{
    float2 uv = i.screenPos.xy / i.screenPos.w;
    float4 col = float4(tex2D(_MainTex, uv).xyz, 0);

    float depth = GetDepth(uv);
    if (depth >= 1.0) return col;

    float2 spos = 2.0 * uv - 1.0;
    float4 pos = mul(_InvViewProj, float4(spos, depth, 1.0));
    pos = pos / pos.w;

    float3 camDir = normalize(pos - _WorldSpaceCameraPos);
    float3 normal = GetNormal(uv);
    float3 refDir = normalize(camDir - 2.0 * dot(camDir, normal) * normal);

    int maxRayNum = RAYTRACE_LOOP_NUM;
    float maxLength = _RaytraceMaxLength;
    float3 step = maxLength / maxRayNum * refDir;
    float maxThickness = _RaytraceMaxThickness / maxRayNum;

    for (int n = 1; n <= maxRayNum; ++n) {
        float3 ray = (n + noise(uv + _Time.x)) * step;
        float3 rayPos = pos + ray;
        float4 vpPos = mul(_ViewProj, float4(rayPos, 1.0));
        float2 rayUv = vpPos.xy / vpPos.w * 0.5 + 0.5;
        if (max(abs(rayUv.x - 0.5), abs(rayUv.y - 0.5)) > 0.5) break;
        float rayDepth = ComputeDepth(vpPos);
        float gbufferDepth = GetDepth(rayUv);
        if (rayDepth - gbufferDepth > 0 && rayDepth - gbufferDepth < maxThickness) {
            float edgeFactor = 1.0 - pow(2.0 * length(rayUv - 0.5), 2);
            float a = pow(min(1.0, (maxLength / 2) / length(ray)), 2.0) * edgeFactor;
            a *= _ReflectionEnhancer * pow(length(rayUv - 0.5) / 0.5, 0.5);
            col = float4(tex2D(_MainTex, rayUv).xyz, a);
            break;
        }
    }

    return col;
}

float4 frag_blur(v2f i) : SV_Target
{
    float2 uv = i.screenPos.xy / i.screenPos.w;
    float2 size = _ReflectionTexture_TexelSize;

    float4 col = 0.0;
    for (int n = -_BlurNum; n <= _BlurNum; ++n) {
        col += tex2D(_ReflectionTexture, uv + _BlurOffset * size * n);
    }
    return col / (_BlurNum * 2 + 1);
}

float4 frag_accumulation(v2f i) : SV_Target
{
    // 現在のワールド座標を復元
    float2 uv = i.screenPos.xy / i.screenPos.w;
    float depth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
    if (depth >= 1.0) return float4(0, 0, 0, 0);
    float2 spos = 2.0 * uv - 1.0;
    float4 pos = mul(_InvViewProj, float4(spos, depth, 1.0));
    pos = pos / pos.w;

    // 前の UV を復元
    float4 preVpPos = mul(_PreViewProj, pos);
    float2 preUv = preVpPos.xy / preVpPos.w * 0.5 + 0.5;

    float4 accumulation = tex2D(_PreAccumulationTexture, preUv);
    float4 reflection = tex2D(_ReflectionTexture, uv);
    return lerp(accumulation, reflection, _AccumulationBlendRatio);
}

float4 frag_composition(v2f i) : SV_Target
{
    float2 uv = i.screenPos.xy / i.screenPos.w;
    float4 base = tex2D(_MainTex, uv);

    float4 reflection = tex2D(_AccumulationTexture, uv);
#if USE_SMOOTHNESS
    float smoothness = GetSmoothness(uv);
    float4 smoothed = tex2D(_SmoothnessTexture, uv);
    reflection = lerp(smoothed, reflection, smoothness);
#endif

    float4 a = float4(GetSpecular(uv), 1.0) * reflection.a;
    return lerp(base, reflection, a);
}

ENDCG

Pass 
{
    CGPROGRAM
    #pragma multi_compile QUALITY_HIGH QUALITY_MIDDLE QUALITY_LOW
    #pragma vertex vert
    #pragma fragment frag_reflection
    ENDCG
}

Pass 
{
    CGPROGRAM
    #pragma vertex vert_fullscreen
    #pragma fragment frag_blur
    ENDCG
}

Pass 
{
    CGPROGRAM
    #pragma vertex vert_fullscreen
    #pragma fragment frag_accumulation
    ENDCG
}

Pass 
{
    CGPROGRAM
    #pragma multi_compile __ USE_SMOOTHNESS
    #pragma vertex vert
    #pragma fragment frag_composition
    ENDCG
}

}

}
