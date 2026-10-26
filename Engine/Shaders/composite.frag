#version 330 core

// The final combine: scene color * light reaching that pixel. This is the
// one line that makes lighting "happen" — everything else is just building
// the two images it multiplies.
in vec2 vTexCoord;
out vec4 FragColor;

uniform sampler2D uScene;
uniform sampler2D uLightMap;

void main()
{
    vec3 scene = texture(uScene, vTexCoord).rgb;
    vec3 light = texture(uLightMap, vTexCoord).rgb;
    FragColor = vec4(scene * light, 1.0);
}
