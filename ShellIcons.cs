using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace IslandBar;

static class ShellIcons
{
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    struct FileInfo {public nint Icon;public int Index;public uint Attributes;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string Name;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=80)]public string Type;}
    [ComImport,Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IImageList {
        [PreserveSig]int Add(nint image,nint mask,out int index);
        [PreserveSig]int ReplaceIcon(int index,nint icon,out int result);
        [PreserveSig]int SetOverlayImage(int index,int overlay);
        [PreserveSig]int Replace(int index,nint image,nint mask);
        [PreserveSig]int AddMasked(nint image,uint mask,out int index);
        [PreserveSig]int Draw(nint parameters);
        [PreserveSig]int Remove(int index);
        [PreserveSig]int GetIcon(int index,uint flags,out nint icon);
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]static extern nint SHGetFileInfo(string path,uint attributes,ref FileInfo info,uint size,uint flags);
    [DllImport("shell32.dll")]static extern int SHGetImageList(int size,ref Guid iid,[MarshalAs(UnmanagedType.Interface)]out IImageList list);
    [DllImport("user32.dll")]static extern bool DestroyIcon(nint icon);
    public static BitmapSource? Load(string path)
    {
        try {
            var info=new FileInfo();
            if(SHGetFileInfo(path,0,ref info,(uint)Marshal.SizeOf<FileInfo>(),0x4000)==0)return null;
            foreach(int size in new[]{4,2,0}) {
                IImageList? list=null;nint icon=0;
                try {
                    var iid=typeof(IImageList).GUID;
                    if(SHGetImageList(size,ref iid,out list)<0||list.GetIcon(info.Index,1,out icon)<0||icon==0)continue;
                    var bitmap=Imaging.CreateBitmapSourceFromHIcon(icon,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());
                    var bounds=VisibleBounds(bitmap);
                    // Reject a small legacy icon sitting in a jumbo canvas.
                    if(size==4&&Math.Max(bounds.Width,bounds.Height)<96)continue;
                    BitmapSource result=bitmap;
                    int actual=Math.Max(bounds.Width,bounds.Height);
                    if(actual>0&&actual<bitmap.PixelWidth*.65) {
                        int edge=Math.Min(bitmap.PixelWidth,actual+4);
                        int x=Math.Clamp(bounds.X+bounds.Width/2-edge/2,0,bitmap.PixelWidth-edge);
                        int y=Math.Clamp(bounds.Y+bounds.Height/2-edge/2,0,bitmap.PixelHeight-edge);
                        result=new CroppedBitmap(bitmap,new Int32Rect(x,y,edge,edge));
                    }
                    result.Freeze();return result;
                }finally{if(icon!=0)DestroyIcon(icon);if(list is not null)Marshal.ReleaseComObject(list);}
            }
        }catch(Exception ex){Program.Log("large icon: "+ex.Message);}
        return null;
    }
    static Int32Rect VisibleBounds(BitmapSource source)
    {
        var bitmap=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);
        int w=bitmap.PixelWidth,h=bitmap.PixelHeight;var pixels=new byte[w*h*4];bitmap.CopyPixels(pixels,w*4,0);
        int left=w,top=h,right=-1,bottom=-1;
        for(int y=0;y<h;y++)for(int x=0;x<w;x++)if(pixels[(y*w+x)*4+3]>24){left=Math.Min(left,x);top=Math.Min(top,y);right=Math.Max(right,x);bottom=Math.Max(bottom,y);}
        return right>=left?new Int32Rect(left,top,right-left+1,bottom-top+1):Int32Rect.Empty;
    }
}
