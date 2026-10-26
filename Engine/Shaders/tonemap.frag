#version 330 core

// Final step: squeeze the HDR image (any brightness) into what a monitor
// can show (0..1), then convert linear light to sRGB.
in vec2 vTexCoord;
out vec4 FragColor;

uniform sampler2D uScene;
uniform float uExposure;

// ACES filmic curve (the Narkowicz fit): bright values roll off gently
// instead of clipping hard to white, and darks get a little contrast.
vec3 aces(vec3 x)
{
    return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0);
}

void main()
{
    vec3 hdr = texture(uScene, vTexCoord).rgb * uExposure;
    vec3 color = pow(aces(hdr), vec3(1.0 / 2.2));

    // Tiny per-pixel noise ("dithering") hides the banding that 8-bit
    // output otherwise shows in smooth sky gradients.
    float noise = fract(sin(dot(gl_FragCoord.xy, vec2(12.9898, 78.233))) * 43758.5453);
    color += (noise - 0.5) / 255.0;

    FragColor = vec4(color, 1.0);
}
