#version 330 core

// Physically based shading (the "metallic/roughness" model used by Godot,
// Unity, Unreal and glTF). Per light:
//
//   diffuse  = albedo / PI                  (light scattered evenly)
//   specular = D * G * F / (4 N.L N.V)     (Cook-Torrance microfacets)
//     D = how many microfacets face the half-vector  (GGX; roughness widens it)
//     G = how many are shadowed by their neighbors   (Smith/Schlick)
//     F = how much reflects instead of refracting    (Fresnel/Schlick)
//
// Metals have no diffuse and tint their reflections with albedo; that one
// rule (the `metallic` mix below) is most of what makes metal read as metal.
// Everything here is in *linear* color; sRGB conversion happens in
// tonemap.frag at the very end.

in vec3 vWorldPosition;
in vec3 vNormal;
in vec2 vTexCoord;

out vec4 FragColor;

uniform vec3 uCameraPosition;

uniform vec3 uAlbedo;
uniform float uMetallic;
uniform float uRoughness;
uniform vec3 uEmission;
uniform bool uChecker;
uniform float uCheckerScale;

#define MAX_DIR_LIGHTS 4
#define MAX_POINT_LIGHTS 16
uniform int uDirLightCount;
uniform vec3 uDirLightDirection[MAX_DIR_LIGHTS];   // direction the light travels
uniform vec3 uDirLightColor[MAX_DIR_LIGHTS];       // color * energy
uniform int uPointLightCount;
uniform vec3 uPointLightPosition[MAX_POINT_LIGHTS];
uniform vec3 uPointLightColor[MAX_POINT_LIGHTS];
uniform float uPointLightRange[MAX_POINT_LIGHTS];

uniform vec3 uSkyTop;
uniform vec3 uSkyHorizon;
uniform vec3 uGround;
uniform float uAmbientEnergy;
uniform float uFogDensity;

const float PI = 3.14159265;

vec3 skyColor(vec3 dir)
{
    if (dir.y >= 0.0)
        return mix(uSkyHorizon, uSkyTop, smoothstep(0.0, 0.6, dir.y));
    return mix(uSkyHorizon, uGround, smoothstep(0.0, 0.15, -dir.y));
}

float distributionGGX(float NdotH, float roughness)
{
    float a = roughness * roughness;
    float a2 = a * a;
    float d = NdotH * NdotH * (a2 - 1.0) + 1.0;
    return a2 / (PI * d * d);
}

float geometrySmith(float NdotV, float NdotL, float roughness)
{
    float k = (roughness + 1.0) * (roughness + 1.0) / 8.0;
    float gv = NdotV / (NdotV * (1.0 - k) + k);
    float gl = NdotL / (NdotL * (1.0 - k) + k);
    return gv * gl;
}

vec3 fresnelSchlick(float cosTheta, vec3 F0)
{
    return F0 + (1.0 - F0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}

// Light arriving from direction L with intensity `radiance`, bounced toward
// the viewer V.
vec3 shade(vec3 N, vec3 V, vec3 L, vec3 radiance, vec3 albedo, vec3 F0)
{
    float NdotL = max(dot(N, L), 0.0);
    if (NdotL <= 0.0) return vec3(0.0);
    vec3 H = normalize(V + L);
    float NdotV = max(dot(N, V), 1e-4);
    float NdotH = max(dot(N, H), 0.0);

    float D = distributionGGX(NdotH, uRoughness);
    float G = geometrySmith(NdotV, NdotL, uRoughness);
    vec3 F = fresnelSchlick(max(dot(H, V), 0.0), F0);

    vec3 specular = D * G * F / (4.0 * NdotV * NdotL + 1e-4);
    // Energy that reflected (F) can't also diffuse; metals don't diffuse.
    vec3 kd = (1.0 - F) * (1.0 - uMetallic);
    return (kd * albedo / PI + specular) * radiance * NdotL;
}

void main()
{
    vec3 N = normalize(vNormal);
    if (!gl_FrontFacing) N = -N;     // so planes look right from below too
    vec3 V = normalize(uCameraPosition - vWorldPosition);

    vec3 albedo = uAlbedo;
    if (uChecker)
    {
        // World-space checker, nudged inward along the normal so faces that
        // sit exactly on a grid line don't flicker between squares.
        vec3 p = floor((vWorldPosition - N * 0.001) * uCheckerScale);
        float check = mod(p.x + p.y + p.z, 2.0);
        albedo *= mix(0.78, 1.0, check);
    }

    // Base reflectivity: ~4% for non-metals, the albedo itself for metals.
    vec3 F0 = mix(vec3(0.04), albedo, uMetallic);

    vec3 color = vec3(0.0);
    for (int i = 0; i < uDirLightCount; i++)
        color += shade(N, V, normalize(-uDirLightDirection[i]), uDirLightColor[i], albedo, F0);

    for (int i = 0; i < uPointLightCount; i++)
    {
        vec3 toLight = uPointLightPosition[i] - vWorldPosition;
        float dist = length(toLight);
        // Inverse-square falloff, windowed to reach exactly zero at Range so
        // a light has a hard edge of influence.
        float window = clamp(1.0 - pow(dist / uPointLightRange[i], 4.0), 0.0, 1.0);
        float attenuation = window * window / (dist * dist + 1.0);
        color += shade(N, V, toLight / max(dist, 1e-4), uPointLightColor[i] * attenuation, albedo, F0);
    }

    // Ambient: light from the sky itself. Diffuse uses the sky color in the
    // direction the surface faces; specular uses the reflected direction,
    // blended toward the diffuse look as roughness blurs the reflection.
    float NdotV = max(dot(N, V), 0.0);
    vec3 Fa = F0 + (max(vec3(1.0 - uRoughness), F0) - F0) * pow(1.0 - NdotV, 5.0);
    vec3 irradiance = mix(uGround, mix(uSkyHorizon, uSkyTop, 0.5), N.y * 0.5 + 0.5);
    vec3 R = reflect(-V, N);
    vec3 reflection = mix(skyColor(R), irradiance, uRoughness * uRoughness);
    vec3 ambient = ((1.0 - Fa) * (1.0 - uMetallic) * albedo * irradiance + Fa * reflection) * uAmbientEnergy;

    color += ambient + uEmission;

    // Exponential distance fog, tinted like the sky behind the object so
    // distant things melt into the horizon.
    float dist = length(uCameraPosition - vWorldPosition);
    float fog = 1.0 - exp(-uFogDensity * dist);
    color = mix(color, skyColor(-V), clamp(fog, 0.0, 1.0));

    FragColor = vec4(color, 1.0);
}
