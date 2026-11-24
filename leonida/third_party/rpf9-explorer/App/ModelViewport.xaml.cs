using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;

namespace RpfPreview.App;

internal sealed record MeshPreviewPart(string Name, IReadOnlyList<Vector3> Positions, IReadOnlyList<int> Indices)
{
    public IReadOnlyList<Vector2>? TexCoords { get; init; }
    public ImageSource? Texture { get; init; }
    public IReadOnlyList<uint>? VertexColors { get; init; }
}

public partial class ModelViewport : UserControl
{
    private Point3D _center;
    private Point3D _initialCenter;
    private double _radius = 1, _distance = 3, _yaw = .7, _pitch = .35;
    private Point _last;
    private MouseButton? _drag;
    public int PresentedVertices { get; private set; }
    public int PresentedTriangles { get; private set; }
    public int PresentedTexturedMeshes { get; private set; }
    public int PresentedVertexColoredMeshes { get; private set; }

    public ModelViewport() { InitializeComponent(); UpdateCamera(); }

    internal void ShowMeshes(IEnumerable<MeshPreviewPart> meshes, bool resetView = true)
    {
        var group = new Model3DGroup();
        var bounds = Rect3D.Empty;
        int vertices = 0, triangles = 0, textured = 0, colored = 0;
        long colorAtlasPixels = 0;
        foreach (var part in meshes)
        {
            if (part.Positions.Count < 3 || part.Positions.Count > 2_000_000 || part.Indices.Count % 3 != 0
                || part.Indices.Count > 6_000_000 || part.Indices.Any(i => i < 0 || i >= part.Positions.Count))
                throw new InvalidOperationException("The decoded mesh has invalid vertex/index bounds.");
            var points = new Point3DCollection(part.Positions.Count);
            foreach (var p in part.Positions)
            {
                if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) throw new InvalidOperationException("The mesh contains a non-finite position.");

                var point = new Point3D(p.X, p.Z, -p.Y);
                points.Add(point); bounds.Union(point);
            }
            var geometry = new MeshGeometry3D { Positions = points, TriangleIndices = new Int32Collection(part.Indices) };
            Brush brush = new SolidColorBrush(Color.FromRgb(195, 201, 208));
            if (part.Texture != null && part.TexCoords is { } uv && uv.Count == part.Positions.Count)
            {
                if (uv.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y))) throw new InvalidOperationException("The mesh contains a non-finite texture coordinate.");
                geometry.TextureCoordinates = new PointCollection(uv.Select(p => new Point(p.X, p.Y)));
                brush = new ImageBrush(part.Texture) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute,
                    Viewport = new Rect(0, 0, 1, 1), Stretch = Stretch.Fill };
                textured++;
            }
            else if (part.TexCoords == null && part.VertexColors is { } colors && colors.Count == part.Positions.Count
                && TryVertexColorPreview(part, colors, 4_000_000 - colorAtlasPixels) is { } preview)
            {
                geometry = preview.Geometry; colorAtlasPixels += preview.Pixels;
                brush = new ImageBrush(preview.Image) { Stretch = Stretch.Fill }; colored++;
            }
            geometry.Freeze();
            var material = new DiffuseMaterial(brush);
            material.Freeze();
            var model = new GeometryModel3D(geometry, material) { BackMaterial = material };
            model.Freeze(); group.Children.Add(model);
            vertices += points.Count; triangles += part.Indices.Count / 3;
        }
        if (bounds.IsEmpty || triangles == 0) throw new InvalidOperationException("No validated triangles were decoded from this resource.");
        group.Freeze(); GeometryRoot.Content = group;
        PresentedVertices = vertices; PresentedTriangles = triangles;
        PresentedTexturedMeshes = textured;
        PresentedVertexColoredMeshes = colored;
        _initialCenter = new Point3D(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
        _radius = Math.Max(.01, Math.Sqrt(bounds.SizeX * bounds.SizeX + bounds.SizeY * bounds.SizeY + bounds.SizeZ * bounds.SizeZ) / 2);
        ModelInfoText.Text = $"{vertices:N0} vertices · {triangles:N0} triangles · drag to orbit, wheel to zoom";
        if (resetView) ResetView(); else UpdateCamera();
    }
    public void Clear()
    {
        GeometryRoot.Content = null;
        PresentedVertices = PresentedTriangles = PresentedTexturedMeshes = PresentedVertexColoredMeshes = 0;
    }
    private sealed record VertexColorPreview(MeshGeometry3D Geometry, BitmapSource Image, long Pixels);
    private static VertexColorPreview? TryVertexColorPreview(MeshPreviewPart part, IReadOnlyList<uint> colors, long pixelBudget)
    {
        int faces = part.Indices.Count / 3;
        if (faces is < 1 or > 250_000) return null;
        int tile = faces <= 4096 ? 8 : 4, cell = tile + 2, columns = (int)Math.Ceiling(Math.Sqrt(faces)), rows = (faces + columns - 1) / columns;
        int width = columns * cell, height = rows * cell;
        long pixels = (long)width * height;
        if (pixels > pixelBudget) return null;
        var rgba = new byte[checked(width * height * 4)];
        var geometry = new MeshGeometry3D { Positions = new Point3DCollection(faces * 3), TriangleIndices = new Int32Collection(faces * 3), TextureCoordinates = new PointCollection(faces * 3) };
        for (int face = 0; face < faces; face++)
        {
            int a = part.Indices[face * 3], b = part.Indices[face * 3 + 1], c = part.Indices[face * 3 + 2];
            int x = face % columns * cell + 1, y = face / columns * cell + 1;
            foreach (int index in new[] { a, b, c })
            {
                var p = part.Positions[index]; geometry.Positions.Add(new(p.X, p.Z, -p.Y)); geometry.TriangleIndices.Add(geometry.TriangleIndices.Count);
            }
            geometry.TextureCoordinates.Add(new((x + .5) / width, (y + .5) / height));
            geometry.TextureCoordinates.Add(new((x + tile - .5) / width, (y + .5) / height));
            geometry.TextureCoordinates.Add(new((x + .5) / width, (y + tile - .5) / height));
            for (int py = -1; py <= tile; py++)
            for (int px = -1; px <= tile; px++)
            {
                double u = (double)px / (tile - 1), v = (double)py / (tile - 1);
                int at = ((y + py) * width + x + px) * 4;
                for (int channel = 0; channel < 3; channel++)
                {
                    int shift = channel * 8;
                    double value = ((colors[a] >> shift) & 255) * (1 - u - v) + ((colors[b] >> shift) & 255) * u + ((colors[c] >> shift) & 255) * v;
                    rgba[at + 2 - channel] = (byte)Math.Clamp((int)Math.Round(value), 0, 255);
                }
                rgba[at + 3] = 255;
            }
        }
        geometry.Freeze();
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, rgba, width * 4); image.Freeze();
        return new(geometry, image, pixels);
    }
    private double FitDistance(Size size)
    {
        double ratio = size.Width > 0 && size.Height > 0 ? Math.Min(1, size.Height / size.Width) : .65;
        double halfAngle = Math.Atan(Math.Tan(Camera.FieldOfView * Math.PI / 360) * ratio);
        return _radius / Math.Sin(Math.Max(.01, halfAngle)) * 1.12;
    }
    private void ResetView() { _center = _initialCenter; _distance = FitDistance(new(Scene.ActualWidth, Scene.ActualHeight)); _yaw = .7; _pitch = .35; UpdateCamera(); }
    private void Scene_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Camera == null) return;
        if (e.PreviousSize.Width > 0 && e.PreviousSize.Height > 0) _distance *= FitDistance(e.NewSize) / FitDistance(e.PreviousSize);
        UpdateCamera();
    }
    private void UpdateCamera()
    {
        var direction = new Vector3D(Math.Cos(_pitch) * Math.Sin(_yaw), Math.Sin(_pitch), Math.Cos(_pitch) * Math.Cos(_yaw));
        Camera.Position = _center + direction * _distance;
        Camera.LookDirection = _center - Camera.Position;
        Camera.UpDirection = new Vector3D(0, 1, 0);
        Camera.NearPlaneDistance = Math.Max(.0001, _radius / 1000);
        Camera.FarPlaneDistance = Math.Max(1000, _distance + _radius * 1000);
    }
    private void Scene_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Right or MouseButton.Middle)) return;
        _last = e.GetPosition(Scene); _drag = e.ChangedButton; Scene.CaptureMouse(); Focus(); e.Handled = true;
    }
    private void Scene_MouseMove(object sender, MouseEventArgs e)
    {
        if (_drag == null) return;
        var point = e.GetPosition(Scene); var delta = point - _last; _last = point;
        if (_drag == MouseButton.Left) { _yaw -= delta.X * .008; _pitch = Math.Clamp(_pitch + delta.Y * .008, -1.5, 1.5); }
        else
        {
            var right = Vector3D.CrossProduct(Camera.LookDirection, Camera.UpDirection); right.Normalize();
            var up = Vector3D.CrossProduct(right, Camera.LookDirection); up.Normalize();
            _center += (right * -delta.X + up * delta.Y) * (_distance / Math.Max(200, Scene.ActualHeight));
        }
        UpdateCamera();
    }
    private void Scene_MouseUp(object sender, MouseButtonEventArgs e) { _drag = null; Scene.ReleaseMouseCapture(); }
    private void Scene_LostMouseCapture(object sender, MouseEventArgs e) { _drag = null; }
    private void Scene_MouseWheel(object sender, MouseWheelEventArgs e) { _distance = Math.Clamp(_distance * Math.Exp(-e.Delta * .001), _radius * .02, _radius * 1000); UpdateCamera(); e.Handled = true; }
    private void Reset_Click(object sender, RoutedEventArgs e) => ResetView();
    private void Viewport_KeyDown(object sender, KeyEventArgs e) { if (e.Key is Key.R or Key.F) { ResetView(); e.Handled = true; } }
}
