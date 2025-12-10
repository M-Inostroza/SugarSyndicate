Shader "UI/VignetteOverlay"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,0.25)
        _MainTex ("Texture", 2D) = "white" {}
        _Center ("Center", Vector) = (0.5, 0.5, 0, 0)
        _Radius ("Radius", Range(0.05, 1)) = 0.45
        _Softness ("Softness", Range(0.001, 1)) = 0.25
    }
    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            "PreviewType"="Plane"
            "CanUseSpriteAtlas"="True"
        }
        Cull Off
        Lighting Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Name "UIVignette"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile __ UNITY_UI_CLIP_RECT
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            struct appdata_t
            {
                float4 vertex   : POSITION;
                float2 texcoord : TEXCOORD0;
                float4 color    : COLOR;
            };

            struct v2f
            {
                float4 pos   : SV_POSITION;
                float2 uv    : TEXCOORD0;
                float4 color : COLOR;
                #ifdef UNITY_UI_CLIP_RECT
                float4 worldPosition : TEXCOORD1;
                #endif
            };

            fixed4 _Color;
            float4 _ClipRect;
            float2 _Center;
            float _Radius;
            float _Softness;
            sampler2D _MainTex;

            v2f vert(appdata_t v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                o.color = v.color;
                #ifdef UNITY_UI_CLIP_RECT
                o.worldPosition = v.vertex;
                #endif
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // radial distance from center, adjusted for aspect so it stays circular
                float aspect = _ScreenParams.x / _ScreenParams.y;
                float2 p = i.uv - _Center;
                p.x *= aspect;
                float dist = length(p);

                // smooth edge: 0 inside radius, 1 outside radius+softness
                float fade = smoothstep(_Radius, _Radius + _Softness, dist);

                fixed4 tex = tex2D(_MainTex, i.uv);
                fixed4 col = tex * _Color;
                col.a *= fade;

                #ifdef UNITY_UI_CLIP_RECT
                col.a *= UnityGet2DClipping(i.worldPosition.xy, _ClipRect);
                #endif

                return col;
            }
            ENDCG
        }
    }
}
