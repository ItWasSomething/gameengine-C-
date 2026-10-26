#version 330 core

// Shared by world.vert (level geometry) and billboard.vert (enemies,
// particles, projectiles), so everything in the 3D scene is lit and fogged
// the same way.

in vec2 vTexCoord;
in float vShade;
in vec3 vViewPosition;
in vec3 vWorldPosition;
out vec4 FragColor;

uniform sampler2D uTexture;
uniform vec4 uTint;          // same idea as sprite.frag: hit flashes go above 1.0

// Pixels with alpha below this are thrown away entirely ("alpha cutout").
// Enemies use 0.5: their transparent corners must not write to the depth
// buffer, or they'd hide whatever is behind them as invisible rectangles.
// Particles use ~0 and rely on blending instead.
uniform float uAlphaCutoff;

uniform vec3 uFogColor;
uniform float uFogStart;
uniform float uFogEnd;
uniform float uLightFalloff; // the player carries the light: farther = darker
uniform float uMuzzleFlash;  // 0..1+, briefly lights up the area near the camera
uniform float uEmissive;     // 1 = ignore lighting (glowing sparks), 0 = lit normally

// Dynamic point lights: rockets and acid globs in flight, explosion flashes.
// Each one adds colour that fades to nothing at its radius. No normals, so
// no "which way does the wall face" — just distance, which suits the style.
#define MAX_LIGHTS 8
uniform int uLightCount;
uniform vec3 uLightPositions[MAX_LIGHTS];
uniform vec3 uLightColors[MAX_LIGHTS];
uniform float uLightRadii[MAX_LIGHTS];

void main()
{
    float viewDistance = length(vViewPosition); // distance from the camera, per pixel
    vec4 texel = texture(uTexture, vTexCoord) * uTint;
    if (texel.a < uAlphaCutoff)
        discard;

    float attenuation = 1.0 / (1.0 + uLightFalloff * viewDistance * viewDistance);
    vec3 flash = vec3(1.0, 0.8, 0.5) * uMuzzleFlash * exp(-viewDistance * 0.35);

    vec3 dynamicLight = vec3(0.0);
    for (int i = 0; i < uLightCount; i++)
    {
        float d = distance(vWorldPosition, uLightPositions[i]);
        float k = clamp(1.0 - d / uLightRadii[i], 0.0, 1.0);
        dynamicLight += uLightColors[i] * k * k; // squared: soft edge, bright core
    }

    vec3 light = mix(vec3(vShade * attenuation) + flash + dynamicLight, vec3(1.0), uEmissive);
    vec3 litColor = texel.rgb * light;

    float fog = smoothstep(uFogStart, uFogEnd, viewDistance);
    FragColor = vec4(mix(litColor, uFogColor, fog), texel.a);
}
