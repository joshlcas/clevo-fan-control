// Writes fancontrol.ico from the same drawing code the app uses at runtime.
//
//   csc /target:exe /main:ClevoFan.IconTool /out:IconTool.exe ^
//       /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
//       tools\IconTool.cs FanControl.cs
//   IconTool.exe fancontrol.ico
//
// /main picks this entry point over FanControl's Program.Main. Compiling
// FanControl.cs here is safe: only Logo.Render is ever called, so nothing
// reaches the EC.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace ClevoFan
{
    static class IconTool
    {
        static readonly int[] Sizes = new int[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

        static int Main(string[] args)
        {
            string path = args.Length > 0 ? args[0] : "fancontrol.ico";
            MemoryStream images = new MemoryStream();
            byte[][] frames = new byte[Sizes.Length][];

            for (int i = 0; i < Sizes.Length; i++)
            {
                using (Bitmap bmp = Logo.Render(Sizes[i]))
                using (MemoryStream png = new MemoryStream())
                {
                    bmp.Save(png, ImageFormat.Png);
                    frames[i] = png.ToArray();
                }
            }

            using (FileStream fs = File.Create(path))
            using (BinaryWriter w = new BinaryWriter(fs))
            {
                w.Write((short)0);               // reserved
                w.Write((short)1);               // type: icon
                w.Write((short)Sizes.Length);

                int offset = 6 + 16 * Sizes.Length;
                for (int i = 0; i < Sizes.Length; i++)
                {
                    int s = Sizes[i];
                    w.Write((byte)(s >= 256 ? 0 : s));   // 0 means 256
                    w.Write((byte)(s >= 256 ? 0 : s));
                    w.Write((byte)0);                    // palette
                    w.Write((byte)0);                    // reserved
                    w.Write((short)1);                   // planes
                    w.Write((short)32);                  // bpp
                    w.Write(frames[i].Length);
                    w.Write(offset);
                    offset += frames[i].Length;
                }

                for (int i = 0; i < Sizes.Length; i++) w.Write(frames[i]);
            }

            Console.WriteLine(path + ": " + Sizes.Length + " sizes");
            return 0;
        }
    }
}
