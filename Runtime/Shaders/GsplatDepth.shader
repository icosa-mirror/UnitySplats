// Copyright (c) 2026 Keir Rice
// SPDX-License-Identifier: MIT

Shader "Gsplat/DepthOnly"
{
    Properties {}
    SubShader
    {
        Tags
        {
            "RenderType"="Transparent"
            "Queue"="Transparent"
        }

        Pass
        {
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5 GSPLAT_VERTEX_BUFFERS
            #pragma target 3.5 GSPLAT_VERTEX_TEXTURES
            #pragma multi_compile SH_BANDS_0 SH_BANDS_1 SH_BANDS_2 SH_BANDS_3 SH_BANDS_4
            #pragma multi_compile UNCOMPRESSED SPARK
            #pragma multi_compile GSPLAT_VERTEX_BUFFERS GSPLAT_VERTEX_TEXTURES

            #ifdef GSPLAT_VERTEX_TEXTURES
            int _GsplatTextureWidth;
            #endif

            #include "UnityCG.cginc"
            #include "Gsplat.hlsl"
            #ifdef UNCOMPRESSED
            #include "GsplatUncompressed.hlsl"
            #endif
            #ifdef SPARK
            #include "GsplatSpark.hlsl"
            #endif

            int _SplatCount;
            int _SplatInstanceSize;
            float4x4 _MATRIX_M;
            float _ScaleFactor;
            float _DepthPrepassAlphaCutoff;
            int _Antialiased;

            #ifdef GSPLAT_VERTEX_TEXTURES
            Texture2D<float4> _OrderTexture;

            uint LoadSplatOrder(uint index)
            {
                float4 encoded = _OrderTexture.Load(
                    int3(GsplatLinearTextureCoord(index, _GsplatTextureWidth), 0));
                return GsplatDecodeTextureUInt(encoded);
            }
            #else
            StructuredBuffer<uint> _OrderBuffer;

            uint LoadSplatOrder(uint index)
            {
                return _OrderBuffer[index];
            }
            #endif

            struct appdata
            {
                float4 vertex : POSITION;
                #if !defined(UNITY_INSTANCING_ENABLED) && !defined(UNITY_PROCEDURAL_INSTANCING_ENABLED) && !defined(UNITY_STEREO_INSTANCING_ENABLED)
                uint instanceID : SV_InstanceID;
                #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            bool InitSource(appdata v, out SplatSource source)
            {
                #if !defined(UNITY_INSTANCING_ENABLED) && !defined(UNITY_PROCEDURAL_INSTANCING_ENABLED) && !defined(UNITY_STEREO_INSTANCING_ENABLED)
                source.order = v.instanceID * _SplatInstanceSize + (uint)v.vertex.z;
                #else
                source.order = unity_InstanceID * _SplatInstanceSize + (uint)v.vertex.z;
                #endif

                if (source.order >= _SplatCount)
                    return false;

                source.id = LoadSplatOrder(source.order);
                source.cornerUV = float2(v.vertex.x, v.vertex.y) * _ScaleFactor;
                return true;
            }

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float alpha : TEXCOORD1;
                float4 vertex : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = discardVec;

                SplatSource source;
                if (!InitSource(v, source))
                    return o;

                SplatCenter center;
                SplatCorner corner;
                float4 color;
                if (!InitSplatData(source, mul(UNITY_MATRIX_V, _MATRIX_M), _Antialiased != 0,
                                   center, corner, color))
                    return o;
                if (color.a <= 1.0 / 255.0)
                    return o;

                ClipCorner(corner, color.a);
                o.vertex = center.proj + float4(corner.offset.x, _ProjectionParams.x * corner.offset.y, 0, 0);
                o.alpha = color.a;
                o.uv = corner.uv;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float A = dot(i.uv, i.uv);
                if (A > 1.0)
                    discard;

                float2 absUV = abs(i.uv);
                float maxUV = max(absUV.x, absUV.y);
                float falloff = -exp((maxUV - _ScaleFactor * 1.16) * 25 * _ScaleFactor);
                float alpha = (exp(-A * 4.0) + falloff) * i.alpha;
                if (alpha < _DepthPrepassAlphaCutoff)
                    discard;
                return 0;
            }
            ENDHLSL
        }
    }
}
