using Microsoft.Xna.Framework;

internal readonly struct LotCamera(int orientation, int lotWidth, int lotHeight)
{
    public int Orientation { get; } = ((orientation % 4) + 4) % 4;
    public int LotWidth { get; } = lotWidth;
    public int LotHeight { get; } = lotHeight;

    public Vector2 CameraPoint(float x, float y) => Orientation switch
    {
        0 => new Vector2(x, y),
        1 => new Vector2(LotHeight - y, x),
        2 => new Vector2(LotWidth - x, LotHeight - y),
        _ => new Vector2(y, LotWidth - x)
    };

    public (int X, int Y) SourceTile(int x, int y) => Orientation switch
    {
        0 => (x, y),
        1 => (y, LotHeight - 1 - x),
        2 => (LotWidth - 1 - x, LotHeight - 1 - y),
        _ => (LotWidth - 1 - y, x)
    };

    public int SourceWallDirection(int cameraDirection) => (cameraDirection - Orientation + 4) % 4;

    public Vector2 SourcePoint(float x, float y) => Orientation switch
    {
        0 => new Vector2(x, y),
        1 => new Vector2(y, LotHeight - x),
        2 => new Vector2(LotWidth - x, LotHeight - y),
        _ => new Vector2(LotWidth - y, x)
    };

    /// <summary>Rotates a camera-space displacement vector back into lot coordinates without applying
    /// origin translations.</summary>
    public Vector2 SourceVector(float x, float y) => Orientation switch
    {
        0 => new Vector2(x, y),
        1 => new Vector2(y, -x),
        2 => new Vector2(-x, -y),
        _ => new Vector2(-y, x)
    };

    public Vector2 ProjectCamera(float x, float y, float altitudePixels = 0) =>
        new((x - y) * 32 + 34, (x + y) * 16 + 158 - altitudePixels);

    public Vector3 CameraNormal(Vector3 normal) => Orientation switch
    {
        0 => normal,
        1 => new Vector3(-normal.Z, normal.Y, normal.X),
        2 => new Vector3(-normal.X, normal.Y, -normal.Z),
        _ => new Vector3(normal.Z, normal.Y, -normal.X)
    };

    public Vector2 Project(float x, float y, float altitudePixels = 0)
    {
        var point = CameraPoint(x, y);
        return new Vector2((point.X - point.Y) * 32 + 34, (point.X + point.Y) * 16 + 158 - altitudePixels);
    }

    public float DepthOrder(float x, float y)
    {
        var point = CameraPoint(x, y);
        return point.X + point.Y;
    }

    public float DepthOrderCamera(float x, float y) => x + y;

    public Vector2 TexturePoint(float u, float v) => Orientation switch
    {
        0 => new Vector2(u, v),
        1 => new Vector2(v, 1 - u),
        2 => new Vector2(1 - u, 1 - v),
        _ => new Vector2(1 - v, u)
    };

    public int RotateJunction(int flags)
    {
        var lower = (flags & 15) << Orientation;
        var upper = (flags & 240) << Orientation;
        return (lower & 15) | (lower >> 4) | ((upper | upper >> 4) & 240);
    }

    public int UnrotateJunction(int flags) => new LotCamera((4 - Orientation) % 4, LotWidth, LotHeight).RotateJunction(flags);

    /// <summary>Converts lot junction arm bits into screen arm bits for wall junction sprites by rotating
    /// directional nibbles per camera quarter-turn.</summary>
    public int CameraJunction(int flags)
    {
        var shift = (4 - Orientation) % 4;
        var lower = (flags & 15) << shift;
        var upper = (flags & 240) << shift;
        return (lower & 15) | (lower >> 4) | ((upper | upper >> 4) & 240);
    }

    public float Depth(float x, float y, float altitude)
    {
        var point = CameraPoint(x, y);
        return SceneDepth.Project(point.X, point.Y, altitude);
    }

    public float DepthCamera(float x, float y, float altitude) => SceneDepth.Project(x, y, altitude);

    public (float Near, float Far) SpriteDepth(float x, float y, float altitude)
    {
        var point = CameraPoint(x, y);
        return SceneDepth.Sprite(point.X, point.Y, altitude);
    }

    public (float Near, float Far) SpriteDepthCamera(float x, float y, float altitude) => SceneDepth.Sprite(x, y, altitude);
}
