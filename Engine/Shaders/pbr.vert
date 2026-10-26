#version 330 core

// Standard 3D vertex shader: object space -> world space -> clip space.
layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec3 aNormal;
layout (location = 2) in vec2 aTexCoord;

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProjection;
// Inverse-transpose of uModel. Normals must be transformed by this rather
// than uModel itself, or non-uniform scale (a squashed sphere) would skew
// them and the lighting would look wrong.
uniform mat4 uNormalMatrix;

out vec3 vWorldPosition;
out vec3 vNormal;
out vec2 vTexCoord;

void main()
{
    vec4 world = uModel * vec4(aPosition, 1.0);
    vWorldPosition = world.xyz;
    vNormal = mat3(uNormalMatrix) * aNormal;
    vTexCoord = aTexCoord;
    gl_Position = uProjection * uView * world;
}
