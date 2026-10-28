using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Seam;
using CodeWalker.GameFiles;

// Optional actual-game integration probe. The portable app never bundles these game assets.
public static class ClothingPreviewChecks
{
    public static int Run(string screenshotFolder)
    {
        string game = ClothingGameAssets.DetectGameFolder();
        if (string.IsNullOrEmpty(game)) throw new InvalidOperationException("GTA V Legacy is required for the actual-game preview integration probe.");
        Directory.CreateDirectory(screenshotFolder);
        var preview = new ClothingViewport();
        var window = new Window { Width=700, Height=850, Content=preview, ShowInTaskbar=false, WindowStartupLocation=WindowStartupLocation.CenterScreen };
        int checks=0;
        Action<bool,string> check=(condition,message)=> { if(!condition) throw new InvalidOperationException(message); checks++; Console.WriteLine("OK " + message); };
        try
        {
            window.Show();
            preview.SetPed("male",game);
            check(preview.HasPed && preview.PedName=="mp_m_freemode_01" && preview.MeshCount>=5,"actual male freemode body and skin load from installed game archives");
            var before=Render(preview);
            Save(before,Path.Combine(screenshotFolder,"clothing-ped-male.png"));
            byte[] blue=new byte[64*64*4];
            for(int i=0;i<blue.Length;i+=4) { blue[i]=240;blue[i+1]=80;blue[i+2]=20;blue[i+3]=255; }
            var bitmap=BitmapSource.Create(64,64,96,96,PixelFormats.Bgra32,null,blue,256);bitmap.Freeze();
            preview.RefreshTexture("head_diff_000_a_whi",bitmap);
            var changed=Render(preview);
            check(Difference(before,changed)>50,"real-time YTD image replacement changes the rendered ped without reloading its mesh");
            Save(changed,Path.Combine(screenshotFolder,"clothing-live-texture.png"));
            preview.SetPed("male",game);
            var clean=Render(preview);
            preview.ShowTattoo(bitmap,"ZONE_TORSO",.5,.5,.3,.3,20);
            check(Difference(clean,Render(preview))>50,"tattoo UV placement updates the actual body skin material");
            preview.ShowTattoo(null,"ZONE_TORSO",.5,.5,.3,.3,0);
            string temporary=Path.Combine(Path.GetTempPath(),"seam-preview-check-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            try
            {
                var archive=new RpfFile(Path.Combine(game,"x64v.rpf"),"x64v.rpf");archive.ScanStructure(null,null);
                var streamed=archive.Children.Single(r=>r.NameLower=="streamedpeds_mp.rpf");
                var modelEntry=streamed.AllEntries.OfType<RpfFileEntry>().First(e=>e.Name=="jbib_001_u.ydd"&&e.Path.Contains("\\mp_m_freemode_01\\"));
                var textureEntry=streamed.AllEntries.OfType<RpfFileEntry>().First(e=>e.Name=="jbib_diff_001_a_uni.ytd"&&e.Path.Contains("\\mp_m_freemode_01\\"));
                string modelPath=Path.Combine(temporary,modelEntry.Name),texturePath=Path.Combine(temporary,textureEntry.Name);
                File.WriteAllBytes(modelPath,RpfFile.GetFile<YddFile>(modelEntry).Save());
                File.WriteAllBytes(texturePath,RpfFile.GetFile<YtdFile>(textureEntry).Save());
                preview.ShowItem(new ClothingItem { Name="Native shirt",SourcePath=modelPath,Ped="male",Component=11,TexturePaths=new System.Collections.Generic.List<string>{texturePath} });
                var clothed=Render(preview);
                check(Difference(clean,clothed)>50,"imported native YDD clothing and external YTD render over the ped");
                preview.RefreshTexture("jbib_diff_001_b_uni",bitmap);
                check(Difference(clothed,Render(preview))>50,"selecting another diffuse texture variant updates the garment");
                Save(Render(preview),Path.Combine(screenshotFolder,"clothing-apparel-live.png"));
            }
            finally { foreach(string file in Directory.GetFiles(temporary)) File.Delete(file); Directory.Delete(temporary); }
            preview.SetPed("female",game);
            check(preview.HasPed && preview.PedName=="mp_f_freemode_01" && preview.MeshCount>=5,"actual female freemode body and skin load from installed game archives");
            Save(Render(preview),Path.Combine(screenshotFolder,"clothing-ped-female.png"));
            preview.SetPed("male",Path.Combine(screenshotFolder,"missing-game"));
            check(!preview.HasPed && preview.Status.Contains("GTA V"),"missing installation has a truthful message and no substitute ped");
            return checks;
        }
        finally { window.Close(); }
    }
    static RenderTargetBitmap Render(ClothingViewport preview)
    {
        preview.UpdateLayout();
        preview.Dispatcher.Invoke(DispatcherPriority.Render,new Action(()=>{}));
        var bitmap=new RenderTargetBitmap((int)preview.ActualWidth,(int)preview.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(preview);bitmap.Freeze();return bitmap;
    }
    static void Save(BitmapSource bitmap,string path)
    {
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(path))png.Save(stream);
    }
    static int Difference(BitmapSource first,BitmapSource second)
    {
        int stride=first.PixelWidth*4;byte[] a=new byte[stride*first.PixelHeight],b=new byte[a.Length];first.CopyPixels(a,stride,0);second.CopyPixels(b,stride,0);int count=0;
        for(int i=0;i<a.Length;i+=4) if(Math.Abs(a[i]-b[i])+Math.Abs(a[i+1]-b[i+1])+Math.Abs(a[i+2]-b[i+2])>30)count++;return count;
    }
}
