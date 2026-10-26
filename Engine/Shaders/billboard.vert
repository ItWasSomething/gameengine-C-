#version 330 core

// Billboards: flat sprites (enemies, particles) placed in the 3D world that
// always turn to face the camera — how Doom/Wolfenstein drew every enemy.
// The same unit quad as sprite.vert (-0.5..0.5) is expanded along the
// camera's RIGHT vector horizontally and straight UP vertically, so the
// sprite rotates around the vertical axis only ("cylindrical" billboard)
// and enemies stay standing upright when you look up or down.
layout (location = 0) in vec2 aPosition;
layout (location = 1) in vec2 aTexCoord;

uniform vec3 uBillboardBase; // world position of the sprite's bottom-center (its feet)
uniform vec2 uSize;          // world units
uniform vec3 uCameraRight;   // flattened onto the ground plane
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
    vec3 worldPosition = uBillboardBase
        + uCameraRight * (aPosition.x * uSize.x)
        + vec3(0.0, (aPosition.y + 0.5) * uSize.y, 0.0);

    vec4 viewPosition = uView * vec4(worldPosition, 1.0);
    gl_Position = uProjection * viewPosition;

    vTexCoord = aTexCoord;
    vShade = 1.0;
    vViewPosition = viewPosition.xyz;
    vWorldPosition = worldPosition;
}
