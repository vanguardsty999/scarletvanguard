using System.Globalization;
using System.Numerics;

namespace RpfPreview.Core;





public static class ObjModelReader
{
    public const int MaxCharacters = 32 * 1024 * 1024;
    private const int MaxSourceVertices = 1_000_000, MaxOutputVertices = 2_000_000;
    private const int MaxTriangles = 2_000_000, MaxMeshes = 1024, MaxPolygonVertices = 256;
    private sealed class Part(string name, bool uv)
    {
        public string Name = name;
        public bool HasUv = uv;
        public Dictionary<(int Position, int Uv), int> Map = [];
        public List<Vector3> Positions = [];
        public List<Vector2> TexCoords = [];
        public List<int> Indices = [];
    }

    public static RageModel Parse(string text, string name = "Imported OBJ")
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxCharacters) throw new InvalidDataException("OBJ text exceeds the 64 MiB text-memory limit.");
        var vertices = new List<Vector3>(); var coords = new List<Vector2>(); int normals = 0;
        var parts = new List<Part>(); Part? current = null; string group = "default";
        int lineNumber = 0, outputVertices = 0, triangles = 0; long operations = 0;
        foreach (ReadOnlySpan<char> raw in text.AsSpan().EnumerateLines())
        {
            lineNumber++;
            if (raw.Length > 1024 * 1024) throw Error(lineNumber, "Line is too long.");
            ReadOnlySpan<char> line = raw; int comment = line.IndexOf('#'); if (comment >= 0) line = line[..comment];
            line = line.Trim(); if (line.IsEmpty) continue;
            if (lineNumber == 1) line = line.TrimStart('\uFEFF');
            string[] fields = line.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            try
            {
                switch (fields[0])
                {
                    case "v":
                        if (fields.Length is not (4 or 5 or 7 or 8)) throw new InvalidDataException("Expected a 3D vertex.");
                        if (vertices.Count >= MaxSourceVertices) throw new InvalidDataException("Vertex limit exceeded.");
                        Vector3 p = new(Number(fields[1]), Number(fields[2]), Number(fields[3]));


                        if (fields.Length is 5 or 8)
                        {
                            float w = Number(fields[4]); if (w == 0) throw new InvalidDataException("Zero homogeneous coordinate.");
                            p /= w;
                        }
                        for (int i = fields.Length is 5 or 8 ? 5 : 4; i < fields.Length; i++) Number(fields[i]);
                        if (!Finite(p)) throw new InvalidDataException("Vertex is outside the supported finite range.");
                        vertices.Add(p); break;
                    case "vt":
                        if (fields.Length is < 2 or > 4 || coords.Count >= MaxSourceVertices) throw new InvalidDataException("Invalid texture coordinate count.");
                        coords.Add(new(Number(fields[1]), fields.Length >= 3 ? Number(fields[2]) : 0));
                        if (fields.Length == 4) Number(fields[3]); break;
                    case "vn":
                        if (fields.Length != 4 || ++normals > MaxSourceVertices) throw new InvalidDataException("Invalid normal count.");
                        Number(fields[1]); Number(fields[2]); Number(fields[3]); break;
                    case "o": case "g":
                        group = SafeName(string.Join(' ', fields.Skip(1)), "default"); current = null; break;
                    case "f":
                    {
                        int count = fields.Length - 1;
                        if (count is < 3 or > MaxPolygonVertices) throw new InvalidDataException("Face must have 3 to 256 corners.");
                        var corners = new (int Position, int Uv)[count]; bool hasUv = true;
                        for (int i = 0; i < count; i++)
                        {
                            string[] index = fields[i + 1].Split('/');
                            if (index.Length is < 1 or > 3) throw new InvalidDataException("Invalid face index.");
                            int position = Index(index[0], vertices.Count), uv = -1;
                            if (index.Length >= 2 && index[1].Length > 0) uv = Index(index[1], coords.Count); else hasUv = false;
                            if (index.Length == 3) Index(index[2], normals);
                            corners[i] = (position, uv);
                        }
                        if (corners.Select(c => c.Position).Distinct().Count() != count) throw new InvalidDataException("Face repeats a vertex.");
                        int[] localTriangles = Triangulate(corners.Select(c => vertices[c.Position]).ToArray(), ref operations);
                        triangles = checked(triangles + localTriangles.Length / 3);
                        if (triangles > MaxTriangles) throw new InvalidDataException("Triangle limit exceeded.");
                        if (current is null || current.HasUv != hasUv)
                        {
                            if (parts.Count >= MaxMeshes) throw new InvalidDataException("Mesh limit exceeded.");
                            current = new(group, hasUv); parts.Add(current);
                        }
                        int[] mapped = new int[count];
                        for (int i = 0; i < count; i++)
                        {
                            var key = (corners[i].Position, hasUv ? corners[i].Uv : -1);
                            if (!current.Map.TryGetValue(key, out int at))
                            {
                                if (++outputVertices > MaxOutputVertices) throw new InvalidDataException("UV-seam vertex limit exceeded.");
                                at = current.Positions.Count; current.Map.Add(key, at); current.Positions.Add(vertices[key.Item1]);
                                if (hasUv) current.TexCoords.Add(coords[key.Item2]);
                            }
                            mapped[i] = at;
                        }
                        foreach (int index in localTriangles) current.Indices.Add(mapped[index]);
                        break;
                    }


                    case "s": case "usemtl": case "mtllib": case "l": case "p": break;
                    default: break;
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or OverflowException)
            { throw Error(lineNumber, ex.Message); }
        }
        if (parts.Count == 0 || triangles == 0) throw new InvalidDataException("OBJ contains no supported polygon faces.");
        Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        foreach (var part in parts) foreach (var p in part.Positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        return new(SafeName(name, "Imported OBJ"), parts.Select(p => new RageMesh(p.Name, p.Positions, p.Indices)
        { TexCoords = p.HasUv ? p.TexCoords : null }).ToArray(), min, max)
        { PresentationNote = "Imported OBJ geometry; external material libraries and texture paths are not loaded." };
    }

    private static InvalidDataException Error(int line, string message) => new($"OBJ line {line}: {message}");
    private static string SafeName(string value, string fallback)
    {
        string result = new(value.Where(c => !char.IsControl(c)).Take(256).ToArray());
        return string.IsNullOrWhiteSpace(result) ? fallback : result;
    }
    private static float Number(string value)
    {
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result) || !float.IsFinite(result) || Math.Abs(result) > 1_000_000)
            throw new InvalidDataException("Expected a finite number within the supported range.");
        return result;
    }
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) &&
        Math.Abs(value.X) <= 1_000_000 && Math.Abs(value.Y) <= 1_000_000 && Math.Abs(value.Z) <= 1_000_000;
    private static int Index(string text, int count)
    {
        if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value) || value == 0)
            throw new InvalidDataException("Face index must be a nonzero integer.");
        long result = value > 0 ? (long)value - 1 : (long)count + value;
        if (result < 0 || result >= count) throw new InvalidDataException("Face index is outside the available source list.");
        return (int)result;
    }

    private static int[] Triangulate(Vector3[] points, ref long operations)
    {
        int n = points.Length; Vector3 normal = Vector3.Zero; float extent = 0;
        foreach (var p in points) extent = Math.Max(extent, Vector3.Distance(p, points[0]));
        for (int i = 0; i < n; i++)
        {
            Vector3 a = points[i] - points[0], b = points[(i + 1) % n] - points[0]; normal += Vector3.Cross(a, b);
        }
        if (extent <= 0 || normal.LengthSquared() < extent * extent * extent * extent * 1e-14f)
            throw new InvalidDataException("Degenerate polygon.");
        normal = Vector3.Normalize(normal);
        foreach (var p in points) if (Math.Abs(Vector3.Dot(p - points[0], normal)) > extent * 0.00001f)
            throw new InvalidDataException("Nonplanar polygon cannot be triangulated faithfully.");
        int axis = Math.Abs(normal.X) > Math.Abs(normal.Y) ? (Math.Abs(normal.X) > Math.Abs(normal.Z) ? 0 : 2) :
            Math.Abs(normal.Y) > Math.Abs(normal.Z) ? 1 : 2;
        var q = points.Select(p => axis == 0 ? new Vector2(p.Y, p.Z) : axis == 1 ? new Vector2(p.X, p.Z) : new Vector2(p.X, p.Y)).ToArray();
        double epsilon = Math.Max((double)extent * extent * 1e-12, 1e-24);
        double Cross(int a, int b, int c) => ((double)q[b].X - q[a].X) * ((double)q[c].Y - q[a].Y) - ((double)q[b].Y - q[a].Y) * ((double)q[c].X - q[a].X);
        double area = 0; for (int i = 1; i + 1 < n; i++) area += Cross(0, i, i + 1);
        if (Math.Abs(area) <= epsilon) throw new InvalidDataException("Degenerate projected polygon.");
        int sign = area > 0 ? 1 : -1;

        for (int a = 0; a < n; a++) for (int b = a + 1; b < n; b++)
        {
            if (++operations > 50_000_000) throw new InvalidDataException("Polygon processing limit exceeded.");
            int aa = (a + 1) % n, bb = (b + 1) % n; if (a == bb || aa == b) continue;
            double c1 = Cross(a, aa, b), c2 = Cross(a, aa, bb), c3 = Cross(b, bb, a), c4 = Cross(b, bb, aa);
            if (Math.Max(q[a].X, q[aa].X) < Math.Min(q[b].X, q[bb].X) || Math.Max(q[b].X, q[bb].X) < Math.Min(q[a].X, q[aa].X) ||
                Math.Max(q[a].Y, q[aa].Y) < Math.Min(q[b].Y, q[bb].Y) || Math.Max(q[b].Y, q[bb].Y) < Math.Min(q[a].Y, q[aa].Y)) continue;
            if (c1 * c2 <= 0 && c3 * c4 <= 0) throw new InvalidDataException("Self-intersecting polygon.");
        }
        var remaining = Enumerable.Range(0, n).ToList(); var triangles = new List<int>((n - 2) * 3);
        while (remaining.Count > 3)
        {
            bool found = false;
            for (int i = 0; i < remaining.Count; i++)
            {
                int a = remaining[(i + remaining.Count - 1) % remaining.Count], b = remaining[i], c = remaining[(i + 1) % remaining.Count];
                if (Cross(a, b, c) * sign <= epsilon) continue; bool inside = false;
                foreach (int d in remaining)
                {
                    if (++operations > 50_000_000) throw new InvalidDataException("Polygon processing limit exceeded.");
                    if (d == a || d == b || d == c) continue;
                    if (Cross(a, b, d) * sign >= -epsilon && Cross(b, c, d) * sign >= -epsilon && Cross(c, a, d) * sign >= -epsilon)
                    { inside = true; break; }
                }
                if (inside) continue;
                triangles.AddRange([a, b, c]); remaining.RemoveAt(i); found = true; break;
            }
            if (!found) throw new InvalidDataException("Polygon has no supported nondegenerate triangulation.");
        }
        if (Cross(remaining[0], remaining[1], remaining[2]) * sign <= epsilon) throw new InvalidDataException("Degenerate final triangle.");
        triangles.AddRange(remaining); return triangles.ToArray();
    }
}
