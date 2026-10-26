#version 330 core

// For FullscreenQuad: positions are already in clip space, so they pass
// straight through — no projection, no world coordinates.
layout (location = 0) in vec2 aPosition;
layout (location = 1) in vec2 aTexCoord;

out vec2 vTexCoord;

void main()
{
    gl_Position = vec4(aPosition, 0.0, 1.0);
    vTexCoord = aTexCoord;
}
