namespace CSharp.UI.OpenTK;

/// <summary>
/// GLSL 330 着色器源码。所有绘制都通过这两套程序完成：圆角矩形（含填充/描边）与纹理（RGBA 位图/Alpha 遮罩）。
/// </summary>
/// <remarks>
/// <para>
/// 颜色约定：顶点颜色与纹理采样结果都用<b>预乘 alpha</b>。混合固定使用
/// <c>glBlendFunc(GL_ONE, GL_ONE_MINUS_SRC_ALPHA)</c>，因此着色器直接输出预乘结果，
/// 避免半透明边缘出现色晕（dark fringe）。
/// </para>
/// <para>坐标约定：顶点位置是 framebuffer 像素、左上原点；顶点着色器按视口尺寸换算为 NDC 并翻转 Y 轴。</para>
/// <para>
/// 着色器源码本身只使用 ASCII 字符（含注释）：GLSL 的源字符集是 ASCII 的子集，
/// 在部分 Windows 驱动（例如 Intel）上，注释里的多字节字符会破坏词法分析并产生难以理解的编译错误。
/// </para>
/// </remarks>
internal static class GlShaderSources
{
    /// <summary>圆角矩形程序的顶点着色器。</summary>
    internal const string RectangleVertex = """
        #version 330 core

        // Framebuffer pixels, origin at top-left of the viewport.
        layout(location = 0) in vec2 aPosition;
        // Position relative to the rectangle center, in framebuffer pixels.
        layout(location = 1) in vec2 aLocal;
        // Half width / half height of the rectangle.
        layout(location = 2) in vec2 aHalfSize;
        // Corner radius in framebuffer pixels.
        layout(location = 3) in float aRadius;
        // Premultiplied fill color.
        layout(location = 4) in vec4 aFill;
        // Premultiplied stroke color.
        layout(location = 5) in vec4 aStroke;
        // Stroke width; 0 disables the stroke.
        layout(location = 6) in float aStrokeWidth;

        uniform vec2 uViewport;

        out vec2 vLocal;
        out vec2 vHalfSize;
        out float vRadius;
        out vec4 vFill;
        out vec4 vStroke;
        out float vStrokeWidth;

        void main()
        {
            vec2 ndc = vec2(
                (aPosition.x / uViewport.x) * 2.0 - 1.0,
                1.0 - (aPosition.y / uViewport.y) * 2.0);
            gl_Position = vec4(ndc, 0.0, 1.0);
            vLocal = aLocal;
            vHalfSize = aHalfSize;
            vRadius = aRadius;
            vFill = aFill;
            vStroke = aStroke;
            vStrokeWidth = aStrokeWidth;
        }
        """;

    /// <summary>圆角矩形程序的片元着色器：SDF 计算圆角矩形覆盖率与描边环。</summary>
    internal const string RectangleFragment = """
        #version 330 core

        in vec2 vLocal;
        in vec2 vHalfSize;
        in float vRadius;
        in vec4 vFill;
        in vec4 vStroke;
        in float vStrokeWidth;

        layout(location = 0) out vec4 outColor;

        // Signed distance to a rounded box; negative inside, unit = framebuffer pixels.
        float roundedBoxDistance(vec2 point, vec2 halfSize, float radius)
        {
            float r = clamp(radius, 0.0, min(halfSize.x, halfSize.y));
            vec2 q = abs(point) - halfSize + vec2(r);
            return min(max(q.x, q.y), 0.0) + length(max(q, vec2(0.0))) - r;
        }

        void main()
        {
            float distanceToEdge = roundedBoxDistance(vLocal, vHalfSize, vRadius);

            // One pixel wide analytic antialiasing: coverage is 0.5 on the edge.
            float coverage = clamp(0.5 - distanceToEdge, 0.0, 1.0);

            // The stroke is centered on the boundary and drawn inwards, so the stroke
            // ring covers the inner band [0, strokeWidth].
            float strokeWidth = max(vStrokeWidth, 0.0);
            float hasStroke = step(0.0001, strokeWidth);
            float inner = clamp(0.5 - (distanceToEdge + strokeWidth), 0.0, 1.0);
            float strokeCoverage = (coverage - inner) * hasStroke;

            vec4 color = mix(vFill, vStroke, strokeCoverage);
            outColor = color * coverage;
        }
        """;

    /// <summary>纹理程序的顶点着色器。</summary>
    internal const string TextureVertex = """
        #version 330 core

        // Framebuffer pixels, origin at top-left of the viewport.
        layout(location = 0) in vec2 aPosition;
        // Texture coordinates; (0,0) is the top-left corner of the bitmap.
        layout(location = 1) in vec2 aUv;
        // rgb = tint (not premultiplied), a = opacity.
        layout(location = 2) in vec4 aTint;

        uniform vec2 uViewport;

        out vec2 vUv;
        out vec4 vTint;

        void main()
        {
            vec2 ndc = vec2(
                (aPosition.x / uViewport.x) * 2.0 - 1.0,
                1.0 - (aPosition.y / uViewport.y) * 2.0);
            gl_Position = vec4(ndc, 0.0, 1.0);
            vUv = aUv;
            vTint = aTint;
        }
        """;

    /// <summary>纹理程序的片元着色器：RGBA 预乘纹理与 Alpha8 覆盖率遮罩共用一条路径。</summary>
    internal const string TextureFragment = """
        #version 330 core

        in vec2 vUv;
        in vec4 vTint;

        // 1 = the texture is already premultiplied; 0 = single channel coverage mask.
        uniform sampler2D uTexture;
        uniform float uPremultiplied;

        layout(location = 0) out vec4 outColor;

        void main()
        {
            vec4 texel = texture(uTexture, vUv);
            if (uPremultiplied > 0.5)
            {
                // Premultiplied texture: modulate color and alpha, result stays premultiplied.
                outColor = texel * vec4(vTint.rgb, vTint.a);
            }
            else
            {
                // Coverage mask: premultiply the tint and scale by coverage so that
                // antialiased edges never darken (no fringe).
                float coverage = texel.r;
                outColor = vec4(vTint.rgb * vTint.a * coverage, vTint.a * coverage);
            }
        }
        """;
}
