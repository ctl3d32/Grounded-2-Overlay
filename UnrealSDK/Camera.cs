using System.Numerics;
using System.Runtime.CompilerServices;

namespace G2Overlay.UnrealSDK;

/// <summary>
/// Câmera lida de APlayerCameraManager::CameraCachePrivate.POV (FMinimalViewInfo) e WorldToScreen.
///
/// Mesma matemática da engine (FRotationMatrix + projeção com FOV horizontal,
/// AspectRatioAxisConstraint = MaintainXFOV, padrão da UE):
///   eixos X (frente), Y (direita), Z (cima) da rotação da câmera;
///   v = (dot(d,Y), dot(d,Z), dot(d,X)), d = P - CamLoc
///   sx = cx + v.x * f / v.z ; sy = cy - v.y * f / v.z ; f = cx / tan(FOV/2)
/// </summary>
public sealed class Camera
{
    public FVector Location;
    public FRotator Rotation;
    public float Fov;

    public float ScreenWidth;
    public float ScreenHeight;

    private FVector _axisX, _axisY, _axisZ;
    private double _focal;
    private float _cx, _cy;

    public bool IsValid { get; private set; }

    public void Update(in FMinimalViewInfoHead pov, float screenW, float screenH)
    {
        Location = pov.Location;
        Rotation = pov.Rotation;
        Fov = pov.FOV;
        ScreenWidth = screenW;
        ScreenHeight = screenH;

        IsValid = Location.IsFinite && Fov > 1f && Fov < 179f && screenW > 0 && screenH > 0 &&
                  double.IsFinite(Rotation.Pitch) && double.IsFinite(Rotation.Yaw) && double.IsFinite(Rotation.Roll);
        if (!IsValid) return;

        const double deg = Math.PI / 180.0;
        double sp = Math.Sin(Rotation.Pitch * deg), cp = Math.Cos(Rotation.Pitch * deg);
        double sy = Math.Sin(Rotation.Yaw * deg), cy = Math.Cos(Rotation.Yaw * deg);
        double sr = Math.Sin(Rotation.Roll * deg), cr = Math.Cos(Rotation.Roll * deg);

        _axisX = new FVector(cp * cy, cp * sy, sp);
        _axisY = new FVector(sr * sp * cy - cr * sy, sr * sp * sy + cr * cy, -sr * cp);
        _axisZ = new FVector(-(cr * sp * cy + sr * sy), cy * sr - cr * sp * sy, cr * cp);

        _cx = screenW * 0.5f;
        _cy = screenH * 0.5f;
        _focal = _cx / Math.Tan(Fov * Math.PI / 360.0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool WorldToScreen(in FVector world, out Vector2 screen)
    {
        screen = default;
        if (!IsValid) return false;

        var d = world - Location;
        double vx = d.Dot(_axisY);
        double vy = d.Dot(_axisZ);
        double vz = d.Dot(_axisX);
        if (vz < 1.0) return false; // atrás da câmera

        screen = new Vector2((float)(_cx + vx * _focal / vz), (float)(_cy - vy * _focal / vz));
        return float.IsFinite(screen.X) && float.IsFinite(screen.Y);
    }

    /// <summary>
    /// Projeta os 8 cantos de uma AABB e retorna o retângulo 2D que os envolve.
    /// Retorna false se algum canto estiver atrás da câmera ou o retângulo for absurdo.
    /// </summary>
    public bool ProjectBox(in FVector center, in FVector extent, out float left, out float top, out float right, out float bottom)
    {
        left = top = float.MaxValue;
        right = bottom = float.MinValue;
        for (int i = 0; i < 8; i++)
        {
            var corner = new FVector(
                center.X + ((i & 1) != 0 ? extent.X : -extent.X),
                center.Y + ((i & 2) != 0 ? extent.Y : -extent.Y),
                center.Z + ((i & 4) != 0 ? extent.Z : -extent.Z));
            if (!WorldToScreen(corner, out var s)) return false;
            if (s.X < left) left = s.X;
            if (s.Y < top) top = s.Y;
            if (s.X > right) right = s.X;
            if (s.Y > bottom) bottom = s.Y;
        }

        float w = right - left, h = bottom - top;
        if (w < 1 || h < 1) return false;
        if (w > ScreenWidth * 4 || h > ScreenHeight * 4) return false;
        // Totalmente fora da tela
        if (right < 0 || bottom < 0 || left > ScreenWidth || top > ScreenHeight) return false;
        return true;
    }
}
