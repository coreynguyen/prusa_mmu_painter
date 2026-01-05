using System.Numerics;
using OpenTK.Graphics.OpenGL4;
using _3MFTool.Models;

namespace _3MFTool.Rendering;

public class MeshRenderer : IDisposable
{
    private int _vao, _vbo, _prog, _progTex;
    private int _wireVao, _wireVbo, _wireProg;
    private int _subdivVao, _subdivVbo;  // Subdivision edges
    private int _vertCount, _wireCount, _subdivCount;
    
    // Colored shader uniforms
    private int _uMVP, _uModel, _uLightDir, _uViewPos, _uUnlit;
    // Textured shader uniforms
    private int _uMVPTex, _uModelTex, _uLightDirTex, _uViewPosTex, _uTex, _uFlipH, _uFlipV, _uUnlitTex;
    // Wire uniforms
    private int _uWireMVP, _uWireColor;
    
    // Vertex: pos(3) + normal(3) + color(3) + uv(2) = 11 floats
    private const int STRIDE = 11;
    private float[] _buffer = Array.Empty<float>();
    private float[] _wireBuffer = Array.Empty<float>();
    private float[] _subdivBuffer = Array.Empty<float>();

    public void Init()
    {
        // Main mesh VAO
        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, STRIDE * 4, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, STRIDE * 4, 12);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, STRIDE * 4, 24);
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(3, 2, VertexAttribPointerType.Float, false, STRIDE * 4, 36);
        GL.EnableVertexAttribArray(3);
        GL.BindVertexArray(0);
        
        // Wireframe VAO
        _wireVao = GL.GenVertexArray();
        _wireVbo = GL.GenBuffer();
        GL.BindVertexArray(_wireVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _wireVbo);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 12, 0);
        GL.EnableVertexAttribArray(0);
        GL.BindVertexArray(0);
        
        // Subdivision edges VAO
        _subdivVao = GL.GenVertexArray();
        _subdivVbo = GL.GenBuffer();
        GL.BindVertexArray(_subdivVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _subdivVbo);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 12, 0);
        GL.EnableVertexAttribArray(0);
        GL.BindVertexArray(0);
        
        CreateShaders();
    }

    private void CreateShaders()
    {
        // Colored mesh shader - improved lighting for painting/sculpting
        const string vsCol = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNorm;
layout(location=2) in vec3 aCol;
uniform mat4 uMVP, uModel;
out vec3 vNorm, vCol, vWorldPos;
void main() {
    gl_Position = uMVP * vec4(aPos, 1.0);
    vNorm = mat3(uModel) * aNorm;
    vCol = aCol;
    vWorldPos = (uModel * vec4(aPos, 1.0)).xyz;
}";
        const string fsCol = @"#version 330 core
in vec3 vNorm, vCol, vWorldPos;
uniform vec3 uLightDir, uViewPos;
uniform int uUnlit;
out vec4 FragColor;
void main() {
    // Unlit mode - flat color, no shading (best for painting)
    if (uUnlit == 1) {
        FragColor = vec4(vCol, 1.0);
        return;
    }
    
    vec3 N = normalize(vNorm);
    vec3 viewDir = normalize(uViewPos - vWorldPos);
    
    // Camera-relative key light (follows camera, slightly offset)
    vec3 keyLightDir = normalize(viewDir + vec3(0.2, 0.3, 0.0));
    float keyDiff = dot(N, keyLightDir);
    // Half-Lambert for softer shadows
    keyDiff = keyDiff * 0.4 + 0.6;
    vec3 keyLight = vec3(1.0, 0.98, 0.95) * keyDiff * 0.55;
    
    // Fill light from opposite side (also camera-relative)
    vec3 fillLightDir = normalize(viewDir + vec3(-0.5, 0.1, 0.3));
    float fillDiff = max(dot(N, fillLightDir), 0.0);
    fillDiff = fillDiff * 0.5 + 0.5;
    vec3 fillLight = vec3(0.8, 0.85, 1.0) * fillDiff * 0.25;
    
    // Rim/back light for edge definition
    float rim = 1.0 - max(dot(viewDir, N), 0.0);
    rim = pow(rim, 2.5) * 0.2;
    vec3 rimLight = vec3(1.0) * rim;
    
    // Hemisphere ambient (sky/ground)
    float hemi = N.y * 0.5 + 0.5;
    vec3 ambient = mix(vec3(0.3, 0.28, 0.28), vec3(0.4, 0.42, 0.45), hemi) * 0.4;
    
    vec3 totalLight = ambient + keyLight + fillLight + rimLight;
    vec3 result = vCol * totalLight;
    
    FragColor = vec4(result, 1.0);
}";
        _prog = CreateProgram(vsCol, fsCol);
        _uMVP = GL.GetUniformLocation(_prog, "uMVP");
        _uModel = GL.GetUniformLocation(_prog, "uModel");
        _uLightDir = GL.GetUniformLocation(_prog, "uLightDir");
        _uViewPos = GL.GetUniformLocation(_prog, "uViewPos");
        _uUnlit = GL.GetUniformLocation(_prog, "uUnlit");
        
        // Textured mesh shader - improved lighting
        const string vsTex = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNorm;
layout(location=2) in vec3 aCol;
layout(location=3) in vec2 aUV;
uniform mat4 uMVP, uModel;
out vec3 vNorm, vWorldPos;
out vec2 vUV;
void main() {
    gl_Position = uMVP * vec4(aPos, 1.0);
    vNorm = mat3(uModel) * aNorm;
    vWorldPos = (uModel * vec4(aPos, 1.0)).xyz;
    vUV = vec2(aUV.x, 1.0 - aUV.y);
}";
        const string fsTex = @"#version 330 core
in vec3 vNorm, vWorldPos;
in vec2 vUV;
uniform vec3 uLightDir, uViewPos;
uniform sampler2D uTex;
uniform bool uFlipH, uFlipV;
uniform int uUnlit;
out vec4 FragColor;
void main() {
    vec2 uv = vUV;
    if (uFlipH) uv.x = 1.0 - uv.x;
    if (uFlipV) uv.y = 1.0 - uv.y;
    vec3 texCol = texture(uTex, uv).rgb;
    
    // Unlit mode
    if (uUnlit == 1) {
        FragColor = vec4(texCol, 1.0);
        return;
    }
    
    vec3 N = normalize(vNorm);
    vec3 viewDir = normalize(uViewPos - vWorldPos);
    
    // Camera-relative key light
    vec3 keyLightDir = normalize(viewDir + vec3(0.2, 0.3, 0.0));
    float keyDiff = dot(N, keyLightDir);
    keyDiff = keyDiff * 0.4 + 0.6;
    vec3 keyLight = vec3(1.0, 0.98, 0.95) * keyDiff * 0.55;
    
    // Fill light
    vec3 fillLightDir = normalize(viewDir + vec3(-0.5, 0.1, 0.3));
    float fillDiff = max(dot(N, fillLightDir), 0.0);
    fillDiff = fillDiff * 0.5 + 0.5;
    vec3 fillLight = vec3(0.8, 0.85, 1.0) * fillDiff * 0.25;
    
    // Rim light
    float rim = 1.0 - max(dot(viewDir, N), 0.0);
    rim = pow(rim, 2.5) * 0.2;
    vec3 rimLight = vec3(1.0) * rim;
    
    // Hemisphere ambient
    float hemi = N.y * 0.5 + 0.5;
    vec3 ambient = mix(vec3(0.3, 0.28, 0.28), vec3(0.4, 0.42, 0.45), hemi) * 0.4;
    
    vec3 totalLight = ambient + keyLight + fillLight + rimLight;
    vec3 result = texCol * totalLight;
    
    FragColor = vec4(result, 1.0);
}";
        _progTex = CreateProgram(vsTex, fsTex);
        _uMVPTex = GL.GetUniformLocation(_progTex, "uMVP");
        _uModelTex = GL.GetUniformLocation(_progTex, "uModel");
        _uLightDirTex = GL.GetUniformLocation(_progTex, "uLightDir");
        _uViewPosTex = GL.GetUniformLocation(_progTex, "uViewPos");
        _uTex = GL.GetUniformLocation(_progTex, "uTex");
        _uFlipH = GL.GetUniformLocation(_progTex, "uFlipH");
        _uFlipV = GL.GetUniformLocation(_progTex, "uFlipV");
        _uUnlitTex = GL.GetUniformLocation(_progTex, "uUnlit");
        
        // Wireframe shader
        const string vsWire = "#version 330 core\nlayout(location=0) in vec3 aPos;\nuniform mat4 uMVP;\nvoid main(){gl_Position=uMVP*vec4(aPos,1);}";
        const string fsWire = "#version 330 core\nuniform vec3 uColor;\nout vec4 FragColor;\nvoid main(){FragColor=vec4(uColor,1);}";
        _wireProg = CreateProgram(vsWire, fsWire);
        _uWireMVP = GL.GetUniformLocation(_wireProg, "uMVP");
        _uWireColor = GL.GetUniformLocation(_wireProg, "uColor");
    }

    private static int CreateProgram(string vs, string fs)
    {
        int v = GL.CreateShader(ShaderType.VertexShader); GL.ShaderSource(v, vs); GL.CompileShader(v);
        int f = GL.CreateShader(ShaderType.FragmentShader); GL.ShaderSource(f, fs); GL.CompileShader(f);
        int p = GL.CreateProgram(); GL.AttachShader(p, v); GL.AttachShader(p, f); GL.LinkProgram(p);
        GL.DeleteShader(v); GL.DeleteShader(f);
        return p;
    }

    public void Update(Mesh mesh, Vector3[] palette, bool buildSubdivEdges = false)
    {
        if (mesh == null) return;
        
        int total = mesh.TotalSubTriangles * 3 * STRIDE;
        if (_buffer.Length < total) _buffer = new float[total + total / 4];
        
        int idx = 0;
        foreach (var tri in mesh.Triangles)
        {
            var v0 = mesh.Vertices[tri.Indices.V0];
            var v1 = mesh.Vertices[tri.Indices.V1];
            var v2 = mesh.Vertices[tri.Indices.V2];
            var n = tri.Normal;
            var uv0 = tri.UV?.UV0 ?? Vector2.Zero;
            var uv1 = tri.UV?.UV1 ?? Vector2.Zero;
            var uv2 = tri.UV?.UV2 ?? Vector2.Zero;
            
            foreach (var sub in tri.PaintData)
            {
                var col = palette[sub.ExtruderId % palette.Length];
                if (sub.Masked || tri.Masked)
                    col = new Vector3(col.X * 0.4f + 0.2f, col.Y * 0.3f, col.Z * 0.3f);
                
                for (int i = 0; i < 3; i++)
                {
                    var b = sub.BaryCorners[i];
                    float px = b.U * v0.X + b.V * v1.X + b.W * v2.X;
                    float py = b.U * v0.Y + b.V * v1.Y + b.W * v2.Y;
                    float pz = b.U * v0.Z + b.V * v1.Z + b.W * v2.Z;
                    float uvx = b.U * uv0.X + b.V * uv1.X + b.W * uv2.X;
                    float uvy = b.U * uv0.Y + b.V * uv1.Y + b.W * uv2.Y;
                    
                    _buffer[idx++] = px; _buffer[idx++] = py; _buffer[idx++] = pz;
                    _buffer[idx++] = n.X; _buffer[idx++] = n.Y; _buffer[idx++] = n.Z;
                    _buffer[idx++] = col.X; _buffer[idx++] = col.Y; _buffer[idx++] = col.Z;
                    _buffer[idx++] = uvx; _buffer[idx++] = uvy;
                }
            }
        }
        
        _vertCount = idx / STRIDE;
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, idx * 4, _buffer, BufferUsageHint.DynamicDraw);
        
        // Base wireframe (triangle edges)
        int wireTotal = mesh.Triangles.Count * 6 * 3;
        if (_wireBuffer.Length < wireTotal) _wireBuffer = new float[wireTotal];
        
        int widx = 0;
        foreach (var tri in mesh.Triangles)
        {
            var v0 = mesh.Vertices[tri.Indices.V0];
            var v1 = mesh.Vertices[tri.Indices.V1];
            var v2 = mesh.Vertices[tri.Indices.V2];
            _wireBuffer[widx++] = v0.X; _wireBuffer[widx++] = v0.Y; _wireBuffer[widx++] = v0.Z;
            _wireBuffer[widx++] = v1.X; _wireBuffer[widx++] = v1.Y; _wireBuffer[widx++] = v1.Z;
            _wireBuffer[widx++] = v1.X; _wireBuffer[widx++] = v1.Y; _wireBuffer[widx++] = v1.Z;
            _wireBuffer[widx++] = v2.X; _wireBuffer[widx++] = v2.Y; _wireBuffer[widx++] = v2.Z;
            _wireBuffer[widx++] = v2.X; _wireBuffer[widx++] = v2.Y; _wireBuffer[widx++] = v2.Z;
            _wireBuffer[widx++] = v0.X; _wireBuffer[widx++] = v0.Y; _wireBuffer[widx++] = v0.Z;
        }
        _wireCount = widx / 3;
        GL.BindBuffer(BufferTarget.ArrayBuffer, _wireVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, widx * 4, _wireBuffer, BufferUsageHint.DynamicDraw);
        
        // Subdivision edges (optional - expensive)
        if (buildSubdivEdges)
        {
            int subdivTotal = mesh.TotalSubTriangles * 6 * 3;
            if (_subdivBuffer.Length < subdivTotal) _subdivBuffer = new float[subdivTotal];
            
            int sidx = 0;
            foreach (var tri in mesh.Triangles)
            {
                var v0 = mesh.Vertices[tri.Indices.V0];
                var v1 = mesh.Vertices[tri.Indices.V1];
                var v2 = mesh.Vertices[tri.Indices.V2];
                
                foreach (var sub in tri.PaintData)
                {
                    // Get world positions of sub-triangle corners
                    var b0 = sub.BaryCorners[0];
                    var b1 = sub.BaryCorners[1];
                    var b2 = sub.BaryCorners[2];
                    
                    float p0x = b0.U * v0.X + b0.V * v1.X + b0.W * v2.X;
                    float p0y = b0.U * v0.Y + b0.V * v1.Y + b0.W * v2.Y;
                    float p0z = b0.U * v0.Z + b0.V * v1.Z + b0.W * v2.Z;
                    
                    float p1x = b1.U * v0.X + b1.V * v1.X + b1.W * v2.X;
                    float p1y = b1.U * v0.Y + b1.V * v1.Y + b1.W * v2.Y;
                    float p1z = b1.U * v0.Z + b1.V * v1.Z + b1.W * v2.Z;
                    
                    float p2x = b2.U * v0.X + b2.V * v1.X + b2.W * v2.X;
                    float p2y = b2.U * v0.Y + b2.V * v1.Y + b2.W * v2.Y;
                    float p2z = b2.U * v0.Z + b2.V * v1.Z + b2.W * v2.Z;
                    
                    // Edge 0-1
                    _subdivBuffer[sidx++] = p0x; _subdivBuffer[sidx++] = p0y; _subdivBuffer[sidx++] = p0z;
                    _subdivBuffer[sidx++] = p1x; _subdivBuffer[sidx++] = p1y; _subdivBuffer[sidx++] = p1z;
                    // Edge 1-2
                    _subdivBuffer[sidx++] = p1x; _subdivBuffer[sidx++] = p1y; _subdivBuffer[sidx++] = p1z;
                    _subdivBuffer[sidx++] = p2x; _subdivBuffer[sidx++] = p2y; _subdivBuffer[sidx++] = p2z;
                    // Edge 2-0
                    _subdivBuffer[sidx++] = p2x; _subdivBuffer[sidx++] = p2y; _subdivBuffer[sidx++] = p2z;
                    _subdivBuffer[sidx++] = p0x; _subdivBuffer[sidx++] = p0y; _subdivBuffer[sidx++] = p0z;
                }
            }
            _subdivCount = sidx / 3;
            GL.BindBuffer(BufferTarget.ArrayBuffer, _subdivVbo);
            GL.BufferData(BufferTarget.ArrayBuffer, sidx * 4, _subdivBuffer, BufferUsageHint.DynamicDraw);
        }
    }

    public void Draw(Matrix4x4 mvp, Matrix4x4 model, Vector3 lightDir, Vector3 viewPos, 
                     int textureId, bool showTexture, bool flipH, bool flipV, bool unlit = false)
    {
        if (_vertCount == 0) return;
        
        if (showTexture && textureId != 0)
        {
            GL.UseProgram(_progTex);
            GL.UniformMatrix4(_uMVPTex, 1, false, ToFloats(mvp));
            GL.UniformMatrix4(_uModelTex, 1, false, ToFloats(model));
            GL.Uniform3(_uLightDirTex, lightDir.X, lightDir.Y, lightDir.Z);
            GL.Uniform3(_uViewPosTex, viewPos.X, viewPos.Y, viewPos.Z);
            GL.Uniform1(_uFlipH, flipH ? 1 : 0);
            GL.Uniform1(_uFlipV, flipV ? 1 : 0);
            GL.Uniform1(_uUnlitTex, unlit ? 1 : 0);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, textureId);
            GL.Uniform1(_uTex, 0);
        }
        else
        {
            GL.UseProgram(_prog);
            GL.UniformMatrix4(_uMVP, 1, false, ToFloats(mvp));
            GL.UniformMatrix4(_uModel, 1, false, ToFloats(model));
            GL.Uniform3(_uLightDir, lightDir.X, lightDir.Y, lightDir.Z);
            GL.Uniform3(_uViewPos, viewPos.X, viewPos.Y, viewPos.Z);
            GL.Uniform1(_uUnlit, unlit ? 1 : 0);
        }
        
        GL.BindVertexArray(_vao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, _vertCount);
        GL.BindVertexArray(0);
        GL.UseProgram(0);
    }

    public void DrawWireframe(Matrix4x4 mvp, Vector3 color)
    {
        if (_wireCount == 0) return;
        
        GL.UseProgram(_wireProg);
        GL.UniformMatrix4(_uWireMVP, 1, false, ToFloats(mvp));
        GL.Uniform3(_uWireColor, color.X, color.Y, color.Z);
        
        GL.BindVertexArray(_wireVao);
        GL.DrawArrays(PrimitiveType.Lines, 0, _wireCount);
        GL.BindVertexArray(0);
        GL.UseProgram(0);
    }

    public void DrawSubdivisionEdges(Matrix4x4 mvp, Vector3 color)
    {
        if (_subdivCount == 0) return;
        
        GL.UseProgram(_wireProg);
        GL.UniformMatrix4(_uWireMVP, 1, false, ToFloats(mvp));
        GL.Uniform3(_uWireColor, color.X, color.Y, color.Z);
        
        GL.BindVertexArray(_subdivVao);
        GL.DrawArrays(PrimitiveType.Lines, 0, _subdivCount);
        GL.BindVertexArray(0);
        GL.UseProgram(0);
    }

    private static float[] ToFloats(Matrix4x4 m) => new[] {
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44
    };

    public void Clear()
    {
        _vertCount = 0;
        _wireCount = 0;
        _subdivCount = 0;
    }

    public void Dispose()
    {
        if (_vao != 0) GL.DeleteVertexArray(_vao);
        if (_vbo != 0) GL.DeleteBuffer(_vbo);
        if (_wireVao != 0) GL.DeleteVertexArray(_wireVao);
        if (_wireVbo != 0) GL.DeleteBuffer(_wireVbo);
        if (_subdivVao != 0) GL.DeleteVertexArray(_subdivVao);
        if (_subdivVbo != 0) GL.DeleteBuffer(_subdivVbo);
        if (_prog != 0) GL.DeleteProgram(_prog);
        if (_progTex != 0) GL.DeleteProgram(_progTex);
        if (_wireProg != 0) GL.DeleteProgram(_wireProg);
    }
}
