internal static class SceneDepth
{
    public static float Project(float x, float y, float height) =>
        Math.Clamp(0.9f - (x + y + height * 0.81649658f) * 0.003f, 0.001f, 0.999f);

    public static (float Near, float Far) Sprite(float x, float y, float height) =>
        (Project(x + 2.55f, y + 2.55f, height), Project(x + 0.05f, y + 0.05f, height));
}
