Shader "Custom/ToonLitURP"
{
    Properties
    {
        _BaseMap ("Base Color (sRGB)", 2D) = "white" {}
        _BaseColor ("Tint", Color) = (1,1,1,1)

        [Normal]_BumpMap ("Normal Map", 2D) = "bump" {}
        _BumpScale ("Normal Scale", Range(0,2)) = 1

        _RoughnessTex ("Roughness (R)", 2D) = "white" {}
        _Roughness ("Roughness Fallback", Range(0,1)) = 0.5
        _RoughnessPower ("Roughness Power", Range(0.5,8)) = 3

        _RampTex ("Shading Ramp (sRGB)", 2D) = "white" {}

        _SpecColor ("Spec Color", Color) = (1,1,1,1)
        _SpecWidthMin ("Spec Width Min", Range(0,1)) = 0.02
        _SpecWidthMax ("Spec Width Max", Range(0,1)) = 0.25
        _SpecThreshMin ("Spec Threshold Min", Range(0,1)) = 0.40
        _SpecThreshMax ("Spec Threshold Max", Range(0,1)) = 0.78

        _RimColor ("Rim Color", Color) = (1,1,1,1)
        _RimMin ("Rim Min", Range(0,1)) = 0.40
        _RimMax ("Rim Max", Range(0,1)) = 0.90
        _RimIntensity ("Rim Intensity", Range(0,2)) = 0.15

        // 环境/GI 强度与最小补光
        _AmbientStrength ("Ambient (SH) Strength", Range(0,2)) = 0.35
        _MinLight ("Fallback Min Light", Range(0,0.3)) = 0.03
    }

    SubShader
    {
        Tags
        {
            "RenderType"="Opaque"
            "Queue"="Geometry"
            "RenderPipeline"="UniversalPipeline"
            "UniversalMaterialType"="Lit"
        }
        LOD 300

        Pass
        {
            Name "ForwardLit"
            Tags{ "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   vert
            #pragma fragment frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

            TEXTURE2D(_BaseMap);       SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap);       SAMPLER(sampler_BumpMap);
            TEXTURE2D(_RoughnessTex);  SAMPLER(sampler_RoughnessTex);
            TEXTURE2D(_RampTex);       SAMPLER(sampler_RampTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float  _BumpScale;

                float  _Roughness;
                float  _RoughnessPower;

                float4 _SpecColor;
                float  _SpecWidthMin;
                float  _SpecWidthMax;
                float  _SpecThreshMin;
                float  _SpecThreshMax;

                float4 _RimColor;
                float  _RimMin;
                float  _RimMax;
                float  _RimIntensity;

                // 补光
                float  _AmbientStrength;
                float  _MinLight;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float3 posWS       : TEXCOORD1;
                half3  normalWS    : TEXCOORD2;
                half3  tangentWS   : TEXCOORD3;
                half3  bitangentWS : TEXCOORD4;
                float4 shadowCoord : TEXCOORD5;
                float  fogFactor   : TEXCOORD6;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            inline float SampleRoughness(float2 uv)
            {
                float r = SAMPLE_TEXTURE2D(_RoughnessTex, sampler_RoughnessTex, uv).r;
                r = lerp(_Roughness, r, 1.0);
                return saturate(r);
            }

            inline float3 SampleAlbedo(float2 uv)
            {
                float3 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv).rgb;
                return albedo * _BaseColor.rgb;
            }

            inline float3 NormalTangentToWorld(float3 nTS, half3 tWS, half3 bWS, half3 nWS)
            {
                float3x3 TBN = float3x3(tWS, bWS, nWS);
                return normalize(mul(nTS, TBN));
            }

            inline float3 EvaluateSpecBand(float3 N, float3 V, float3 L, float roughness)
            {
                float gloss = pow(saturate(1.0 - roughness), _RoughnessPower);
                float width  = lerp(_SpecWidthMax,  _SpecWidthMin,  gloss);
                float thresh = lerp(_SpecThreshMin, _SpecThreshMax, gloss);
                float3 H = normalize(L + V);
                float ndh = saturate(dot(N, H));
                float band = smoothstep(thresh - width, thresh + width, ndh);
                return band * _SpecColor.rgb;
            }

            inline float3 EvaluateRim(float3 N, float3 V)
            {
                float rimTerm = 1.0 - saturate(dot(N, V));
                float rim = smoothstep(_RimMin, _RimMax, rimTerm) * _RimIntensity;
                return rim * _RimColor.rgb;
            }

            inline float3 EvaluateDiffuseRamp(float ndl)
            {
                float3 ramp = SAMPLE_TEXTURE2D(_RampTex, sampler_RampTex, float2(ndl, 0.5)).rgb;
                return ramp;
            }

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                float3 posWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionCS = TransformWorldToHClip(posWS);
                OUT.posWS = posWS;

                half3 nWS = TransformObjectToWorldNormal(IN.normalOS);
                half3 tWS = TransformObjectToWorldDir(IN.tangentOS.xyz);
                half  sgn = IN.tangentOS.w * GetOddNegativeScale();
                half3 bWS = cross(nWS, tWS) * sgn;

                OUT.normalWS    = SafeNormalize(nWS);
                OUT.tangentWS   = SafeNormalize(tWS);
                OUT.bitangentWS = SafeNormalize(bWS);

                OUT.uv = IN.uv;

                #if defined(MAIN_LIGHT_CALCULATE_SHADOWS)
                    OUT.shadowCoord = TransformWorldToShadowCoord(posWS);
                #else
                    OUT.shadowCoord = 0;
                #endif

                OUT.fogFactor = ComputeFogFactor(OUT.positionCS.z);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                float3 albedo = SampleAlbedo(IN.uv);

                float3 nTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, IN.uv), _BumpScale);
                float3 N   = NormalTangentToWorld(nTS, IN.tangentWS, IN.bitangentWS, IN.normalWS);
                float3 V   = SafeNormalize(GetWorldSpaceViewDir(IN.posWS));
                float  rough = SampleRoughness(IN.uv);

                // 环境漫反射
                float3 ambient = SampleSH(N) * _AmbientStrength;
                float3 lit = albedo * ambient;

                // 主光
                Light mainLight;
                #if defined(MAIN_LIGHT_CALCULATE_SHADOWS)
                    mainLight = GetMainLight(IN.shadowCoord);
                #else
                    mainLight = GetMainLight();
                #endif

                float NdotL_main = saturate(dot(N, mainLight.direction));
                float3 diffRamp  = EvaluateDiffuseRamp(NdotL_main);

                // 硬边阴影
                float shadowBand = step(0.5, mainLight.shadowAttenuation);
                float mainAtten  = mainLight.distanceAttenuation * shadowBand;

                float3 specCol = EvaluateSpecBand(N, V, mainLight.direction, rough);

                lit += albedo * diffRamp * mainLight.color.rgb * mainAtten
                    +  specCol       * mainLight.color.rgb * mainAtten;

                // 额外光
                #if defined(_ADDITIONAL_LIGHTS)
                {
                    int addCount = GetAdditionalLightsCount();
                    for (int i = 0; i < addCount; i++)
                    {
                        Light addL = GetAdditionalLight(i, IN.posWS);
                        float NdotL = saturate(dot(N, addL.direction));
                        float3 dr   = EvaluateDiffuseRamp(NdotL);
                        float3 sp   = EvaluateSpecBand(N, V, addL.direction, rough);
                        float atten = addL.distanceAttenuation * addL.shadowAttenuation;

                        lit += albedo * dr * addL.color.rgb * atten
                             + sp          * addL.color.rgb * atten;
                    }
                }
                #endif

                // Rim设置
                lit += EvaluateRim(N, V);

                // 补光
                lit = max(lit, albedo * _MinLight);

                // Fog线
                lit = MixFog(lit, IN.fogFactor);

                return half4(lit, 1);
            }
            ENDHLSL
        }

        UsePass "Universal Render Pipeline/Lit/ShadowCaster"
    }

    Fallback "Hidden/Universal Render Pipeline/FallbackError"
}
