#version 330 core

// Like sprite.vert (unit quad, pixel-space projection) but instead of showing
// the whole texture it shows one small rectangle of it: a single letter out of
// the font atlas. Paired with sprite.frag, so text gets uTint for free.
layout (location = 0) in vec2 aPosition;
layout (location = 1) in vec2 aTexCoord;

uniform vec2 uPosition;  // centre of the glyph, in screen pixels
uniform vec2 uSize;      // glyph size, in screen pixels
uniform mat4 uProjection;
// Which part of the atlas to sample: xy = bottom-left UV, zw = width/height
// in UV units. aTexCoord runs 0..1 across the quad, so this remaps it.
uniform vec4 uUvRect;

out vec2 vTexCoord;

void main()
{
    gl_Position = uProjection * vec4(aPosition * uSize + uPosition, 0.0, 1.0);
    vTexCoord = uUvRect.xy + aTexCoord * uUvRect.zw;
}
