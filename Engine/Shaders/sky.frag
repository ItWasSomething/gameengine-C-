#version 330 core

// Fills the background with the environment's sky gradient. Each pixel
// turns its screen position back into a world-space view direction (via
// the inverse view-projection matrix) and looks the sky up in that
// direction, so the sky stays put as the camera rotates.
in vec2 vTexCoord;
out vec4 FragColor;

uniform mat4 uInverseViewProjection;
uniform vec3 uCameraPosition;
uniform vec3 uSkyTop;
uniform vec3 uSkyHorizon;
uniform vec3 uGround;
uniform vec3 uSunDirection;   // direction the sunlight travels
uniform vec3 uSunColor;

void main()
{
    vec4 farPoint = uInverseViewProjection * vec4(vTexCoord * 2.0 - 1.0, 1.0, 1.0);
    vec3 dir = normalize(farPoint.xyz / farPoint.w - uCameraPosition);

    vec3 color;
    if (dir.y >= 0.0)
        color = mix(uSkyHorizon, uSkyTop, smoothstep(0.0, 0.6, dir.y));
    else
        color = mix(uSkyHorizon, uGround, smoothstep(0.0, 0.15, -dir.y));

    // A sun disc plus a soft glow around it, so the main light's direction
    // is visible in the sky.
    float sun = max(dot(dir, -uSunDirection), 0.0);
    color += uSunColor * (pow(sun, 2000.0) * 40.0 + pow(sun, 24.0) * 0.25);

    FragColor = vec4(color, 1.0);
}
