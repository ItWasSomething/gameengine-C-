#version 330 core

// DebugDraw: colored lines in world space.
layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec4 aColor;

uniform mat4 uViewProjection;

out vec4 vColor;

void main()
{
    gl_Position = uViewProjection * vec4(aPosition, 1.0);
    vColor = aColor;
}
