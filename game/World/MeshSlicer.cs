using System;
using System.Collections.Generic;
using Godot;

namespace DeadRecon.World;

/// <summary>
/// Cuts a mesh with a horizontal plane (y = height, in the mesh's own space) and keeps one side. Triangles that cross
/// the plane are clipped exactly; the cut face gets a flat "fresh wood" cap. No special model preparation needed.
/// Interpolates position, normal, UV and colour; tangents and UV2 are dropped (the tree materials don't use them).
/// </summary>
public static class MeshSlicer
{
    private struct V
    {
        public Vector3 P, N; public Vector2 Uv; public Color C;
        public static V Lerp(V a, V b, float t) => new()
        {
            P = a.P.Lerp(b.P, t), N = a.N.Lerp(b.N, t).Normalized(), Uv = a.Uv.Lerp(b.Uv, t), C = a.C.Lerp(b.C, t),
        };
    }

    /// <param name="keepAbove">true = the part above the cut (the falling log), false = below (the stump).</param>
    /// <param name="capMaterial">Material for the cut face; null = no cap.</param>
    public static ArrayMesh Slice(ArrayMesh source, float height, bool keepAbove, Material? capMaterial)
    {
        var result = new ArrayMesh();
        var capRing = new List<Vector3>();
        for (int s = 0; s < source.GetSurfaceCount(); s++)
        {
            var arrays = source.SurfaceGetArrays(s);
            var pos = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var nrm = arrays[(int)Mesh.ArrayType.Normal].VariantType == Variant.Type.Nil ? null : arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
            var uv = arrays[(int)Mesh.ArrayType.TexUV].VariantType == Variant.Type.Nil ? null : arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            var col = arrays[(int)Mesh.ArrayType.Color].VariantType == Variant.Type.Nil ? null : arrays[(int)Mesh.ArrayType.Color].AsColorArray();
            var idx = arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil ? null : arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            int triCount = (idx?.Length ?? pos.Length) / 3;
            V Get(int i) => new() { P = pos[i], N = nrm?[i] ?? Vector3.Up, Uv = uv?[i] ?? Vector2.Zero, C = col?[i] ?? Colors.White };

            var outV = new List<V>();
            Span<V> poly = stackalloc V[4];
            for (int t = 0; t < triCount; t++)
            {
                int i0 = idx?[t * 3] ?? t * 3, i1 = idx?[t * 3 + 1] ?? t * 3 + 1, i2 = idx?[t * 3 + 2] ?? t * 3 + 2;
                var tri = new[] { Get(i0), Get(i1), Get(i2) };
                // Sutherland-Hodgman against one plane: at most 4 vertices out.
                int n = 0;
                for (int k = 0; k < 3; k++)
                {
                    var a = tri[k]; var b = tri[(k + 1) % 3];
                    bool ina = Inside(a.P.Y, height, keepAbove), inb = Inside(b.P.Y, height, keepAbove);
                    if (ina) poly[n++] = a;
                    if (ina != inb)
                    {
                        float f = (height - a.P.Y) / (b.P.Y - a.P.Y);
                        var cut = V.Lerp(a, b, f);
                        poly[n++] = cut;
                        capRing.Add(cut.P);
                    }
                }
                for (int k = 1; k + 1 < n; k++) { outV.Add(poly[0]); outV.Add(poly[k]); outV.Add(poly[k + 1]); }
            }
            if (outV.Count == 0) continue;
            var o = new Godot.Collections.Array();
            o.Resize((int)Mesh.ArrayType.Max);
            var p2 = new Vector3[outV.Count]; var n2 = new Vector3[outV.Count]; var u2 = new Vector2[outV.Count]; var c2 = new Color[outV.Count];
            for (int k = 0; k < outV.Count; k++) { p2[k] = outV[k].P; n2[k] = outV[k].N; u2[k] = outV[k].Uv; c2[k] = outV[k].C; }
            o[(int)Mesh.ArrayType.Vertex] = p2;
            o[(int)Mesh.ArrayType.Normal] = n2;
            if (uv != null) o[(int)Mesh.ArrayType.TexUV] = u2;
            if (col != null) o[(int)Mesh.ArrayType.Color] = c2;
            result.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, o);
            result.SurfaceSetMaterial(result.GetSurfaceCount() - 1, source.SurfaceGetMaterial(s));
        }

        if (capMaterial != null && capRing.Count >= 3) AddCap(result, capRing, height, keepAbove, capMaterial);
        return result;
    }

    private static bool Inside(float y, float h, bool above) => above ? y >= h : y <= h;

    /// <summary>A disc over the cut: centred on the cut points, as wide as the furthest one (the trunk at that height).</summary>
    private static void AddCap(ArrayMesh mesh, List<Vector3> ring, float height, bool keepAbove, Material material)
    {
        // Only the trunk should get a cap; ignore stray leaf cards that also crossed the plane by using the
        // points nearest the centre line (the trunk is the dense cluster around the axis).
        var centre = Vector3.Zero;
        foreach (var p in ring) centre += p;
        centre /= ring.Count;
        var d = new List<float>();
        foreach (var p in ring) d.Add(new Vector2(p.X - centre.X, p.Z - centre.Z).Length());
        d.Sort();
        float radius = d[Math.Min(d.Count - 1, (int)(d.Count * .6f))]; // ignore the outliers (leaves, roots)
        const int Seg = 14;
        var verts = new List<Vector3>(); var norms = new List<Vector3>(); var uvs = new List<Vector2>();
        var n = keepAbove ? Vector3.Down : Vector3.Up;
        var c = new Vector3(centre.X, height, centre.Z);
        for (int k = 0; k < Seg; k++)
        {
            float a0 = k * MathF.Tau / Seg, a1 = (k + 1) * MathF.Tau / Seg;
            var p0 = c + new Vector3(MathF.Cos(a0), 0, MathF.Sin(a0)) * radius;
            var p1 = c + new Vector3(MathF.Cos(a1), 0, MathF.Sin(a1)) * radius;
            // Winding so the face points away from the kept piece.
            if (keepAbove) { verts.Add(c); verts.Add(p0); verts.Add(p1); }
            else { verts.Add(c); verts.Add(p1); verts.Add(p0); }
            for (int q = 0; q < 3; q++) norms.Add(n);
            uvs.Add(new Vector2(.5f, .5f)); uvs.Add(new Vector2(.5f + MathF.Cos(a0) * .5f, .5f + MathF.Sin(a0) * .5f)); uvs.Add(new Vector2(.5f + MathF.Cos(a1) * .5f, .5f + MathF.Sin(a1) * .5f));
        }
        var o = new Godot.Collections.Array();
        o.Resize((int)Mesh.ArrayType.Max);
        o[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        o[(int)Mesh.ArrayType.Normal] = norms.ToArray();
        o[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, o);
        mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, material);
    }
}
