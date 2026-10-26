#version 330 core

in vec2 vTexCoord;
out vec4 FragColor;

uniform sampler2D uTexture;
// Multiplied into the sampled texel. Defaults to (1,1,1,1) — a no-op — for
// ordinary sprites; particles use this to tint and fade without needing a
// separate shader or a colored texture per particle.
uniform vec4 uTint;

void main()
{
    FragColor = texture(uTexture, vTexCoord) * uTint;
}
