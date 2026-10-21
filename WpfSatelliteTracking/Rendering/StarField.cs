using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace WpfSatelliteTracking.Rendering;

// Decorative, deterministic celestial backdrop; these are not catalogued stars.
// Star cores are geometry, so fullscreen does not magnify a low-resolution texture.
internal static class StarField
{
    private static readonly Lazy<Model3DGroup> template = new(Build);
    public static Model3DGroup Create() => template.Value;
    private static readonly Color[] tints = [Color.FromRgb(244,246,255), Color.FromRgb(255,246,226), Color.FromRgb(255,218,180), Color.FromRgb(204,223,255)];

    private static Model3DGroup Build()
    {
        var result = new Model3DGroup();
        var background = new ImageBrush(GalacticBackground());
        RenderOptions.SetBitmapScalingMode(background, BitmapScalingMode.HighQuality); background.Freeze();
        var shell = SceneGeometry.Sphere(510, new EmissiveMaterial(background), 128, 64);
        shell.BackMaterial = shell.Material; shell.Material = null; result.Children.Add(shell);
        var meshes = new MeshGeometry3D[16];
        for (int i=0;i<meshes.Length;i++) meshes[i]=new MeshGeometry3D();
        var halos = new MeshGeometry3D[4];
        for (int i=0;i<halos.Length;i++) halos[i]=new MeshGeometry3D();
        var random = new Random(731);
        for (int i=0;i<14000;i++)
        {
            // Uniform solid angle, rather than uniform texture rows near the poles.
            double latitude = Math.Asin(2*random.NextDouble()-1), longitude=2*Math.PI*random.NextDouble();
            var outward = new Vector3D(Math.Cos(latitude)*Math.Cos(longitude), Math.Sin(latitude), Math.Cos(latitude)*Math.Sin(longitude));
            var right = Vector3D.CrossProduct(new Vector3D(0,1,0),outward); right.Normalize();
            var up = Vector3D.CrossProduct(outward,right);
            double chance=random.NextDouble(); int tier=chance<0.72?0:chance<0.94?1:chance<0.992?2:3;
            double color=random.NextDouble(); int tint=color<0.55?0:color<0.83?1:color<0.94?2:3;
            double angle=(tier switch{0=>0.010,1=>0.016,2=>0.025,_=>0.038})*(0.8+0.4*random.NextDouble());
            double size=500*Math.Tan(angle*Math.PI/180);
            AddCore(meshes[tint*4+tier],(Point3D)(outward*500),right,up,size);
            if(tier==3) AddHalo(halos[tint],(Point3D)(outward*500.1),right,up,size*4);
        }
        // Halos first, opaque star cores last: no transparent core/depth sorting artifacts.
        for(int tint=0;tint<4;tint++)
        {
            var color=tints[tint];
            var glow=new RadialGradientBrush();
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(75,color.R,color.G,color.B),0));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(16,color.R,color.G,color.B),0.25));
            glow.GradientStops.Add(new GradientStop(Colors.Transparent,1));
            var brush=SceneGeometry.BakeBrush(glow,64,64); halos[tint].Freeze();
            result.Children.Add(new GeometryModel3D(halos[tint],new EmissiveMaterial(brush)));
        }
        for(int tint=0;tint<4;tint++) for(int tier=0;tier<4;tier++)
        {
            double brightness=tier switch{0=>0.42,1=>0.64,2=>0.86,_=>1};var c=tints[tint];
            var material=new EmissiveMaterial(new SolidColorBrush(Color.FromRgb((byte)(c.R*brightness),(byte)(c.G*brightness),(byte)(c.B*brightness))));
            var mesh=meshes[tint*4+tier];mesh.Freeze();result.Children.Add(new GeometryModel3D(mesh,material));
        }
        result.Freeze();return result;
    }
    private static void AddCore(MeshGeometry3D mesh,Point3D center,Vector3D right,Vector3D up,double radius)
    {
        int start=mesh.Positions.Count;mesh.Positions.Add(center);
        for(int i=0;i<8;i++){double a=i*Math.PI/4;mesh.Positions.Add(center+radius*(right*Math.Cos(a)+up*Math.Sin(a)));}
        for(int i=0;i<8;i++){mesh.TriangleIndices.Add(start);mesh.TriangleIndices.Add(start+1+(i+1)%8);mesh.TriangleIndices.Add(start+1+i);}
    }
    private static void AddHalo(MeshGeometry3D mesh,Point3D center,Vector3D right,Vector3D up,double radius)
    {
        int start=mesh.Positions.Count;
        foreach(var uv in new[]{new Point(0,0),new Point(1,0),new Point(1,1),new Point(0,1)})
        {mesh.Positions.Add(center+radius*(right*(uv.X*2-1)+up*(1-uv.Y*2)));mesh.TextureCoordinates.Add(uv);}
        foreach(int i in new[]{0,1,2,0,2,3})mesh.TriangleIndices.Add(start+i);
    }
    private static BitmapSource GalacticBackground()
    {
        const int width=2048,height=1024;var pixels=new byte[width*height*4];
        var axis=new Vector3D(0.37,0.82,-0.44);axis.Normalize();
        for(int y=0;y<height;y++)
        {
            double lat=Math.PI/2-(y+0.5)*Math.PI/height,cl=Math.Cos(lat),sl=Math.Sin(lat);
            for(int x=0;x<width;x++)
            {
                double lon=(x+0.5)*2*Math.PI/width-Math.PI;
                double nx=cl*Math.Cos(lon),ny=sl,nz=-cl*Math.Sin(lon);
                double band=nx*axis.X+ny*axis.Y+nz*axis.Z;
                double cloud=0.55+0.3*Math.Sin(11*nx+7*nz)*Math.Sin(13*ny-5*nx)+0.15*Math.Sin(31*nz+19*ny);
                double glow=Math.Exp(-band*band/0.018)*Math.Clamp(cloud,0,1);
                double dust=1-0.65*Math.Exp(-Math.Pow(band+0.018*Math.Sin(17*nx+9*nz),2)/0.0008);
                double light=glow*dust;int p=(y*width+x)*4;
                pixels[p]=(byte)(8+14*light);pixels[p+1]=(byte)(5+11*light);pixels[p+2]=(byte)(4+10*light);pixels[p+3]=255;
            }
        }
        var bitmap=BitmapSource.Create(width,height,96,96,PixelFormats.Pbgra32,null,pixels,width*4);bitmap.Freeze();return bitmap;
    }
}
