#version 330 core

// A unit quad (-0.5..0.5) is rotated by uRotation, stretched by uSize, and
// moved by uPosition, all in world-space pixels (origin bottom-left, Y up).
// uProjection then maps that world-pixel space onto the -1..1 clip space GL
// actually wants — this is what lets platform/player positions be specified
// in ordinary pixel coordinates instead of hand-picked NDC fractions.
layout (location = 0) in vec2 aPosition;
layout (location = 1) in vec2 aTexCoord;

uniform vec2 uPosition;
uniform vec2 uSize;
uniform mat4 uProjection;
// Radians, applied around the quad's own center before it's placed in the
// world — used for the slash-hit particle, everything else just passes 0.
uniform float uRotation;

out vec2 vTexCoord;

void main()
{
    vec2 scaled = aPosition * uSize;

    float c = cos(uRotation);
    float s = sin(uRotation);
    vec2 rotated = vec2(scaled.x * c - scaled.y * s, scaled.x * s + scaled.y * c);

    vec2 worldPosition = rotated + uPosition;
    gl_Position = uProjection * vec4(worldPosition, 0.0, 1.0);
    vTexCoord = aTexCoord;
}
