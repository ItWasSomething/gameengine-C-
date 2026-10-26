#version 330 core

// Drawn once per light, on a quad as wide as the light's diameter, additively
// into the light map. Uses sprite.vert unchanged — only the pixel color logic
// differs, so there's no texture sample: brightness is computed from how far
// this pixel is from the quad's center.
in vec2 vTexCoord;
out vec4 FragColor;

// Reuses Sprite's tint uniform to carry the light's color * intensity.
uniform vec4 uTint;

void main()
{
    // 0 at the center, 1 at the edge of the light's radius.
    float dist = length(vTexCoord - vec2(0.5)) * 2.0;

    // (1 - d)^2 falls off smoothly and reaches exactly 0 at the edge, so the
    // square quad never shows a hard border. A physically "correct" 1/d^2
    // never reaches zero and would need clipping — this reads nicer in 2D.
    float falloff = clamp(1.0 - dist, 0.0, 1.0);
    falloff *= falloff;

    FragColor = vec4(uTint.rgb * falloff, 1.0);
}
