using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace WpfOrbitDemo.Rendering;

// Static (non-articulated) satellites drawn as one mesh per material instead of one model per satellite.
// WPF Viewport3D has no GPU instancing, so this is the CPU-side equivalent: each frame the pre-flattened template
// vertices are transformed by every satellite's pose into a shared buffer. Indices/UVs are rebuilt only when the set
// of satellites (or their level of detail / lit state) changes; positions/normals only when poses moved.
internal sealed class SatelliteBatch
{
    internal sealed class Piece(Point3D[] positions, Vector3D[] normals, Point[] uvs, int[] indices, Material material)
    {
        public Point3D[] Positions { get; } = positions;
        public Vector3D[] Normals { get; } = normals;
        public Point[] Uvs { get; } = uvs;
        public int[] Indices { get; } = indices;
        public Material Material { get; } = material;
    }

    private sealed class Batch
    {
        public readonly MeshGeometry3D Mesh = new();
        public readonly GeometryModel3D Model;
        public readonly List<(int Owner, Piece Piece, Matrix3D Pose)> Pending = [];
        public readonly List<(int Owner, Piece Piece)> Current = [];
        public readonly List<(int Start, int Owner)> Starts = [];
        public Batch(Material material) => Model = new GeometryModel3D(Mesh, material);
    }

    private readonly Dictionary<Material, Batch> batches = new(ReferenceEqualityComparer.Instance);
    public Model3DGroup Model { get; } = new();

    // Flatten a (frozen) template group into pieces in its own model space, one per GeometryModel3D.
    public static Piece[] Flatten(Model3D template)
    {
        var pieces = new List<Piece>();
        void Walk(Model3D part, Matrix3D parent)
        {
            var matrix = part.Transform.Value; matrix.Append(parent);
            if (part is Model3DGroup group) { foreach (var child in group.Children) Walk(child, matrix); return; }
            var model = (GeometryModel3D)part; var mesh = (MeshGeometry3D)model.Geometry;
            var positions = mesh.Positions.Select(matrix.Transform).ToArray();
            var normals = mesh.Normals.Count == mesh.Positions.Count
                ? mesh.Normals.Select(n => { var v = matrix.Transform(n); if (v.LengthSquared > 1e-30) v.Normalize(); return v; }).ToArray()
                : new Vector3D[positions.Length];
            var uvs = mesh.TextureCoordinates.Count == mesh.Positions.Count ? mesh.TextureCoordinates.ToArray() : new Point[positions.Length];
            pieces.Add(new Piece(positions, normals, uvs, mesh.TriangleIndices.ToArray(), model.Material));
        }
        Walk(template, Matrix3D.Identity);
        return pieces.ToArray();
    }

    public void Add(int owner, Piece[] pieces, Matrix3D pose)
    {
        foreach (var piece in pieces)
        {
            if (!batches.TryGetValue(piece.Material, out var batch)) batches.Add(piece.Material, batch = new Batch(piece.Material));
            batch.Pending.Add((owner, piece, pose));
        }
    }

    public void Commit(bool posesChanged)
    {
        foreach (var batch in batches.Values)
        {
            var pending = batch.Pending;
            bool topology = pending.Count != batch.Current.Count;
            for (int k = 0; !topology && k < pending.Count; k++)
                topology = pending[k].Owner != batch.Current[k].Owner || !ReferenceEquals(pending[k].Piece, batch.Current[k].Piece);
            if (pending.Count == 0)
            {
                if (batch.Current.Count > 0) { Model.Children.Remove(batch.Model); batch.Current.Clear(); batch.Starts.Clear(); }
                continue;
            }
            int vertexCount = 0, indexCount = 0;
            foreach (var item in pending) { vertexCount += item.Piece.Positions.Length; indexCount += item.Piece.Indices.Length; }
            if (topology)
            {
                var indices = new Int32Collection(indexCount); var uvs = new PointCollection(vertexCount);
                batch.Current.Clear(); batch.Starts.Clear();
                int start = 0;
                foreach (var (owner, piece, _) in pending)
                {
                    foreach (int index in piece.Indices) indices.Add(start + index);
                    foreach (var uv in piece.Uvs) uvs.Add(uv);
                    batch.Current.Add((owner, piece)); batch.Starts.Add((start, owner));
                    start += piece.Positions.Length;
                }
                indices.Freeze(); uvs.Freeze();
                batch.Mesh.TriangleIndices = indices; batch.Mesh.TextureCoordinates = uvs;
            }
            if (topology || posesChanged)
            {
                var positions = new Point3DCollection(vertexCount); var normals = new Vector3DCollection(vertexCount);
                foreach (var (_, piece, pose) in pending)
                {
                    foreach (var p in piece.Positions) positions.Add(pose.Transform(p));
                    foreach (var n in piece.Normals) { var v = pose.Transform(n); if (v.LengthSquared > 1e-30) v.Normalize(); normals.Add(v); }
                }
                positions.Freeze(); normals.Freeze();
                batch.Mesh.Positions = positions; batch.Mesh.Normals = normals;
            }
            if (!Model.Children.Contains(batch.Model)) Model.Children.Add(batch.Model);
            pending.Clear();
        }
    }

    // Satellite index that owns a hit vertex in one of the batch meshes, for click selection.
    public int? OwnerOf(Model3D model, int vertexIndex)
    {
        foreach (var batch in batches.Values)
        {
            if (!ReferenceEquals(batch.Model, model)) continue;
            int? owner = null;
            foreach (var (start, index) in batch.Starts) { if (start > vertexIndex) break; owner = index; }
            return owner;
        }
        return null;
    }
}
