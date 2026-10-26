#version 330 core

// Vertex shader for static 3D level geometry (walls, floor, ceiling).
// Unlike sprite.vert, positions are real 3D world coordinates (1 unit = 1
// grid tile) and go through TWO matrices: uView moves the world so the
// camera sits at the origin looking down -Z, then uProjection applies
// perspective (far things shrink toward the center of the screen).
layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec2 aTexCoord;
// Baked per-face brightness (e.g. east/west walls darker than north/south)
// — the classic Wolfenstein trick that makes corners readable without any
// real lighting.
layout (location = 2) in float aShade;

uniform mat4 uView;
uniform mat4 uProjection;

out vec2 vTexCoord;
out float vShade;
// View-space position, NOT a precomputed distance: the GPU blends vertex
// outputs linearly across each triangle, which is exact for positions but
// wrong for distances — on a big floor quad whose corners are all far away,
// a per-vertex distance would claim every pixel is far (all fog). The
// fragment shader takes length() per pixel instead.
out vec3 vViewPosition;
out vec3 vWorldPosition; // for the dynamic point lights in world.frag

void main()
{
    vec4 viewPosition = uView * vec4(aPosition, 1.0);
    gl_Position = uProjection * viewPosition;

    vTexCoord = aTexCoord;
    vShade = aShade;
    vViewPosition = viewPosition.xyz;
    vWorldPosition = aPosition;
}
