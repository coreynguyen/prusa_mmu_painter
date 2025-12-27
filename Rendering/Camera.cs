using System.Numerics;

namespace _3MFTool.Rendering;

/// <summary>
/// Camera matching Python MMU Painter viewport behavior (3ds Max style)
/// Uses rotation accumulation, not spherical coordinates
/// </summary>
public class Camera
{
    public Vector3 Target { get; set; } = Vector3.Zero;
    public float Distance { get; set; } = 5f;
    public float Zoom { get; set; } = 1f;
    
    // Rotation in degrees (no clamping - allows full rotation)
    public float RotX { get; set; } = 30f;  // Pitch
    public float RotY { get; set; } = 45f;  // Yaw
    
    // Pan offset in view space
    public float PanX { get; set; } = 0f;
    public float PanY { get; set; } = 0f;
    
    public float Fov { get; set; } = 45f;
    public float Aspect { get; set; } = 1.5f;
    public float Near { get; set; } = 0.01f;
    public float Far { get; set; } = 1000f;

    /// <summary>
    /// View matrix matching Python:
    /// glTranslatef(0, 0, -dist)
    /// glTranslatef(pan_x, pan_y, 0)
    /// glRotatef(rot_x, 1, 0, 0)
    /// glRotatef(rot_y, 0, 1, 0)
    /// glTranslatef(-target)
    /// </summary>
    public Matrix4x4 View
    {
        get
        {
            float dist = Distance / Zoom;
            float pitchRad = RotX * MathF.PI / 180f;
            float yawRad = RotY * MathF.PI / 180f;
            
            // Build transforms (applied right-to-left in OpenGL convention)
            var translateTarget = Matrix4x4.CreateTranslation(-Target);
            var rotateY = Matrix4x4.CreateRotationY(yawRad);
            var rotateX = Matrix4x4.CreateRotationX(pitchRad);
            var translatePan = Matrix4x4.CreateTranslation(PanX, PanY, 0);
            var translateBack = Matrix4x4.CreateTranslation(0, 0, -dist);
            
            // Combine: target -> rotY -> rotX -> pan -> back
            return translateTarget * rotateY * rotateX * translatePan * translateBack;
        }
    }

    public Matrix4x4 Projection => Matrix4x4.CreatePerspectiveFieldOfView(
        Fov * MathF.PI / 180f, Aspect, Near, Far);

    /// <summary>
    /// Camera position in world space (for lighting calculations)
    /// </summary>
    public Vector3 Position
    {
        get
        {
            // Invert view matrix to get camera world position
            if (Matrix4x4.Invert(View, out var invView))
                return new Vector3(invView.M41, invView.M42, invView.M43);
            return Vector3.Zero;
        }
    }

    /// <summary>
    /// Orbit camera (RMB drag or Alt+MMB)
    /// Matches Python: rot_y += dx, rot_x += dy
    /// </summary>
    public void Orbit(float dx, float dy)
    {
        RotY += dx;
        RotX += dy;
    }

    /// <summary>
    /// Pan camera (MMB drag)
    /// </summary>
    public void Pan(float dx, float dy)
    {
        float scale = (Distance / Zoom) * 0.002f;
        PanX += dx * scale;
        PanY -= dy * scale;
    }

    /// <summary>
    /// Zoom camera (scroll wheel)
    /// </summary>
    public void DoZoom(float delta)
    {
        float factor = delta > 0 ? 1.1f : 0.9f;
        Zoom = Math.Clamp(Zoom * factor, 0.1f, 50f);
    }

    /// <summary>
    /// Frame mesh in view
    /// </summary>
    public void Frame(Vector3 center, float radius)
    {
        Target = center;
        Distance = radius * 2.5f;
        Zoom = 1f;
        PanX = 0f;
        PanY = 0f;
        RotX = 30f;
        RotY = 45f;
    }

    /// <summary>
    /// Get ray from screen coordinates for raycasting
    /// </summary>
    public (Vector3 origin, Vector3 dir) ScreenRay(int sx, int sy, int w, int h)
    {
        float nx = (2f * sx / w - 1f);
        float ny = (1f - 2f * sy / h);
        
        Matrix4x4.Invert(Projection, out var invProj);
        Matrix4x4.Invert(View, out var invView);
        
        var nearPt = Vector4.Transform(new Vector4(nx, ny, 0, 1), invProj);
        nearPt /= nearPt.W;
        var farPt = Vector4.Transform(new Vector4(nx, ny, 1, 1), invProj);
        farPt /= farPt.W;
        
        var origin = Vector4.Transform(nearPt, invView);
        var farWorld = Vector4.Transform(farPt, invView);
        var dir = Vector3.Normalize(new Vector3(
            farWorld.X - origin.X, 
            farWorld.Y - origin.Y, 
            farWorld.Z - origin.Z));
        
        return (new Vector3(origin.X, origin.Y, origin.Z), dir);
    }
}
