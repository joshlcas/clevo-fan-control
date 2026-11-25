using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ClevoFan
{
    static class Native
    {
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadLibraryW(string path);

        // Option B: the exe is a Windows-subsystem binary, so no console is ever
        // created for the GUI. CLI runs attach to the calling shell's console.
        [DllImport("kernel32")]
        public static extern bool AttachConsole(int processId);

        [DllImport("kernel32")]
        public static extern bool AllocConsole();

        [DllImport("user32")]
        public static extern bool SetProcessDPIAware();

        // Not DPI-aware means these are already in device pixels, which is what
        // WinForms wants, so a screen that fits at 100 % still fits at 150 %.
        [DllImport("user32")]
        public static extern int GetSystemMetrics(int index);

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32")]
        public static extern bool SystemParametersInfo(uint action, uint param, out Rect rect, uint flags);

        // Power plans. Plain user rights: activating a scheme only affects the
        // logged-on user, which is exactly the scope needed here.
        [DllImport("powrprof.dll")]
        public static extern uint PowerGetActiveScheme(IntPtr userRoot, out IntPtr schemeGuid);

        [DllImport("powrprof.dll")]
        public static extern uint PowerSetActiveScheme(IntPtr userRoot, ref Guid schemeGuid);

        [DllImport("powrprof.dll")]
        public static extern uint PowerWriteACValueIndex(IntPtr userRoot, ref Guid scheme, ref Guid sub,
            ref Guid setting, uint value);

        [DllImport("powrprof.dll")]
        public static extern uint PowerWriteDCValueIndex(IntPtr userRoot, ref Guid scheme, ref Guid sub,
            ref Guid setting, uint value);

        // The scheme is created once by tools\New-PowerPlan.ps1 (admin only);
        // the exe only ever activates what is already there.
        [DllImport("kernel32", SetLastError = true)]
        public static extern IntPtr LocalFree(IntPtr h);

        [StructLayout(LayoutKind.Sequential)]
        public struct FileTime
        {
            public uint Low;
            public uint High;
            public ulong Value { get { return ((ulong)High << 32) | Low; } }
        }

        // One call, three values. Thousands of times cheaper than a PDH counter
        // and it never allocates, blocks or spins up a provider thread.
        [DllImport("kernel32", SetLastError = true)]
        public static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
    }

    // The app mark: four swept blades around a hub, in the accent blue, drawn in
    // code so the build stays a single .cs file with no resource to lose.
    static class Logo
    {
        public static readonly Color Accent = Color.FromArgb(0x1E, 0x6F, 0xD9);

        public static Bitmap Render(int size)
        {
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                Draw(g, size);
            }
            return bmp;
        }

        public static System.Drawing.Icon ToIcon(int size)
        {
            return System.Drawing.Icon.FromHandle(Render(size).GetHicon());
        }

        static void Draw(Graphics g, int size)
        {
            float s = size / 16f;

            using (SolidBrush wash = new SolidBrush(Color.FromArgb(0xE9, 0xF1, 0xFC)))
                g.FillEllipse(wash, 0.5f * s, 0.5f * s, 15f * s, 15f * s);
            using (Pen ring = new Pen(Accent, Math.Max(1f, s)))
                g.DrawEllipse(ring, 0.5f * s, 0.5f * s, 15f * s, 15f * s);

            using (SolidBrush blade = new SolidBrush(Accent))
            {
                for (int i = 0; i < 4; i++)
                {
                    using (GraphicsPath p = Blade(s))
                    using (Matrix m = new Matrix())
                    {
                        m.RotateAt(90f * i, new PointF(8f * s, 8f * s));
                        p.Transform(m);
                        g.FillPath(blade, p);
                    }
                }
            }

            using (SolidBrush hub = new SolidBrush(Accent))
                g.FillEllipse(hub, 6.3f * s, 6.3f * s, 3.4f * s, 3.4f * s);
            using (SolidBrush hole = new SolidBrush(Color.White))
                g.FillEllipse(hole, 7.15f * s, 7.15f * s, 1.7f * s, 1.7f * s);
        }

        // One blade: a swept wedge from the hub, curved like a fan blade.
        static GraphicsPath Blade(float s)
        {
            PointF[] pts = new PointF[]
            {
                new PointF(8.1f * s, 3.1f * s),
                new PointF(10.9f * s, 5.6f * s),
                new PointF(10.1f * s, 7.5f * s),
                new PointF(8.1f * s, 6.6f * s),
            };
            GraphicsPath p = new GraphicsPath();
            p.AddClosedCurve(pts, 0.4f);
            return p;
        }
    }

    // Same entry points the vendor FanSpeedSetting.exe uses.
    static class Dchu
    {
        [DllImport("InsydeDCHU.dll")]
        public static extern int GetDCHU_Data_Integer(int command, ref int data);

        [DllImport("InsydeDCHU.dll")]
        public static extern int GetDCHU_Data_Buffer(int command, byte[] buffer);

        [DllImport("InsydeDCHU.dll")]
        public static extern int SetDCHU_Data(int command, byte[] buffer, int length);
    }

    sealed class Telemetry
    {
        public bool Ok;
        public int FanCount;
        public int CpuRpmRaw, GpuRpmRaw, Gpu2RpmRaw;
        public int CpuDuty, GpuDuty, Gpu2Duty;
        public int CpuTemp, GpuTemp, Gpu2Temp;

        // Package 13 only changes when someone writes a table, so read the fan
        // count once instead of issuing a second EC transaction every tick.
        static int cachedFanCount = -1;

        // Vendor app converts the EC counter to RPM: round(60 / (5.5652e-05 * raw) * 2)
        public static int ToRpm(int raw)
        {
            if (raw <= 0) return 0;
            return (int)Math.Round(2156250.0 / raw, 0);
        }

        public static Telemetry Read()
        {
            Telemetry t = new Telemetry();
            byte[] b = new byte[256];
            int rc = Dchu.GetDCHU_Data_Buffer(12, b);
            if (rc != 12) return t;

            t.CpuRpmRaw = b[3] + (b[2] << 8);
            t.GpuRpmRaw = b[5] + (b[4] << 8);
            t.Gpu2RpmRaw = b[7] + (b[6] << 8);
            t.CpuDuty = b[16];
            t.GpuDuty = b[19];
            t.Gpu2Duty = b[22];
            t.CpuTemp = b[18];
            t.GpuTemp = b[21];
            t.Gpu2Temp = b[24];

            if (cachedFanCount < 0)
            {
                byte[] c = new byte[256];
                if (Dchu.GetDCHU_Data_Buffer(13, c) == 13) cachedFanCount = c[12];
            }

            t.FanCount = cachedFanCount > 0 ? cachedFanCount : 2;
            t.Ok = true;
            return t;
        }

        public int Hottest()
        {
            int h = CpuTemp;
            if (GpuTemp > h) h = GpuTemp;
            if (FanCount > 2 && Gpu2Temp > h) h = Gpu2Temp;
            return h;
        }
    }

    // Package 13: the EC's four-point curve, per fan.
    sealed class FanTable
    {
        public int FanCount;
        public int[] T1 = new int[3];
        public int[] D1 = new int[3];
        public int[] T2 = new int[3];
        public int[] D2 = new int[3];
        public int[] T3 = new int[3];
        public int[] D3 = new int[3];

        public static FanTable Read()
        {
            byte[] b = new byte[256];
            if (Dchu.GetDCHU_Data_Buffer(13, b) != 13) return null;
            FanTable t = new FanTable();
            t.FanCount = b[12];
            int[] bases = new int[] { 16, 24, 32 };
            for (int i = 0; i < 3; i++)
            {
                int p = bases[i];
                t.T1[i] = b[p];
                t.D1[i] = Pct(b[p + 1]);
                t.T2[i] = b[p + 2];
                t.D2[i] = Pct(b[p + 3]);
                t.T3[i] = b[p + 4];
                t.D3[i] = Pct(b[p + 5]);
            }
            return t;
        }

        static int Pct(int raw)
        {
            return (int)Math.Round(raw / 255.0 * 100.0, 0);
        }
    }

    // The curve we enforce in software. The EC's own D1 point is ignored here:
    // it is pinned at 25 % below 40 C and cannot be written, which is exactly
    // why the EC-interpreted curves never went below ~38 % in practice.
    sealed class FanCurve
    {
        public readonly int[] T2 = new int[3];
        public readonly int[] D2 = new int[3];
        public readonly int[] T3 = new int[3];
        public readonly int[] D3 = new int[3];
        public readonly int MinDuty;

        public FanCurve(int[] t2, int[] d2, int[] t3, int[] d3, int minDuty)
        {
            for (int i = 0; i < 3; i++)
            {
                T2[i] = Fans.Clamp(t2[i], 20, 95);
                D2[i] = Fans.Clamp(d2[i], 0, 100);
                T3[i] = Fans.Clamp(t3[i], T2[i] + 1, 99);
                D3[i] = Fans.Clamp(d3[i], 0, 100);
            }
            MinDuty = Fans.Clamp(minDuty, 0, 50);
        }

        // Where the curve reaches its final point (full speed). Custom uses the
        // EC's own 100 C; Quiet brings it earlier so the curve tops out before
        // the guard band would take over, instead of forever under it.
        int fullC = 100;

        public int FullC { get { return fullC; } }

        // Returns a curve that reaches 100 % at `atC` smoothly.
        //
        // The upper part is redistributed inside the space that is left: T2 is
        // kept, and T3 is placed so that the two remaining segments (T2->T3 and
        // T3->full) split the span evenly and their duties climb without a
        // cliff. Squeezing T3 right up against the top (or dropping it) put
        // almost all the rise into a 1-4 C window, which is the same jump the
        // guard band exists to avoid - it just moved it earlier.
        public FanCurve WithTop(int atC)
        {
            int top = Fans.Clamp(atC, 25, 100);
            FanCurve c = new FanCurve(T2, D2, T3, D3, MinDuty);
            c.fullC = top;
            for (int i = 0; i < 3; i++)
            {
                int t2 = c.T2[i];
                if (t2 >= top - 2) continue;   // no room; leave this channel as is

                // Split the remaining span in half: T3 sits midway, so both
                // segments have real width.
                int t3 = t2 + (Math.Max(2, top - t2) / 2);
                if (t3 > top - 1) t3 = top - 1;

                // Duty at T3 comes from the original shape, but capped so that
                // the final half of the span still has room to climb to 100 %.
                // That keeps the two segments comparable in steepness.
                double orig = Eval(i, t3, 40);
                double ceiling = 50.0 + 40.0 * (top == t2 ? 0 : (top - t3) / (double)(top - t2));
                if (orig > ceiling) orig = ceiling;
                if (orig < c.D2[i] + 3) orig = c.D2[i] + 3;   // never dip at T3

                c.T3[i] = t3;
                c.D3[i] = (int)Math.Round(Math.Min(orig, 90));
            }
            return c;
        }

        // The machine's shipped curve, kept for "Reset to shipped" in the editor.
        public static FanCurve Shipped(int minDuty)
        {
            return new FanCurve(new int[] { 65, 66, 66 }, new int[] { 41, 48, 48 },
                                new int[] { 80, 80, 80 }, new int[] { 56, 59, 59 }, minDuty);
        }

        public double Eval(int ch, double temp, int t1)
        {
            if (t1 <= 0) t1 = 40;

            double x0 = t1, y0 = MinDuty;
            double x1 = T2[ch], y1 = D2[ch];
            double x2 = T3[ch], y2 = D3[ch];
            double x3 = fullC, y3 = 100.0;

            if (x1 <= x0) x1 = x0 + 1;
            if (x2 <= x1) x2 = x1 + 1;
            if (x3 <= x2) return y3;

            double r;
            if (temp <= x0) r = y0;
            else if (temp <= x1) r = Lerp(x0, y0, x1, y1, temp);
            else if (temp <= x2) r = Lerp(x1, y1, x2, y2, temp);
            else if (temp <= x3) r = Lerp(x2, y2, x3, y3, temp);
            else r = y3;

            if (r < MinDuty) r = MinDuty;
            if (r > 100) r = 100;
            return r;
        }

        static double Lerp(double x0, double y0, double x1, double y1, double x)
        {
            if (x1 == x0) return y1;
            return y0 + (y1 - y0) * (x - x0) / (x1 - x0);
        }

        // Locked mode: the curve used while the session is locked. Deliberately
        // separate from Custom and written for this machine's job - an idle
        // desktop serving background agents, not a game - so the fans idle at
        // 0 % through the 40s and 50s and only start moving in the low 50s.
        public static FanCurve Locked()
        {
            return new FanCurve(new int[] { 55, 55, 55 }, new int[] { 14, 14, 14 },
                                new int[] { 72, 72, 72 }, new int[] { 60, 60, 60 }, 0);
        }

        public string Describe(FanTable t)
        {
            StringBuilder sb = new StringBuilder();
            string[] names = new string[] { "CPU", "GPU1", "GPU2" };
            for (int i = 0; i < 3; i++)
            {
                if (t != null && i >= t.FanCount) continue;
                int t1 = (t != null && t.T1[i] > 0) ? t.T1[i] : 40;
                if (sb.Length > 0) sb.Append("   ");
                sb.AppendFormat("{0} {1}/{2}% {3}/{4}% {5}/{6}% 100/100%",
                    names[i], t1, MinDuty, T2[i], D2[i], T3[i], D3[i]);
            }
            return sb.ToString();
        }
    }

    static class Fans
    {
        public const int CmdTelemetry = 12;
        public const int CmdTable = 13;
        public const int CmdCustomTable = 14;
        public const int CmdSetDuty = 104;   // 0x68 - one duty byte per channel
        public const int CmdAutoDuty = 105;  // 0x69 - bitmask of channels back to auto
        public const int CmdMode = 121;      // 0x79 - preset mode

        public static int SetDuty(int cpu, int gpu, int gpu2)
        {
            byte[] b = new byte[4];
            b[0] = (byte)Clamp(cpu, 0, 255);
            b[1] = (byte)Clamp(gpu, 0, 255);
            b[2] = (byte)Clamp(gpu2, 0, 255);
            b[3] = 0;
            return Dchu.SetDCHU_Data(CmdSetDuty, b, 4);
        }

        public static int SetAuto()
        {
            return Dchu.SetDCHU_Data(CmdAutoDuty, new byte[] { 0x0F, 0, 0, 0 }, 4);
        }

        public static int SetMode(int mode)
        {
            // byte0 = mode, byte3 = subcommand 1 (SetFanMode)
            return SetSub(CmdMode, 1, mode);
        }

        // Subcommand goes in byte 3, data little-endian in bytes 0..2.
        public static int SetSub(int cmd, int sub, int data)
        {
            byte[] b = new byte[4];
            b[0] = (byte)(data & 0xFF);
            b[1] = (byte)((data >> 8) & 0xFF);
            b[2] = (byte)((data >> 16) & 0xFF);
            b[3] = (byte)sub;
            return Dchu.SetDCHU_Data(cmd, b, 4);
        }

        public static int LoadDefaults()
        {
            return SetSub(CmdMode, 34, 1);   // vendor: WMI13_LoadDefault
        }

        // Builds the 256-byte EC table. The EC's own T1/D1 are left alone; D2/D3
        // come from the curve. This is the fallback the EC enforces if our
        // process dies or the machine resumes, not the accurate control path.
        public static byte[] BuildCurve(FanTable t, FanCurve c)
        {
            byte[] b = new byte[256];
            int[] slot = new int[] { 2, 6, 10 };
            int[] slope = new int[] { 14, 20, 26 };

            for (int i = 0; i < 3; i++)
            {
                if (i >= t.FanCount) continue;

                int t1 = t.T1[i] > 0 ? t.T1[i] : 40;
                int d1 = t.D1[i] > 0 ? t.D1[i] : 25;
                int t2 = c.T2[i], d2 = c.D2[i], t3 = c.T3[i], d3 = c.D3[i];

                b[slot[i]] = (byte)t2;
                b[slot[i] + 1] = (byte)PercentToRaw(d2);
                b[slot[i] + 2] = (byte)t3;
                b[slot[i] + 3] = (byte)PercentToRaw(d3);

                int r12 = Slope(d1, d2, t1, t2);
                int r23 = Slope(d2, d3, t2, t3);
                int r34 = Slope(d3, 100, t3, 100);

                b[slope[i]] = (byte)(r12 >> 8);
                b[slope[i] + 1] = (byte)(r12 & 0xFF);
                b[slope[i] + 2] = (byte)(r23 >> 8);
                b[slope[i] + 3] = (byte)(r23 & 0xFF);
                b[slope[i] + 4] = (byte)(r34 >> 8);
                b[slope[i] + 5] = (byte)(r34 & 0xFF);
            }

            return b;
        }

        // Vendor formula: round((d2-d1)/(t2-t1) * 2.55 * 16)
        static int Slope(int d1, int d2, int t1, int t2)
        {
            if (t2 == t1) return 0;
            int r = (int)Math.Round((d2 - d1) / (double)(t2 - t1) * 2.55 * 16.0, 0);
            return Clamp(r, 0, 65535);
        }

        // Writes the matching table into the EC and selects mode 6. Kept as a
        // fallback only; the accurate control is the direct duty path.
        public static int WriteEcCurve(FanTable t, FanCurve c)
        {
            SetAuto();                        // drop any manual duty first
            int rc = Dchu.SetDCHU_Data(CmdCustomTable, BuildCurve(t, c), 256);
            if (rc != CmdCustomTable) return rc;
            return SetMode(6);                // custom
        }

        public static int Clamp(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        public static int PercentToRaw(int pct)
        {
            return (int)Math.Round(Clamp(pct, 0, 100) * 255.0 / 100.0, 0);
        }

        public static int RawToPercent(int raw)
        {
            return (int)Math.Round(Clamp(raw, 0, 255) / 255.0 * 100.0, 0);
        }
    }

    // Power side of locked mode. The scheme is created once by build.cmd
    // (PowerDuplicateScheme needs admin); this code only writes its values and
    // activates it, which a normal user may do. Everything is best-effort:
    // a missing scheme or a failed write just means locked mode is fans-only.
    //
    // No package (PL1/PL2) limit here on purpose: those settings are not in
    // this machine's processor subgroup at all, so a "watts" box would report
    // success and cap nothing. The clock caps below are what actually applies.
    static class LockedPower
    {
        // Found by exact name at startup: creating a scheme is admin-only and
        // powercfg will not give it a chosen GUID, so the tool creates it with
        // tools\New-PowerPlan.ps1 and the app looks it up here.
        public const string SchemeName = "ClevoFanControl - locked";
        public static readonly Guid SubProcessor = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        public static readonly Guid MaxState = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec");
        public static readonly Guid MinState = new Guid("893dee8e-2bef-41e0-89c6-b55d0929964c");
        public static readonly Guid BoostMode = new Guid("be337238-0d82-4146-a960-4f3749d470c7");
        public static readonly Guid Epp = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");
        public static readonly Guid MaxCores = new Guid("0cc5b647-c1df-4637-891a-dec35c318583");

        static Guid? scheme;
        static bool looked;

        public static Guid? Scheme
        {
            get
            {
                if (!looked)
                {
                    looked = true;
                    scheme = FindByName(SchemeName);
                }
                return scheme;
            }
        }

        // powercfg /L output: "Power Scheme GUID: xxxxxxxx-...  (Name)".
        static Guid? FindByName(string name)
        {
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo("powercfg", "/L");
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.CreateNoWindow = true;
                psi.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
                using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
                {
                    string text = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3000);
                    foreach (string line in text.Split('\n'))
                    {
                        if (line.IndexOf("(" + name + ")", StringComparison.Ordinal) < 0) continue;
                        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
                            line, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
                        Guid g;
                        if (m.Success && Guid.TryParse(m.Value, out g)) return g;
                    }
                }
            }
            catch { }
            return null;
        }

        // True when the hidden plan exists *and* is the one Windows is using,
        // which is the only reliable "the CPU is actually capped" signal.
        public static bool InEffect()
        {
            Guid? active = Active();
            return active.HasValue && Scheme.HasValue && active.Value == Scheme.Value;
        }

        public static Guid? Active()
        {
            IntPtr p;
            if (Native.PowerGetActiveScheme(IntPtr.Zero, out p) != 0 || p == IntPtr.Zero) return null;
            try { return (Guid)Marshal.PtrToStructure(p, typeof(Guid)); }
            finally { Native.LocalFree(p); }
        }

        public static bool Activate(Guid g)
        {
            return Native.PowerSetActiveScheme(IntPtr.Zero, ref g) == 0;
        }

        // Every write takes local copies: C# will not pass a static readonly
        // field by ref, and the copies stop a caller from rewriting the GUIDs.
        static uint WriteAc(Guid setting, uint value)
        {
            Guid s;
            if (!Scheme.HasValue) return 1;
            s = Scheme.Value;
            Guid sub = SubProcessor, set = setting;
            return Native.PowerWriteACValueIndex(IntPtr.Zero, ref s, ref sub, ref set, value);
        }

        static uint WriteDc(Guid setting, uint value)
        {
            Guid s;
            if (!Scheme.HasValue) return 1;
            s = Scheme.Value;
            Guid sub = SubProcessor, set = setting;
            return Native.PowerWriteDCValueIndex(IntPtr.Zero, ref s, ref sub, ref set, value);
        }

        static void WriteBoth(Guid setting, uint value)
        {
            WriteAc(setting, value);
            WriteDc(setting, value);
        }

        // Writes the numbers from Settings into the hidden scheme. Returns the
        // scheme GUID that was active before, so the caller can go back to it.
        public static Guid? Configure(Settings s)
        {
            Guid? previous = Active();
            if (!Scheme.HasValue) return previous;   // fans-only; the caller reports it

            WriteBoth(MaxState, (uint)s.LockedMaxState);
            WriteBoth(MinState, (uint)s.LockedMinState);
            WriteBoth(BoostMode, s.LockedBoostOff ? 0u : 2u);   // 0 disabled, 2 aggressive = firmware default
            WriteBoth(Epp, (uint)s.LockedEpp);
            WriteBoth(MaxCores, (uint)s.LockedMaxCores);
            return previous;
        }

        // True when the scheme exists and accepts writes.
        public static bool Ready(Settings s)
        {
            return WriteAc(MaxState, (uint)s.LockedMaxState) == 0;
        }

        // Called on unlock: back to the plan that was active at lock time.
        public static void Restore(string previousGuid)
        {
            Guid g;
            if (string.IsNullOrEmpty(previousGuid) || !Guid.TryParse(previousGuid, out g)) return;
            if (Scheme.HasValue && g == Scheme.Value) return;
            Activate(g);
        }
    }

    // System-wide CPU busy using GetSystemTimes. Deltas between ticks, smoothed
    // with a short EMA so a single noisy tick does not move the fans.
    sealed class SystemLoad
    {
        ulong lastIdle, lastKernel, lastUser;
        bool have;

        public double Busy;   // 0..1

        public void Sample()
        {
            Native.FileTime idle, kernel, user;
            if (!Native.GetSystemTimes(out idle, out kernel, out user)) return;

            ulong i = idle.Value, k = kernel.Value, u = user.Value;
            if (have)
            {
                ulong di = i - lastIdle, dk = k - lastKernel, du = u - lastUser;
                ulong total = dk + du;
                if (total > 0)
                {
                    double busy = 1.0 - (double)di / (double)total;
                    if (busy < 0) busy = 0;
                    if (busy > 1) busy = 1;
                    Busy = Busy * 0.6 + busy * 0.4;
                }
            }
            lastIdle = i;
            lastKernel = k;
            lastUser = u;
            have = true;
        }
    }

    enum FanMode { Auto, Manual, Custom, Quiet, Smart, Locked }

    sealed class Settings
    {
        public bool AutoStart;
        public FanMode Mode = FanMode.Auto;
        public bool Guard = true;
        public int GuardTemp = 92;
        public bool Restore = true;
        public bool Reapply = true;
        public bool Link = true;
        public int MinDuty = 15;
        // Default curve sits well under the shipped one, which is far more
        // aggressive than the firmware's own 25 % floor. The firmware holds
        // 25 % up to ~79 C, so this starts below that and ramps hard past 85 C.
        public int[] T2 = new int[] { 72, 72, 72 };
        public int[] D2 = new int[] { 28, 28, 28 };
        public int[] T3 = new int[] { 86, 86, 86 };
        public int[] D3 = new int[] { 55, 55, 55 };

        // Locked mode's power side. The fan side is FanCurve.Locked(). These are
        // written into the hidden "ClevoFanControl - locked" scheme; AC and DC
        // are kept identical because the machine can be either when it locks.
        public bool LockedPowerEnabled = true;
        public int LockedMaxState = 25;     // % of max clock - 50 % on this i9 is
                                            // still 1.8 GHz, i.e. above base clock
        public int LockedMinState = 5;
        public bool LockedBoostOff = true;  // PERFBOOSTMODE 0
        public int LockedEpp = 90;          // PERFEPP, 0 = full speed, 100 = max saving
        public int LockedMaxCores = 60;     // CPMAXCORES, % of cores left unparked

        public Settings Clone()
        {
            Settings s = new Settings();
            s.AutoStart = AutoStart;
            s.Mode = Mode;
            s.Guard = Guard;
            s.GuardTemp = GuardTemp;
            s.Restore = Restore;
            s.Reapply = Reapply;
            s.Link = Link;
            s.MinDuty = MinDuty;
            Array.Copy(T2, s.T2, 3);
            Array.Copy(D2, s.D2, 3);
            Array.Copy(T3, s.T3, 3);
            Array.Copy(D3, s.D3, 3);
            s.LockedPowerEnabled = LockedPowerEnabled;
            s.LockedMaxState = LockedMaxState;
            s.LockedMinState = LockedMinState;
            s.LockedBoostOff = LockedBoostOff;
            s.LockedEpp = LockedEpp;
            s.LockedMaxCores = LockedMaxCores;
            return s;
        }

        public FanCurve Custom()
        {
            return new FanCurve(T2, D2, T3, D3, MinDuty);
        }

        // Quiet: its own curve, not a shifted copy of Custom.
        //
        // It used to be "Custom evaluated 3 C earlier", which tied its top end
        // to Custom's. Custom only reaches 100 % at 100 C, but the guard starts
        // ramping at (threshold - GuardBand) = 84 C on the default 92 setting, so
        // the guard always took over first and Quiet never used its own range -
        // it sat around 62 % when the guard fired. Moving Custom's anchors
        // around to reach full earlier then squeezed the whole rise into a
        // 1-4 C window, which just moved the cliff rather than removing it.
        //
        // So Quiet now has its own four points, chosen to sound calm and climb
        // steadily:
        //
        //   T1 40 C / 15 %   no extra fan until the case is warm
        //   T2 62 C / 22 %   still gentle well into the 60s
        //   T3 74 C / 45 %   a real, audible climb through the 70s
        //   full at `top`   100 %, reached before the guard band starts
        //
        // The top tracks the guard threshold, 12 C below it, so at the default
        // 92 C full speed arrives at 80 C with 84 C (band start) still to spare.
        // That means the guard never fires in normal running: its band only
        // catches a runaway that ignores the curve.
        public FanCurve QuietCurve(int guardTemp)
        {
            int top = Math.Max(60, Math.Min(99, guardTemp - 12));
            int[] t2 = new int[3], d2 = new int[3], t3 = new int[3], d3 = new int[3];
            for (int i = 0; i < 3; i++)
            {
                t2[i] = 62;
                d2[i] = 22;
                t3[i] = 74;
                d3[i] = 45;
            }
            return new FanCurve(t2, d2, t3, d3, Math.Max(0, MinDuty - 2)).WithTop(top);
        }

        public FanCurve QuietCurve()
        {
            return QuietCurve(92);   // matches the default guard threshold
        }

        static string Dir
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClevoFanControl");
            }
        }

        static string FilePath
        {
            get { return Path.Combine(Dir, "settings.ini"); }
        }

        public static Settings Load()
        {
            Settings s = new Settings();
            Dictionary<string, string> m = new Dictionary<string, string>();
            try
            {
                if (File.Exists(FilePath))
                {
                    foreach (string line in File.ReadAllLines(FilePath))
                    {
                        string l = line.Trim();
                        if (l.Length == 0 || l.StartsWith("#") || l.StartsWith(";")) continue;
                        int eq = l.IndexOf('=');
                        if (eq <= 0) continue;
                        m[l.Substring(0, eq).Trim()] = l.Substring(eq + 1).Trim();
                    }
                }
            }
            catch { }

            s.AutoStart = Bool(m, "autostart", false);
            s.Guard = Bool(m, "guard", true);
            s.GuardTemp = Int(m, "guardTemp", 92);
            s.Restore = Bool(m, "restore", true);
            s.Reapply = Bool(m, "reapply", true);
            s.Link = Bool(m, "link", true);
            s.MinDuty = Int(m, "minDuty", 15);
            s.LockedPowerEnabled = Bool(m, "lockedPower", true);
            s.LockedMaxState = Int(m, "lockedMaxState", 25);
            s.LockedMinState = Int(m, "lockedMinState", 5);
            s.LockedBoostOff = Bool(m, "lockedBoostOff", true);
            s.LockedEpp = Int(m, "lockedEpp", 90);
            s.LockedMaxCores = Int(m, "lockedMaxCores", 60);

            string modeText = Get(m, "mode");
            FanMode parsed;
            if (modeText != null && Enum.TryParse<FanMode>(modeText, true, out parsed)) s.Mode = parsed;

            for (int i = 0; i < 3; i++)
            {
                s.T2[i] = Int(m, "t2." + i.ToString(CultureInfo.InvariantCulture), s.T2[i]);
                s.D2[i] = Int(m, "d2." + i.ToString(CultureInfo.InvariantCulture), s.D2[i]);
                s.T3[i] = Int(m, "t3." + i.ToString(CultureInfo.InvariantCulture), s.T3[i]);
                s.D3[i] = Int(m, "d3." + i.ToString(CultureInfo.InvariantCulture), s.D3[i]);
            }

            // Never trust the file: build one curve so the clamps run, then copy back.
            FanCurve c = s.Custom();
            Array.Copy(c.T2, s.T2, 3);
            Array.Copy(c.D2, s.D2, 3);
            Array.Copy(c.T3, s.T3, 3);
            Array.Copy(c.D3, s.D3, 3);
            s.MinDuty = c.MinDuty;

            // A saved "locked" is stale: that mode only ever applies while the
            // session is actually locked, and never between runs.
            if (s.Mode == FanMode.Locked) s.Mode = FanMode.Auto;

            s.GuardTemp = Fans.Clamp(s.GuardTemp, 55, 105);
            s.LockedMaxState = Fans.Clamp(s.LockedMaxState, 5, 100);
            s.LockedMinState = Fans.Clamp(s.LockedMinState, 1, 100);
            s.LockedEpp = Fans.Clamp(s.LockedEpp, 0, 100);
            s.LockedMaxCores = Fans.Clamp(s.LockedMaxCores, 10, 100);
            return s;
        }
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# ClevoFanControl settings");
                sb.AppendLine("autostart=" + (AutoStart ? 1 : 0));
                sb.AppendLine("mode=" + Mode.ToString().ToLowerInvariant());
                sb.AppendLine("guard=" + (Guard ? 1 : 0));
                sb.AppendLine("guardTemp=" + GuardTemp.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("restore=" + (Restore ? 1 : 0));
                sb.AppendLine("reapply=" + (Reapply ? 1 : 0));
                sb.AppendLine("link=" + (Link ? 1 : 0));
                sb.AppendLine("minDuty=" + MinDuty.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("lockedPower=" + (LockedPowerEnabled ? 1 : 0));
                sb.AppendLine("lockedMaxState=" + LockedMaxState.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("lockedMinState=" + LockedMinState.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("lockedBoostOff=" + (LockedBoostOff ? 1 : 0));
                sb.AppendLine("lockedEpp=" + LockedEpp.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("lockedMaxCores=" + LockedMaxCores.ToString(CultureInfo.InvariantCulture));
                for (int i = 0; i < 3; i++)
                {
                    string k = i.ToString(CultureInfo.InvariantCulture);
                    sb.AppendLine("t2." + k + "=" + T2[i].ToString(CultureInfo.InvariantCulture));
                    sb.AppendLine("d2." + k + "=" + D2[i].ToString(CultureInfo.InvariantCulture));
                    sb.AppendLine("t3." + k + "=" + T3[i].ToString(CultureInfo.InvariantCulture));
                    sb.AppendLine("d3." + k + "=" + D3[i].ToString(CultureInfo.InvariantCulture));
                }
                File.WriteAllText(FilePath, sb.ToString());
            }
            catch { }
        }

        static string StatePath
        {
            get { return Path.Combine(Dir, "locked-backup.ini"); }
        }

        // Written before the locked scheme is activated and deleted after the
        // original scheme is restored. An interrupted lock (crash, Task Manager
        // kill) leaves this behind, so the next start puts the user's power
        // settings back instead of leaving the machine throttled.
        public static void SaveThrottleBackup(string schemeGuid)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(StatePath, schemeGuid + Environment.NewLine);
            }
            catch { }
        }

        public static string LoadThrottleBackup()
        {
            try
            {
                if (!File.Exists(StatePath)) return null;
                string g = File.ReadAllText(StatePath).Trim();
                return g.Length == 0 ? null : g;
            }
            catch { return null; }
        }

        public static void ClearThrottleBackup()
        {
            try { if (File.Exists(StatePath)) File.Delete(StatePath); }
            catch { }
        }

        static string Get(Dictionary<string, string> m, string key)
        {
            string v;
            return m.TryGetValue(key, out v) ? v : null;
        }

        static int Int(Dictionary<string, string> m, string key, int fallback)
        {
            int v;
            string s = Get(m, key);
            if (s != null && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            return fallback;
        }

        static bool Bool(Dictionary<string, string> m, string key, bool fallback)
        {
            string s = Get(m, key);
            if (s == null) return fallback;
            return s == "1" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
        }
    }

    static class AutoRun
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "ClevoFanControl";

        public static bool Enabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                    return k != null && k.GetValue(RunName) != null;
            }
            catch { return false; }
        }

        public static void Set(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on)
                        k.SetValue(RunName, "\"" + Application.ExecutablePath + "\" --tray", RegistryValueKind.String);
                    else
                        k.DeleteValue(RunName, false);
                }
            }
            catch { }
        }
    }

    static class Program
    {
        static string dllLoadedFrom;
        static bool ownConsole;

        const string MutexName = @"Local\ClevoFanControl.Instance";
        const string ShowEventName = @"Local\ClevoFanControl.Show";

        [STAThread]
        static int Main(string[] args)
        {
            bool startHidden = false;
            List<string> rest = new List<string>();
            foreach (string a in args)
            {
                if (string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase)) startHidden = true;
                else rest.Add(a);
            }

            // Option B: a winexe has no console of its own. Only the CLI path
            // asks for one, and it borrows the calling shell's when there is one.
            if (rest.Count > 0) SetupConsole();

            if (!LoadDriverDll())
            {
                string msg = "Could not load InsydeDCHU.dll. Is Control Center installed?";
                if (rest.Count > 0)
                {
                    Console.Error.WriteLine(msg);
                    PauseOwnConsole();
                }
                else
                {
                    MessageBox.Show(msg, "Clevo Fan Control", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return 2;
            }

            if (rest.Count > 0)
            {
                int rc = RunCli(rest.ToArray());
                PauseOwnConsole();
                return rc;
            }

            bool created;
            using (Mutex mutex = new Mutex(true, MutexName, out created))
            {
                if (!created)
                {
                    // Someone is already running: ask it to show itself and quit.
                    try
                    {
                        using (EventWaitHandle ev = EventWaitHandle.OpenExisting(ShowEventName))
                            ev.Set();
                    }
                    catch { }
                    return 0;
                }

                Native.SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                Settings settings = Settings.Load();
                using (EventWaitHandle show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
                {
                    MainForm form = new MainForm(settings, startHidden, show);
                    Application.Run(form);
                }
                return 0;
            }
        }

        static void SetupConsole()
        {
            if (!Native.AttachConsole(-1))
            {
                if (!Native.AllocConsole()) return;
                ownConsole = true;
            }

            // Console.Out/Error are cached on first use, so attaching alone
            // prints nothing. Rebind the standard streams to the new handles.
            try
            {
                StreamWriter stdout = new StreamWriter(Console.OpenStandardOutput(), Console.OutputEncoding);
                stdout.AutoFlush = true;
                Console.SetOut(stdout);

                StreamWriter stderr = new StreamWriter(Console.OpenStandardError(), Console.OutputEncoding);
                stderr.AutoFlush = true;
                Console.SetError(stderr);
            }
            catch { }
        }

        static void PauseOwnConsole()
        {
            if (!ownConsole) return;
            Console.WriteLine();
            Console.Write("Press Enter to close...");
            Console.ReadLine();
        }

        static bool LoadDriverDll()
        {
            string[] candidates = new string[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "InsydeDCHU.dll"),
                @"C:\Program Files (x86)\ControlCenter\InsydeDCHU.dll",
                @"C:\Program Files\ControlCenter\InsydeDCHU.dll"
            };
            foreach (string p in candidates)
            {
                if (!File.Exists(p)) continue;
                if (Native.LoadLibraryW(p) != IntPtr.Zero)
                {
                    dllLoadedFrom = p;
                    return true;
                }
            }
            return false;
        }

        static int RunCli(string[] args)
        {
            bool did = false;
            int cpu = -1, gpu = -1, gpu2 = -1;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                string v = (i + 1 < args.Length) ? args[i + 1] : null;

                switch (a)
                {
                    case "--status":
                    case "-s":
                        PrintStatus();
                        did = true;
                        break;

                    case "--set":
                    {
                        int pct = ParseInt(v, -1);
                        if (pct < 0) { Console.Error.WriteLine("--set needs 0..100"); return 2; }
                        cpu = gpu = gpu2 = Fans.PercentToRaw(pct);
                        did = true; i++;
                        break;
                    }
                    case "--duty":
                    {
                        int raw = ParseInt(v, -1);
                        if (raw < 0) { Console.Error.WriteLine("--duty needs 0..255"); return 2; }
                        cpu = gpu = gpu2 = Fans.Clamp(raw, 0, 255);
                        did = true; i++;
                        break;
                    }
                    case "--cpu":
                    {
                        int pct = ParseInt(v, -1);
                        if (pct < 0) { Console.Error.WriteLine("--cpu needs 0..100"); return 2; }
                        cpu = Fans.PercentToRaw(pct);
                        did = true; i++;
                        break;
                    }
                    case "--gpu":
                    {
                        int pct = ParseInt(v, -1);
                        if (pct < 0) { Console.Error.WriteLine("--gpu needs 0..100"); return 2; }
                        gpu = Fans.PercentToRaw(pct);
                        did = true; i++;
                        break;
                    }
                    case "--auto":
                        Console.WriteLine("auto -> rc " + Fans.SetAuto());
                        Console.WriteLine("mode 0 -> rc " + Fans.SetMode(0));
                        did = true;
                        break;

                    case "--quiet":
                    {
                        Settings s = Settings.Load();
                        FanCurve c = s.QuietCurve();
                        FanTable t = FanTable.Read();
                        if (t == null) { Console.Error.WriteLine("could not read fan table"); return 2; }
                        Console.WriteLine("quiet -> rc " + Fans.WriteEcCurve(t, c) + "  " + c.Describe(t));
                        did = true;
                        break;
                    }

                    case "--custom":
                    case "--original":
                    {
                        Settings s = Settings.Load();
                        FanCurve c = s.Custom();
                        FanTable t = FanTable.Read();
                        if (t == null) { Console.Error.WriteLine("could not read fan table"); return 2; }
                        Console.WriteLine("custom -> rc " + Fans.WriteEcCurve(t, c) + "  " + c.Describe(t));
                        did = true;
                        break;
                    }

                    case "--defaults":
                        Console.WriteLine("firmware table restored -> rc " + Fans.LoadDefaults());
                        did = true;
                        break;

                    case "--table":
                        PrintTable();
                        did = true;
                        break;

                    case "--max":
                        Console.WriteLine("max  -> rc " + Fans.SetDuty(255, 255, 0));
                        did = true;
                        break;

                    case "--off":
                        Console.WriteLine("off  -> rc " + Fans.SetDuty(0, 0, 0));
                        did = true;
                        break;

                    case "--mode":
                    {
                        int m = ParseInt(v, -1);
                        if (m < 0) { Console.Error.WriteLine("--mode needs a number (0 auto, 1 max, 3 silent, 5 maxq, 6 custom)"); return 2; }
                        Console.WriteLine("mode " + m + " -> rc " + Fans.SetMode(m));
                        did = true; i++;
                        break;
                    }
                    case "--help":
                    case "-h":
                    case "/?":
                        PrintHelp();
                        return 0;
                }
            }

            if (cpu >= 0 || gpu >= 0 || gpu2 >= 0)
            {
                if (cpu < 0) cpu = 255;
                if (gpu < 0) gpu = 255;
                if (gpu2 < 0) gpu2 = 0;
                int rc = Fans.SetDuty(cpu, gpu, gpu2);
                Console.WriteLine("duty cpu=" + cpu + " gpu=" + gpu + " -> rc " + rc);
                did = true;
            }

            if (!did)
            {
                PrintHelp();
                return 2;
            }
            return 0;
        }

        static int ParseInt(string s, int fallback)
        {
            int v;
            if (s != null && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                return v;
            return fallback;
        }

        static void PrintStatus()
        {
            Telemetry t = Telemetry.Read();
            if (!t.Ok) { Console.WriteLine("telemetry read failed"); return; }
            Console.WriteLine("fans=" + t.FanCount);
            Console.WriteLine("cpu  rpm=" + Telemetry.ToRpm(t.CpuRpmRaw) + " raw=" + t.CpuRpmRaw + " duty=" + t.CpuDuty + " (" + Fans.RawToPercent(t.CpuDuty) + "%) temp=" + t.CpuTemp + "C");
            Console.WriteLine("gpu  rpm=" + Telemetry.ToRpm(t.GpuRpmRaw) + " raw=" + t.GpuRpmRaw + " duty=" + t.GpuDuty + " (" + Fans.RawToPercent(t.GpuDuty) + "%) temp=" + t.GpuTemp + "C");
            if (t.FanCount > 2)
                Console.WriteLine("gpu2 rpm=" + Telemetry.ToRpm(t.Gpu2RpmRaw) + " raw=" + t.Gpu2RpmRaw + " duty=" + t.Gpu2Duty + " temp=" + t.Gpu2Temp + "C");
        }

        static void PrintTable()
        {
            FanTable t = FanTable.Read();
            if (t == null) { Console.WriteLine("fan table read failed"); return; }
            string[] names = new string[] { "CPU ", "GPU1", "GPU2" };
            Console.WriteLine("fans=" + t.FanCount);
            for (int i = 0; i < t.FanCount && i < 3; i++)
                Console.WriteLine("{0}  {1}C/{2}%  {3}C/{4}%  {5}C/{6}%  100C/100%",
                    names[i], t.T1[i], t.D1[i], t.T2[i], t.D2[i], t.T3[i], t.D3[i]);
        }

        static void PrintHelp()
        {
            Console.WriteLine("Clevo fan control (X170KM-G)");
            Console.WriteLine("  FanControl.exe                     open the GUI");
            Console.WriteLine("  FanControl.exe --tray              start hidden in the notification area");
            Console.WriteLine("  FanControl.exe --status            read fans once");
            Console.WriteLine("  FanControl.exe --table             read the EC fan curve");
            Console.WriteLine("  FanControl.exe --set 60            both fans to 60%");
            Console.WriteLine("  FanControl.exe --duty 153          both fans to raw duty 153 (0-255)");
            Console.WriteLine("  FanControl.exe --cpu 60 --gpu 80   per-fan percent");
            Console.WriteLine("  FanControl.exe --max | --off       full speed / stopped");
            Console.WriteLine("  FanControl.exe --custom            write the saved custom curve to the EC");
            Console.WriteLine("  FanControl.exe --quiet             write the built-in quiet curve to the EC");
            Console.WriteLine("  FanControl.exe --auto              firmware automatic (EC curve)");
            Console.WriteLine("  FanControl.exe --defaults          load firmware table (replaces current curve)");
            Console.WriteLine("  FanControl.exe --mode 0|1|3|5|6    firmware preset");
            Console.WriteLine();
            Console.WriteLine("The GUI drives duty in software, which reaches far below the EC's own");
            Console.WriteLine("curve floor. The --custom/--quiet switches only write the EC table, so the");
            Console.WriteLine("EC interprets them loosely - open the GUI for exact control.");
        }
    }

    sealed class CurveDialog : Form
    {
        readonly NumericUpDown[] nT2 = new NumericUpDown[3];
        readonly NumericUpDown[] nD2 = new NumericUpDown[3];
        readonly NumericUpDown[] nT3 = new NumericUpDown[3];
        readonly NumericUpDown[] nD3 = new NumericUpDown[3];
        readonly NumericUpDown nMin;
        readonly Label preview;
        readonly int fanCount;

        public FanCurve Result;

        public CurveDialog(FanCurve current, int fanCount)
        {
            this.fanCount = fanCount < 2 ? 2 : fanCount;

            Text = "Custom fan curve";
            ClientSize = new Size(486, 292);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9f);
            Icon = Logo.ToIcon(32);

            Label head = new Label();
            head.Text = "The EC pins the first point at 40 C / 25 % and will not let it be written, so the curve is applied in software from the minimum duty below.";
            head.AutoSize = false;
            head.Size = new Size(456, 32);
            head.ForeColor = Color.DimGray;
            head.Location = new Point(14, 10);
            Controls.Add(head);

            string[] cols = new string[] { "T2 (C)", "D2 (%)", "T3 (C)", "D3 (%)" };
            int[] colX = new int[] { 96, 196, 276, 376 };
            for (int c = 0; c < cols.Length; c++)
            {
                Label l = new Label();
                l.Text = cols[c];
                l.AutoSize = true;
                l.Location = new Point(colX[c], 48);
                Controls.Add(l);
            }

            string[] names = new string[] { "CPU", "GPU1", "GPU2" };
            for (int i = 0; i < 3; i++)
            {
                int y = 70 + i * 32;

                Label l = new Label();
                l.Text = names[i];
                l.AutoSize = true;
                l.Location = new Point(14, y + 4);
                Controls.Add(l);

                nT2[i] = MakeNum(colX[0], y, 20, 95);
                nD2[i] = MakeNum(colX[1], y, 0, 100);
                nT3[i] = MakeNum(colX[2], y, 21, 99);
                nD3[i] = MakeNum(colX[3], y, 0, 100);

                if (i >= fanCount)
                {
                    nT2[i].Enabled = nD2[i].Enabled = nT3[i].Enabled = nD3[i].Enabled = false;
                }
            }

            Label minLabel = new Label();
            minLabel.Text = "Minimum duty (%)";
            minLabel.AutoSize = true;
            minLabel.Location = new Point(14, 172);
            Controls.Add(minLabel);

            nMin = new NumericUpDown();
            nMin.Minimum = 0;
            nMin.Maximum = 50;
            nMin.Location = new Point(150, 169);
            nMin.Size = new Size(64, 23);
            nMin.ValueChanged += delegate { RefreshPreview(); };
            Controls.Add(nMin);

            Label minNote = new Label();
            minNote.Text = "Floor at and below the EC's first point.";
            minNote.AutoSize = true;
            minNote.ForeColor = Color.DimGray;
            minNote.Location = new Point(224, 172);
            Controls.Add(minNote);

            preview = new Label();
            preview.AutoSize = false;
            preview.Size = new Size(456, 20);
            preview.Location = new Point(14, 202);
            preview.ForeColor = Color.DimGray;
            Controls.Add(preview);

            Button reset = new Button();
            reset.Text = "Reset to shipped";
            reset.Location = new Point(14, 236);
            reset.Size = new Size(120, 28);
            reset.Click += delegate { LoadCurve(FanCurve.Shipped(current.MinDuty)); RefreshPreview(); };
            Controls.Add(reset);

            Button ok = new Button();
            ok.Text = "OK";
            ok.Location = new Point(300, 236);
            ok.Size = new Size(78, 28);
            ok.DialogResult = DialogResult.None;
            ok.Click += delegate { if (ValidateCurve()) { Result = ReadCurve(); DialogResult = DialogResult.OK; } };
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.Location = new Point(386, 236);
            cancel.Size = new Size(86, 28);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;

            LoadCurve(current);
            RefreshPreview();
        }

        NumericUpDown MakeNum(int x, int y, int lo, int hi)
        {
            NumericUpDown n = new NumericUpDown();
            n.Minimum = lo;
            n.Maximum = hi;
            n.Location = new Point(x, y);
            n.Size = new Size(64, 23);
            n.ValueChanged += delegate { RefreshPreview(); };
            Controls.Add(n);
            return n;
        }

        void LoadCurve(FanCurve c)
        {
            for (int i = 0; i < 3; i++)
            {
                nT2[i].Value = c.T2[i];
                nD2[i].Value = c.D2[i];
                nT3[i].Value = c.T3[i];
                nD3[i].Value = c.D3[i];
            }
            nMin.Value = c.MinDuty;
        }

        FanCurve ReadCurve()
        {
            int[] t2 = new int[3], d2 = new int[3], t3 = new int[3], d3 = new int[3];
            for (int i = 0; i < 3; i++)
            {
                t2[i] = (int)nT2[i].Value;
                d2[i] = (int)nD2[i].Value;
                t3[i] = (int)nT3[i].Value;
                d3[i] = (int)nD3[i].Value;
            }
            return new FanCurve(t2, d2, t3, d3, (int)nMin.Value);
        }

        bool ValidateCurve()
        {
            for (int i = 0; i < 3; i++)
            {
                if (i >= fanCount) continue;
                if ((int)nT3[i].Value <= (int)nT2[i].Value)
                {
                    MessageBox.Show("T3 must be above T2 (fan " + (i + 1) + ").",
                        "Custom fan curve", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
            }
            return true;
        }

        void RefreshPreview()
        {
            FanCurve c = ReadCurve();
            StringBuilder sb = new StringBuilder();
            sb.Append("CPU: ");
            int[] temps = new int[] { 45, 55, 65, 75, 85 };
            for (int i = 0; i < temps.Length; i++)
            {
                if (i > 0) sb.Append("   ");
                sb.AppendFormat("{0} C -> {1}%", temps[i], (int)Math.Round(c.Eval(0, temps[i], 40)));
            }
            preview.Text = sb.ToString();
        }
    }

    sealed class MainForm : Form
    {
        readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer reapply = new System.Windows.Forms.Timer();

        readonly Settings settings;
        readonly EventWaitHandle showSignal;
        readonly bool startHidden;
        readonly SystemLoad load = new SystemLoad();

        Label lblCpu, lblGpu, lblInfo, lblCpuPct, lblGpuPct, lblMode, lblSmart, lblStatus;
        TrackBar tbCpu, tbGpu;
        NumericUpDown numCpu, numGpu, numGuard;
        CheckBox chkLink, chkGuard, chkRestore, chkReapply, chkAutoStart, chkLocked;
        Button btnApply, btnMax, btnOff, btnManual, btnCustom, btnQuiet, btnSmart, btnAuto, btnEdit, btnLockedSetup;
        Label lblLockedSummary;
        // The always-visible banner: inside the scrolling page, plus a copy that
        // stays pinned below it so the state is visible without scrolling.
        Panel bannerContent, bannerPinned;
        Label bannerContentTitle, bannerContentDetail;
        Label bannerPinnedTitle, bannerPinnedDetail;
        Panel scroll;
        NotifyIcon tray;

        bool syncing;
        bool guardActive;
        bool closingForReal;
        bool allowVisible;
        bool balloonShown;

        FanMode mode = FanMode.Auto;
        Telemetry lastT;
        int fanCount = 2;
        int failCount;
        int tickCount;
        int lastCpu = 255, lastGpu = 255;
        int[] dutyNow = new int[] { -1, -1, -1 };

        readonly int[,] hist = new int[16, 3];
        readonly int[] t1s = new int[] { 40, 40, 40 };
        int histPos, histCount;
        bool signalInstalled;

        // Locked mode. prevMode is what to go back to on unlock; sessionLocked
        // is read from the timer rather than from SessionSwitch alone so a
        // standby that started in a previous session is never resumed.
        FanMode prevMode = FanMode.Auto;
        bool locked;
        bool sessionLocked;
        string throttleBackup;

        public MainForm(Settings s, bool startHidden, EventWaitHandle showSignal)
        {
            this.settings = s;
            this.startHidden = startHidden;
            this.showSignal = showSignal;

            Text = "Clevo Fan Control";
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(580, 360);   // narrow enough for a 720p-class panel
            Font = new Font("Segoe UI", 9f);
            Icon = Logo.ToIcon(32);

            BuildUi();
            FitToScreen();
            ApplySettingsToUi();

            poll.Interval = 1000;
            poll.Tick += delegate { Poll(); };
            // Measured on this EC: re-sending every 3 s held the requested duty
            // only ~4% of the time (the firmware overwrites within ~1 s), 1 s
            // held 83%, 500 ms held 88%. 500 ms is the point where it is
            // reliably ours, without hammering the EC path every tick.
            reapply.Interval = 500;
            reapply.Tick += delegate { Reapply(); };

            FormClosing += OnFormClosing;

            // No Load handler: with --tray the form is never shown, so Load is
            // never raised. Start the controller from the constructor instead.
            SetupTray();
            Poll();                                   // seed lastT
            RecoverInterruptedLock();
            SystemEvents.SessionSwitch += OnSessionSwitch;
            EnterMode(settings.Mode, true);
            settings.Save();                          // make the file exist on first run
            poll.Start();
            reapply.Start();
        }

        // A previous run was killed between activating the locked scheme and
        // restoring it. The fans are not ours to guess at (the EC re-asserts
        // its own curve after that), but a throttled CPU must not survive.
        void RecoverInterruptedLock()
        {
            string backup = Settings.LoadThrottleBackup();
            if (backup == null) return;

            LockedPower.Restore(backup);          // power first: no fans are owed here
            Settings.ClearThrottleBackup();
            lblStatus.Text = "restored the power plan after an interrupted locked mode";
        }

        void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            try
            {
                sessionLocked = (e.Reason == SessionSwitchReason.SessionLock);
                if (e.Reason == SessionSwitchReason.SessionUnlock && locked)
                {
                    // SessionSwitch is delivered on the UI thread's message
                    // loop, so act now instead of posting: an unlock must never
                    // depend on a later BeginInvoke being pumped into a thread
                    // that is about to be busy or blocked.
                    ExitLockedMode();
                }
            }
            catch { }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // Keep the focus ring off the first mode button, which otherwise
            // looks selected while another mode is active.
            ActiveControl = null;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!signalInstalled && showSignal != null)
            {
                signalInstalled = true;
                InstallShowSignal();
            }
        }

        protected override void SetVisibleCore(bool value)
        {
            if (!allowVisible && startHidden)
            {
                if (!IsHandleCreated) CreateHandle();
                value = false;
            }
            base.SetVisibleCore(value);
        }

        // ---------------------------------------------------------------- UI

        // Every control below the header lives on this panel, which scrolls, so
        // nothing is ever unreachable on a small screen. Groups are positioned
        // relative to it; the vertical scrollbar lives on the far edge.
        Panel MakePage()
        {
            Panel p = new Panel();
            p.Name = "page";
            p.AutoScroll = true;
            p.Dock = DockStyle.None;   // positioned by LayoutForWindow, below the header
            p.BackColor = SystemColors.Control;
            Controls.Add(p);
            return p;
        }

        // Group boxes keep their absolute X so the page never scrolls sideways:
        // the vertical scrollbar is only visible once it appears, so a control
        // at X = 647 could otherwise be wider than the panel's client area.
        GroupBox MakeGroupOn(Control parent, string text, int y, int h)
        {
            GroupBox g = new GroupBox();
            g.Text = text;
            g.Location = new Point(12, y);
            g.Size = new Size(606, h);
            parent.Controls.Add(g);
            return g;
        }

        void BuildUi()
        {
            PictureBox mark = new PictureBox();
            mark.Image = Logo.Render(32);
            mark.SizeMode = PictureBoxSizeMode.AutoSize;
            mark.Location = new Point(14, 8);
            mark.BackColor = Color.Transparent;
            Controls.Add(mark);

            Label title = new Label();
            title.Text = "Clevo Fan Control";
            title.Font = new Font("Segoe UI Semibold", 15f);
            title.AutoSize = true;
            title.Location = new Point(54, 10);
            Controls.Add(title);

            Label sub = new Label();
            sub.Text = "X170KM-G  -  drives the EC through the same AcpiBridge path as Control Center";
            sub.ForeColor = Color.DimGray;
            sub.AutoSize = true;
            sub.Location = new Point(56, 42);
            Controls.Add(sub);

            scroll = MakePage();
            scroll.SendToBack();   // header stays above it in z-order
            BuildBanners(scroll);

            GroupBox gStatus = MakeGroupOn(scroll, "Live", 100, 98);
            lblCpu = MakeMono(gStatus, 14, 20);
            lblGpu = MakeMono(gStatus, 14, 44);
            lblInfo = new Label();
            lblInfo.ForeColor = Color.DimGray;
            lblInfo.AutoSize = true;
            lblInfo.Location = new Point(14, 70);
            gStatus.Controls.Add(lblInfo);

            GroupBox gMode = MakeGroupOn(scroll, "Fan mode", 204, 132);
            btnManual = MakeButton(gMode, "Manual", 16, 24, 84);
            btnCustom = MakeButton(gMode, "Custom", 104, 24, 84);
            btnQuiet = MakeButton(gMode, "Quiet", 192, 24, 84);
            btnSmart = MakeButton(gMode, "Smart", 280, 24, 84);
            btnAuto = MakeButton(gMode, "Automatic", 368, 24, 104);
            btnEdit = MakeButton(gMode, "Edit curve...", 478, 24, 124);

            btnManual.Click += delegate { EnterMode(FanMode.Manual, false); };
            btnCustom.Click += delegate { EnterMode(FanMode.Custom, false); };
            btnQuiet.Click += delegate { EnterMode(FanMode.Quiet, false); };
            btnSmart.Click += delegate { EnterMode(FanMode.Smart, false); };
            btnAuto.Click += delegate { EnterMode(FanMode.Auto, false); };
            btnEdit.Click += delegate { EditCurve(); };

            lblMode = new Label();
            lblMode.AutoSize = true;
            lblMode.Location = new Point(16, 62);
            lblMode.Text = "mode: -";
            gMode.Controls.Add(lblMode);

            lblSmart = new Label();
            lblSmart.AutoSize = true;
            lblSmart.ForeColor = Color.DimGray;
            lblSmart.Location = new Point(16, 84);
            lblSmart.Text = "waiting for telemetry...";
            gMode.Controls.Add(lblSmart);

            Label modeHint = new Label();
            modeHint.AutoSize = true;
            modeHint.ForeColor = Color.DimGray;
            modeHint.Location = new Point(16, 106);
            modeHint.Text = "Custom, Quiet and Smart drive the duty directly, so they reach below the EC curve floor.";
            gMode.Controls.Add(modeHint);

            GroupBox gManual = MakeGroupOn(scroll, "Manual duty", 342, 172);
            AddLabel(gManual, "CPU", 16, 26);
            tbCpu = MakeSlider(gManual, 60, 16);
            numCpu = MakeNum(gManual, 402, 24);
            lblCpuPct = MakePct(gManual, 474, 27);
            AddLabel(gManual, "GPU", 16, 78);
            tbGpu = MakeSlider(gManual, 60, 68);
            numGpu = MakeNum(gManual, 402, 76);
            lblGpuPct = MakePct(gManual, 474, 79);

            tbCpu.ValueChanged += delegate
            {
                if (syncing) return;
                numCpu.Value = tbCpu.Value;
                if (chkLink.Checked) { numGpu.Value = tbCpu.Value; tbGpu.Value = tbCpu.Value; }
            };
            tbGpu.ValueChanged += delegate
            {
                if (syncing) return;
                numGpu.Value = tbGpu.Value;
                if (chkLink.Checked) { numCpu.Value = tbGpu.Value; tbCpu.Value = tbGpu.Value; }
            };
            numCpu.ValueChanged += delegate
            {
                syncing = true;
                tbCpu.Value = (int)numCpu.Value;
                syncing = false;
                if (chkLink.Checked) { numGpu.Value = numCpu.Value; tbGpu.Value = (int)numCpu.Value; }
                UpdatePct();
            };
            numGpu.ValueChanged += delegate
            {
                syncing = true;
                tbGpu.Value = (int)numGpu.Value;
                syncing = false;
                if (chkLink.Checked) { numCpu.Value = numGpu.Value; tbCpu.Value = (int)numGpu.Value; }
                UpdatePct();
            };

            chkLink = new CheckBox();
            chkLink.Text = "Link CPU and GPU";
            chkLink.AutoSize = true;
            chkLink.Location = new Point(16, 116);
            gManual.Controls.Add(chkLink);

            btnApply = MakeButton(gManual, "Apply", 16, 140, 80);
            btnMax = MakeButton(gManual, "Full speed", 102, 140, 92);
            btnOff = MakeButton(gManual, "Fans off", 200, 140, 88);
            btnApply.Click += delegate { EnterMode(FanMode.Manual, false); };
            btnMax.Click += delegate { numCpu.Value = 255; numGpu.Value = 255; EnterMode(FanMode.Manual, false); };
            btnOff.Click += delegate { numCpu.Value = 0; numGpu.Value = 0; EnterMode(FanMode.Manual, false); };

            Label manualHint = new Label();
            manualHint.Text = "Duty is the raw PWM value the EC accepts (0-255).";
            manualHint.ForeColor = Color.DimGray;
            manualHint.AutoSize = true;
            manualHint.Location = new Point(300, 144);
            gManual.Controls.Add(manualHint);

            GroupBox gSafety = MakeGroupOn(scroll, "Safety", 520, 104);
            chkGuard = new CheckBox();
            chkGuard.Text = "Temperature guard: full speed above";
            chkGuard.AutoSize = true;
            chkGuard.Location = new Point(16, 22);
            gSafety.Controls.Add(chkGuard);

            numGuard = new NumericUpDown();
            numGuard.Minimum = 55;
            numGuard.Maximum = 105;
            numGuard.Location = new Point(292, 20);
            numGuard.Size = new Size(56, 23);
            gSafety.Controls.Add(numGuard);

            AddLabel(gSafety, "C", 354, 24);

            chkRestore = new CheckBox();
            chkRestore.Text = "Hand fans back to automatic when this program exits";
            chkRestore.AutoSize = true;
            chkRestore.Location = new Point(16, 52);
            gSafety.Controls.Add(chkRestore);

            chkReapply = new CheckBox();
            chkReapply.Text = "Keep re-applying the current duty (every 0.5 s)";
            chkReapply.AutoSize = true;
            chkReapply.Location = new Point(16, 78);
            gSafety.Controls.Add(chkReapply);

            GroupBox gLocked = MakeGroupOn(scroll, "Locked mode", 632, 112);
            chkLocked = new CheckBox();
            chkLocked.Text = "On lock: cap the power and let the fans idle";
            chkLocked.AutoSize = true;
            chkLocked.Location = new Point(16, 22);
            chkLocked.CheckedChanged += delegate
            {
                settings.LockedPowerEnabled = chkLocked.Checked;
                settings.Save();
                UpdateBanner();
                if (locked && !chkLocked.Checked) ExitLockedMode();
            };
            gLocked.Controls.Add(chkLocked);

            btnLockedSetup = MakeButton(gLocked, "Locked mode setup...", 300, 18, 150);
            btnLockedSetup.Click += delegate { ShowLockedSetup(); };
            gLocked.Controls.Add(btnLockedSetup);

            lblLockedSummary = new Label();
            lblLockedSummary.AutoSize = true;
            lblLockedSummary.ForeColor = Color.DimGray;
            lblLockedSummary.Location = new Point(16, 50);
            gLocked.Controls.Add(lblLockedSummary);

            Label lockedHint = new Label();
            lockedHint.Text = "Unlocked at all other times. Edit the fan curve this mode enforces in the setup window.";
            lockedHint.ForeColor = Color.DimGray;
            lockedHint.AutoSize = true;
            lockedHint.Location = new Point(16, 72);
            gLocked.Controls.Add(lockedHint);

            GroupBox gStartup = MakeGroupOn(scroll, "Startup and tray", 752, 112);
            chkAutoStart = new CheckBox();
            chkAutoStart.Text = "Start with Windows, hidden in the notification area";
            chkAutoStart.AutoSize = true;
            chkAutoStart.Location = new Point(16, 22);
            chkAutoStart.CheckedChanged += delegate
            {
                settings.AutoStart = chkAutoStart.Checked;
                AutoRun.Set(chkAutoStart.Checked);
                settings.Save();
            };
            gStartup.Controls.Add(chkAutoStart);

            Label startupHint = new Label();
            startupHint.Text = "Closing this window keeps it running in the notification area; use Exit there to stop it.";
            startupHint.ForeColor = Color.DimGray;
            startupHint.AutoSize = true;
            startupHint.Location = new Point(16, 48);
            gStartup.Controls.Add(startupHint);

            lblStatus = new Label();
            lblStatus.AutoSize = true;
            lblStatus.ForeColor = Color.DimGray;
            lblStatus.Location = new Point(16, 76);
            lblStatus.Text = "starting...";
            gStartup.Controls.Add(lblStatus);
        }

        // The locked-mode banner. Shown inside the page (so it is the first
        // thing in it even after scrolling) and again pinned under the page, so
        // the state is never hidden by scrolling whatever the window size.
        void BuildBanners(Control page)
        {
            bannerContent = MakeBanner(page, 34, false);
            bannerPinned = MakeBanner(this, 34, true);
            bannerContentTitle = BannerTitle(bannerContent);
            bannerContentDetail = BannerDetail(bannerContent);
            bannerPinnedTitle = BannerTitle(bannerPinned);
            bannerPinnedDetail = BannerDetail(bannerPinned);
        }

        Panel MakeBanner(Control parent, int y, bool docked)
        {
            Panel p = new Panel();
            p.Location = new Point(12, y);
            p.Size = new Size(606, 54);
            p.BackColor = SystemColors.Control;
            p.Visible = false;
            p.Cursor = Cursors.Hand;
            p.Click += delegate
            {
                scroll.ScrollControlIntoView(bannerContent);
                if (chkLocked != null) chkLocked.Focus();
            };
            parent.Controls.Add(p);
            return p;
        }

        static Label BannerTitle(Panel p)
        {
            Label l = new Label();
            l.Font = new Font("Segoe UI Semibold", 11f);
            l.AutoSize = true;
            l.Location = new Point(12, 6);
            l.Click += delegate { if (p.Parent != null) p.Parent.Focus(); };
            p.Controls.Add(l);
            return l;
        }

        // Wraps rather than running off the right edge; the panel is resized
        // around this label so the whole sentence is always readable.
        static Label BannerDetail(Panel p)
        {
            Label l = new Label();
            l.AutoSize = false;
            l.Location = new Point(12, 30);
            p.Controls.Add(l);
            return l;
        }

        // Called whenever the state changes, and from the poll tick.
        //   - locked mode running  -> loud red banner
        //   - enabled but not locked, no plan -> amber, it will only quiet fans
        //   - enabled -> quiet green, it is armed
        void UpdateBanner()
        {
            bool armed = settings.LockedPowerEnabled;
            string title, detail;
            Color back, fore;

            if (locked)
            {
                bool capped = LockedPower.InEffect();
                back = Color.FromArgb(176, 0, 32);          // loud red
                fore = Color.White;
                title = "\u25CF LOCKED MODE RUNNING - fans idling";
                if (!armed)
                    detail = "Locked by the session; locked mode is disabled in settings.";
                else if (capped)
                    detail = string.Format("CPU capped to {0} %, no boost, EPP {1} %, {2} % cores idle.",
                        settings.LockedMaxState, settings.LockedEpp, 100 - settings.LockedMaxCores);
                else
                    detail = "FANS are idling but the CPU is NOT capped: the power plan is missing " +
                             "or could not be switched to. Run tools\\New-PowerPlan.ps1 (admin).";
            }
            else if (!armed)
            {
                back = SystemColors.ControlLight;
                fore = SystemColors.ControlDarkDark;
                title = "\u25CB Locked mode is off";
                detail = "Tick \"On lock\" below to cap the power and idle the fans when the session locks.";
            }
            else if (!LockedPower.Scheme.HasValue)
            {
                back = Color.FromArgb(255, 224, 130);       // amber
                fore = Color.FromArgb(64, 44, 0);
                title = "\u26A0 Locked mode is armed - but will only idle the fans";
                detail = "The power plan is missing. Run tools\\New-PowerPlan.ps1 (admin) once to cap the CPU too.";
            }
            else
            {
                back = Color.FromArgb(198, 239, 206);       // green
                fore = Color.FromArgb(6, 64, 24);
                title = "\u2713 Locked mode is armed";
                detail = "On lock: CPU capped to " + settings.LockedMaxState + " %, no boost, fans idle until warm.";
            }

            ApplyBanner(bannerContent, true, title, detail, back, fore);
            ApplyBanner(bannerPinned, false, title, detail, back, fore);

            // The pinned copy is only needed when the page can scroll.
            bannerPinned.Visible = locked || (scroll != null && scroll.VerticalScroll.Visible);
        }

        void ApplyBanner(Panel p, bool contentBanner, string title, string detail, Color back, Color fore)
        {
            p.BackColor = back;
            Label t = contentBanner ? bannerContentTitle : bannerPinnedTitle;
            Label d = contentBanner ? bannerContentDetail : bannerPinnedDetail;
            t.Text = title;
            t.ForeColor = fore;
            d.Text = detail;
            d.ForeColor = fore;

            int inner = p.Width - 24;
            if (inner < 200) inner = 200;
            d.Width = inner;
            d.Height = TextRenderer.MeasureText(detail, d.Font,
                new Size(inner, 1000), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height + 2;
            t.Width = inner;

            // Grow the bar to fit the wrapped sentence. PinnedHeight uses the
            // matching constant below, so keep them in step.
            int needed = d.Top + d.Height + 8;
            if (p.Height != needed) p.Height = needed;
            p.Visible = true;
        }

        // Room the pinned banner needs, measured from the control so a
        // wrapped sentence is never cut off by the page sitting on top of it.
        int PinnedHeight
        {
            get { return bannerPinned != null && bannerPinned.Visible ? bannerPinned.Height + 6 : 0; }
        }

        // The page is a fixed design (640 px wide, ~900 tall). On a short
        // screen it scrolls instead of running off the bottom edge. The page
        // starts below the header so the banner never crowds the subtitle.
        void LayoutForWindow()
        {
            if (scroll == null) return;
            const int pageTop = 66;
            int h = ClientSize.Height - pageTop - PinnedHeight;
            if (h < 80) h = 80;
            scroll.Location = new Point(0, pageTop);
            scroll.Size = new Size(ClientSize.Width, h);
            scroll.AutoScrollMinSize = new Size(640, PageHeight);

            if (bannerPinned != null)
            {
                bannerPinned.Location = new Point(12, scroll.Bottom + 6);
                bannerPinned.Width = ClientSize.Width - 24;
            }
        }

        // Design height: banner (34) + gap + groups down to the last one
        // (752 + 112) + a little room.
        const int PageHeight = 900;

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (scroll != null) LayoutForWindow();
        }

        // Opens tall enough to show the whole page where the screen allows it,
        // but never taller than the screen's working area - that is what made
        // the bottom groups unreachable before (a fixed 736 px window).
        void FitToScreen()
        {
            const int workArea = 0x0030;   // SPI_GETWORKAREA
            Native.Rect r = new Native.Rect();
            Native.SystemParametersInfo(workArea, 0, out r, 0);
            int availW = r.Right - r.Left;
            int availH = r.Bottom - r.Top;
            if (availW <= 0) availW = Native.GetSystemMetrics(0);
            if (availH <= 0) availH = Native.GetSystemMetrics(1);

            // header 66 + page design height + pinned banner 60.
            int wantedH = 66 + PageHeight + 60;
            int w = Math.Min(640, availW - 40);
            int h = Math.Min(wantedH, availH - 40);
            if (w < MinimumSize.Width) w = MinimumSize.Width;
            if (h < MinimumSize.Height) h = MinimumSize.Height;

            ClientSize = new Size(w, h);
            LayoutForWindow();
        }

        static Label MakeMono(Control parent, int x, int y)
        {
            Label l = new Label();
            l.Font = new Font("Consolas", 10f);
            l.AutoSize = true;
            l.Location = new Point(x, y);
            parent.Controls.Add(l);
            return l;
        }

        static Label MakePct(Control parent, int x, int y)
        {
            Label l = new Label();
            l.AutoSize = true;
            l.Location = new Point(x, y);
            l.Text = "0%";
            parent.Controls.Add(l);
            return l;
        }

        static void AddLabel(Control parent, string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Location = new Point(x, y);
            parent.Controls.Add(l);
        }

        static NumericUpDown MakeNum(Control parent, int x, int y)
        {
            NumericUpDown n = new NumericUpDown();
            n.Minimum = 0;
            n.Maximum = 255;
            n.Location = new Point(x, y);
            n.Size = new Size(62, 23);
            parent.Controls.Add(n);
            return n;
        }

        static TrackBar MakeSlider(Control parent, int x, int y)
        {
            TrackBar t = new TrackBar();
            t.Minimum = 0;
            t.Maximum = 255;
            t.TickFrequency = 32;
            t.LargeChange = 16;
            t.SmallChange = 1;
            t.Location = new Point(x, y);
            t.Size = new Size(330, 45);
            parent.Controls.Add(t);
            return t;
        }

        static Button MakeButton(Control parent, string text, int x, int y, int w)
        {
            Button b = new Button();
            b.Text = text;
            b.Location = new Point(x, y);
            b.Size = new Size(w, 28);
            parent.Controls.Add(b);
            return b;
        }

        void ApplySettingsToUi()
        {
            chkGuard.Checked = settings.Guard;
            numGuard.Value = Fans.Clamp(settings.GuardTemp, (int)numGuard.Minimum, (int)numGuard.Maximum);
            chkRestore.Checked = settings.Restore;
            chkReapply.Checked = settings.Reapply;
            chkLink.Checked = settings.Link;
            chkLocked.Checked = settings.LockedPowerEnabled;
            chkAutoStart.Checked = AutoRun.Enabled();
            settings.AutoStart = chkAutoStart.Checked;
            // Re-write the Run entry so a moved exe does not leave a dead path.
            if (settings.AutoStart) AutoRun.Set(true);
            UpdatePct();
            UpdateLockedSummary();
            UpdateBanner();
        }

        // One line that says what locked mode will actually do, so the numbers
        // never live only inside the setup dialog.
        void UpdateLockedSummary()
        {
            if (lblLockedSummary == null) return;
            if (!settings.LockedPowerEnabled)
            {
                lblLockedSummary.Text = "disabled - locking changes nothing";
                return;
            }
            string power = string.Format("CPU max {0} %{1}, EPP {2} %, {3} % cores",
                settings.LockedMaxState,
                settings.LockedBoostOff ? ", no boost" : "",
                settings.LockedEpp,
                settings.LockedMaxCores);

            FanCurve c = FanCurve.Locked();
            lblLockedSummary.Text = string.Format("fans 0 % below {0} C, {1} % at {2} C, full by {3} C   -   {4}",
                c.T2[0] - 2, c.D2[0], c.T2[0], c.T3[0], power);
        }

        void UpdatePct()
        {
            lblCpuPct.Text = Fans.RawToPercent((int)numCpu.Value) + "%";
            lblGpuPct.Text = Fans.RawToPercent((int)numGpu.Value) + "%";
        }

        // -------------------------------------------------------------- tray

        void SetupTray()
        {
            tray = new NotifyIcon();
            tray.Icon = Logo.ToIcon(16);
            tray.Text = "Clevo Fan Control";
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowFromTray(); };

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Open", null, delegate { ShowFromTray(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Automatic", null, delegate { EnterMode(FanMode.Auto, false); });
            menu.Items.Add("Custom", null, delegate { EnterMode(FanMode.Custom, false); });
            menu.Items.Add("Quiet", null, delegate { EnterMode(FanMode.Quiet, false); });
            menu.Items.Add("Smart", null, delegate { EnterMode(FanMode.Smart, false); });
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem lockedItem = new ToolStripMenuItem("Quiet standby while locked");
            lockedItem.Checked = settings.LockedPowerEnabled;
            lockedItem.Click += delegate
            {
                lockedItem.Checked = !lockedItem.Checked;
                settings.LockedPowerEnabled = lockedItem.Checked;
                settings.Save();
                if (chkLocked != null) chkLocked.Checked = lockedItem.Checked;
                UpdateLockedSummary();
                UpdateBanner();
                if (!settings.LockedPowerEnabled && locked) ExitLockedMode();
            };
            // Keep the tick in step when it is changed from the window instead.
            chkLocked.CheckedChanged += delegate
            {
                lockedItem.Checked = settings.LockedPowerEnabled;
                lockedItem.Text = settings.LockedPowerEnabled
                    ? "Quiet standby while locked  (armed)"
                    : "Quiet standby while locked";
            };
            menu.Items.Add(lockedItem);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { ExitApp(); });
            tray.ContextMenuStrip = menu;
        }

        void InstallShowSignal()
        {
            ThreadPool.RegisterWaitForSingleObject(showSignal,
                delegate { BeginInvoke((MethodInvoker)ShowFromTray); },
                null, Timeout.Infinite, false);
        }

        void ShowFromTray()
        {
            allowVisible = true;
            ShowInTaskbar = true;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        void ExitApp()
        {
            closingForReal = true;
            Close();
        }

        void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            // A real shutdown or Task Manager kill must go through: hiding to the
            // tray would block it, and leaving a duty set would outlive us.
            bool systemClosing = e.CloseReason == CloseReason.WindowsShutDown
                              || e.CloseReason == CloseReason.TaskManagerClosing;

            if (!closingForReal && !systemClosing)
            {
                e.Cancel = true;
                Hide();
                ShowInTaskbar = false;
                if (!balloonShown && tray != null)
                {
                    balloonShown = true;
                    tray.ShowBalloonTip(4000, "Clevo Fan Control",
                        "Still running in the notification area. Use Exit there to stop it.",
                        ToolTipIcon.Info);
                }
                return;
            }

            poll.Stop();
            reapply.Stop();
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            if (locked) ExitLockedMode();
            SaveSettings();
            if (chkRestore.Checked)
            {
                Fans.SetAuto();
                Fans.SetMode(0);
            }
            if (tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
                tray = null;
            }
        }

        void SaveSettings()
        {
            // Locked mode is restored from whatever session it started in, so
            // never persist it as the user's chosen mode.
            if (mode != FanMode.Locked) settings.Mode = mode;
            settings.Guard = chkGuard.Checked;
            settings.GuardTemp = (int)numGuard.Value;
            settings.Restore = chkRestore.Checked;
            settings.Reapply = chkReapply.Checked;
            settings.Link = chkLink.Checked;
            settings.Save();
        }

        // ------------------------------------------------------------ control

        void EnterMode(FanMode m, bool restoring)
        {
            mode = m;
            guardActive = false;
            dutyNow[0] = dutyNow[1] = dutyNow[2] = -1;
            UpdateModeUi();

            switch (m)
            {
                case FanMode.Auto:
                    Fans.SetAuto();
                    Fans.SetMode(0);
                    lblStatus.Text = "automatic: firmware owns the fans";
                    break;

                case FanMode.Manual:
                    ApplyManual();
                    break;

                case FanMode.Custom:
                    ApplyCurveMode(settings.Custom(), "custom");
                    break;

                case FanMode.Quiet:
                    ApplyCurveMode(settings.QuietCurve((int)numGuard.Value), "quiet");
                    break;

                case FanMode.Smart:
                    ApplyCurveMode(settings.Custom(), "smart");
                    break;

                case FanMode.Locked:
                    ApplyCurveMode(FanCurve.Locked(), "locked");
                    break;
            }

            // Unlock: own the fans again *before* the CPU gets its clocks and
            // boost back, so there is never a moment of full-power with the
            // fans idle. Reached when the previous mode was Locked - which
            // only happens if a lock and an unlock raced - or via EnterMode.
            if (locked && m != FanMode.Locked)
            {
                LockedPower.Restore(throttleBackup);
                Settings.ClearThrottleBackup();
                throttleBackup = null;
                locked = false;
                lblStatus.Text = "unlocked - back to " + m.ToString().ToLowerInvariant()
                    + " and the previous power plan";
            }

            if (!restoring && m != FanMode.Locked)
            {
                settings.Mode = m;
                settings.Save();
            }
        }

        void ApplyCurveMode(FanCurve c, string name)
        {
            FanTable t = FanTable.Read();
            int rc = -1;
            if (t != null)
            {
                fanCount = t.FanCount;
                for (int i = 0; i < 3; i++)
                    if (t.T1[i] > 0) t1s[i] = t.T1[i];
                rc = Fans.WriteEcCurve(t, c);   // fallback if this process dies or the machine resumes
            }

            lblStatus.Text = name + " curve: " + c.Describe(t) + "   (EC fallback rc " + rc + ")";

            // Apply the software target straight away rather than waiting a tick.
            if (lastT != null && lastT.Ok) ControlTick(lastT);
            MarkSettling();
        }

        void ApplyManual()
        {
            int cpu = (int)numCpu.Value;
            int gpu = (int)numGpu.Value;
            int rc = Fans.SetDuty(cpu, gpu, fanCount > 2 ? gpu : 0);
            lastCpu = cpu;
            lastGpu = gpu;
            dutyNow[0] = cpu;
            dutyNow[1] = gpu;
            dutyNow[2] = fanCount > 2 ? gpu : 0;
            lblStatus.Text = "manual duty " + cpu + "/" + gpu + "  (rc " + rc + ")";
            MarkSettling();
        }

        // The EC accepts a duty write and then the firmware reclaims the fans
        // within a second or two, so a write is only real if it is re-sent.
        // These track whether the fans are actually doing what we asked.
        int verifyMisses;
        int settleUntilTick;
        bool VerifyDuty(Telemetry t)
        {
            if (t == null || !t.Ok) return true;          // cannot judge
            if (mode == FanMode.Auto) return true;        // firmware owns them
            if (mode == FanMode.Manual && !chkReapply.Checked) return true;
            if (dutyNow[0] < 0) return true;              // nothing commanded yet
            if (tickCount < settleUntilTick) return true;  // firmware ramp-down is normal

            // "Actually there" = within a few raw steps, allowing the EC's own
            // rounding. The EC reports the *effective* duty, so a discarded
            // write shows up as our value vanishing from the readback.
            int wantCpu = dutyNow[0];
            int wantGpu = dutyNow[1];
            int tol = 4;   // EC rounds to raw steps; anything beyond this is a real miss
            bool cpuOk = Math.Abs(t.CpuDuty - wantCpu) <= tol;
            bool gpuOk = fanCount <= 1 || Math.Abs(t.GpuDuty - wantGpu) <= tol;

            if (cpuOk && gpuOk)
            {
                verifyMisses = 0;
                return true;
            }
            verifyMisses++;
            return false;
        }

        // Re-send the duty we believe is correct. Called more often than the
        // control tick, because the firmware reverts between ticks otherwise.
        void AssertDuty()
        {
            if (dutyNow[0] < 0) return;
            Fans.SetDuty(dutyNow[0], dutyNow[1], fanCount > 2 ? dutyNow[2] : 0);
        }

        void Reapply()
        {
            // Automatic means firmware owns the fans: never write duty there.
            if (mode == FanMode.Auto) return;

            // The guard used to return here, which meant nothing was re-sent
            // while it was active - so the EC dropped back to its own duty and
            // the fans were never actually at full speed when the guard thought
            // they were. Always hold whatever duty we last decided on.
            if (guardActive)
            {
                AssertDuty();
                return;
            }

            if (mode == FanMode.Manual)
            {
                if (!chkReapply.Checked) return;
                Fans.SetDuty(lastCpu, lastGpu, fanCount > 2 ? lastGpu : 0);
                return;
            }

            if (mode == FanMode.Custom || mode == FanMode.Quiet || mode == FanMode.Smart || mode == FanMode.Locked)
            {
                if (dutyNow[0] < 0) return;
                Fans.SetDuty(dutyNow[0], dutyNow[1], fanCount > 2 ? dutyNow[2] : 0);
            }
        }

        // ------------------------------------------------------ locked mode

        void EnterLockedMode()
        {
            if (locked) return;
            locked = true;
            prevMode = mode;

            if (settings.LockedPowerEnabled)
            {
                // Record where to go back to before anything is throttled.
                Guid? previous = LockedPower.Configure(settings);
                if (previous.HasValue)
                {
                    throttleBackup = previous.Value.ToString();
                    Settings.SaveThrottleBackup(throttleBackup);
                }
                if (!LockedPower.Ready(settings) || !LockedPower.Scheme.HasValue)
                {
                    lblStatus.Text = "locked: fans idle, but the power plan is missing - "
                        + "run tools\\New-PowerPlan.ps1 once (admin)";
                }
                else if (!LockedPower.Activate(LockedPower.Scheme.Value))
                {
                    lblStatus.Text = "locked: fans idle, but the power plan could not be switched to";
                }
            }

            EnterMode(FanMode.Locked, true);
            if (!settings.LockedPowerEnabled)
                lblStatus.Text = "locked: fans idle, power plan untouched (disabled)";
            UpdateBanner();
        }

        void ExitLockedMode()
        {
            if (!locked) return;
            // EnterMode sees `locked` still true and restores the power plan
            // itself, after the fans are back. Do not clear the flag here.
            FanMode back = prevMode == FanMode.Locked ? FanMode.Auto : prevMode;
            EnterMode(back, true);
            locked = false;
            UpdateBanner();
        }

        void CheckLockState()
        {
            // SessionSwitch is the trigger; the timer is the safety net if it
            // was missed (a standby from an earlier session, a replay miss).
            bool shouldLock = sessionLocked && settings.LockedPowerEnabled;
            if (shouldLock == locked) return;
            if (shouldLock) EnterLockedMode();
            else if (!sessionLocked) ExitLockedMode();
        }

        void Poll()
        {
            // Before anything that can return early: a lost EC must not also
            // leave the machine throttled with no way back on unlock.
            CheckLockState();
            UpdateBanner();   // also catches the plan being switched away from

            Telemetry t = Telemetry.Read();
            load.Sample();

            if (!t.Ok)
            {
                failCount++;
                lblCpu.Text = "CPU  --";
                lblGpu.Text = "GPU  --";
                lblInfo.Text = "telemetry read failed (" + failCount + ")";
                if (failCount >= 3 && mode != FanMode.Auto)
                {
                    // Fail safe: never leave a low duty behind because the EC
                    // stopped answering.
                    Fans.SetAuto();
                    Fans.SetMode(0);
                    EnterModeSilent(FanMode.Auto);
                    lblStatus.Text = "telemetry lost - fans handed back to automatic";
                }
                return;
            }

            failCount = 0;
            lastT = t;
            fanCount = t.FanCount;

            lblCpu.Text = string.Format("CPU  {0,5} RPM   duty {1,3} ({2,3}%)   {3} C",
                Telemetry.ToRpm(t.CpuRpmRaw), t.CpuDuty, Fans.RawToPercent(t.CpuDuty), t.CpuTemp);
            lblGpu.Text = string.Format("GPU  {0,5} RPM   duty {1,3} ({2,3}%)   {3} C",
                Telemetry.ToRpm(t.GpuRpmRaw), t.GpuDuty, Fans.RawToPercent(t.GpuDuty), t.GpuTemp);
            lblInfo.Text = string.Format("fans detected: {0}   -   mode: {1}{2}{3}",
                fanCount, mode.ToString().ToLowerInvariant(),
                guardActive ? "   -   GUARD ACTIVE" : "",
                verifyMisses > 1 && mode != FanMode.Auto ? "   -   re-asserting duty" : "");

            if (mode == FanMode.Auto)
                lblSmart.Text = "firmware owns the fans (EC curve)";
            else if (mode == FanMode.Manual)
                lblSmart.Text = string.Format("manual duty {0}% / {1}%",
                    Fans.RawToPercent(lastCpu), Fans.RawToPercent(lastGpu));
            else if (mode == FanMode.Locked)
                lblSmart.Text = "locked: power capped, fans idle until needed";

            hist[histPos, 0] = t.CpuTemp;
            hist[histPos, 1] = t.GpuTemp;
            hist[histPos, 2] = t.Gpu2Temp;
            histPos = (histPos + 1) % 16;
            if (histCount < 16) histCount++;

            if (tray != null)
            {
                string tip = string.Format("CPU {0} C   GPU {1} C", t.CpuTemp, t.GpuTemp);
                if (tip.Length > 63) tip = tip.Substring(0, 63);
                tray.Text = tip;
            }

            ApplyGuard(t);

            tickCount++;
            if (tickCount % 2 == 0) ControlTick(t);   // 2 s: thermal mass makes 1 s pointless

            // The EC discards lone writes, so check that what we commanded is
            // still what the fans are doing and re-assert hard if it vanished.
            // Only complain once the grace period after a mode change is over,
            // because the firmware ramps itself down for a few seconds there.
            if (!VerifyDuty(t) && verifyMisses > 1)
            {
                AssertDuty();
                if (guardActive)
                    lblStatus.Text = "guard: duty was being overridden - re-asserted full speed";
            }
        }

        void EnterModeSilent(FanMode m)
        {
            mode = m;
            guardActive = false;
            dutyNow[0] = dutyNow[1] = dutyNow[2] = -1;
            UpdateModeUi();
        }

        // The guard is the last line of defence, but switching straight to
        // 100 % the instant the threshold is touched made every curve jump
        // from wherever it was (Quiet sat near 65 %) to full speed - which
        // reads as the fans not scaling nicely, then suddenly roaring.
        //
        // Instead, the last GuardBand degrees before the threshold ramp from
        // the curve's own duty up to full speed: GuardLift returns 0 there and
        // 1 at the threshold. The hard latch stays for failure safety (if the
        // fans are not moving at all by the threshold, they are forced on).
        const int GuardBand = 8;

        // After any mode change the EC takes a few seconds to ramp to what was
        // asked (it starts high, then settles). Treat that as normal rather
        // than as the EC ignoring us.
        const int SettleTicks = 5;

        void MarkSettling()
        {
            settleUntilTick = tickCount + SettleTicks;
            verifyMisses = 0;
        }

        double GuardLift(int hottest)
        {
            int guard = (int)numGuard.Value;
            if (!chkGuard.Checked) return 0;
            int start = guard - GuardBand;
            if (hottest <= start) return 0;
            double x = (hottest - start) / (double)GuardBand;
            return x > 1 ? 1 : x;
        }

        void ApplyGuard(Telemetry t)
        {
            if (mode == FanMode.Auto || !chkGuard.Checked)
            {
                guardActive = false;
                return;
            }

            int guard = (int)numGuard.Value;
            int hottest = t.Hottest();

            if (!guardActive && hottest >= guard)
            {
                guardActive = true;
                dutyNow[0] = dutyNow[1] = dutyNow[2] = 255;
                Fans.SetDuty(255, 255, fanCount > 2 ? 255 : 0);
                settleUntilTick = tickCount + SettleTicks;   // let the EC spin up
                verifyMisses = 0;
                lblStatus.Text = "guard: " + hottest + " C reached " + guard + " C - full speed";
            }
            else if (guardActive && hottest <= guard - 6)
            {
                guardActive = false;
                dutyNow[0] = dutyNow[1] = dutyNow[2] = -1;   // let the curve take over at once
                lblStatus.Text = "guard released";
            }
            else if (!guardActive && hottest >= guard - GuardBand)
            {
                // Inside the band but under the threshold: the curve still
                // runs, ControlTick blends it upward instead of cutting off.
                lblStatus.Text = "approaching " + guard + " C - ramping up";
            }
        }

        void ControlTick(Telemetry t)
        {
            if (mode != FanMode.Custom && mode != FanMode.Quiet && mode != FanMode.Smart && mode != FanMode.Locked) return;
            if (guardActive) return;
            if (t == null || !t.Ok) return;

            FanCurve c;
            if (mode == FanMode.Locked) c = FanCurve.Locked();
            else if (mode == FanMode.Quiet) c = settings.QuietCurve((int)numGuard.Value);
            else c = settings.Custom();

            double loadBoost = 0;
            if (mode == FanMode.Smart)
            {
                double raw = (load.Busy - 0.30) / 0.70;
                if (raw < 0) raw = 0;
                if (raw > 1) raw = 1;
                loadBoost = raw * 4.0;   // system load leads the sensor by seconds
            }

            int[] temps = new int[] { t.CpuTemp, t.GpuTemp, t.Gpu2Temp };
            bool write = false;

            // The guard band uses the hottest sensor, not the channel's own:
            // the fans are one cooler, so a hot GPU should pull both up.
            int hottest = t.Hottest();
            double lift = GuardLift(hottest);
            int hotWant = (int)Math.Round(Math.Max(0, Math.Min(1, lift)) * 100.0);

            for (int ch = 0; ch < 3; ch++)
            {
                if (ch >= fanCount) { dutyNow[ch] = 0; continue; }

                int temp = temps[ch];
                if (temp <= 0) temp = t.Hottest();

                double boost = 0;
                if (mode == FanMode.Smart)
                {
                    double trend = Trend(ch);
                    if (trend < 0) trend = 0;
                    if (trend > 1.5) trend = 1.5;
                    boost = trend * 6.0 + loadBoost;   // pre-spin before the sensor shows the climb
                }

                int t1 = t1s[ch];
                int want = (int)Math.Round(c.Eval(ch, temp + boost, t1));
                // Approaching the guard, the demand rises smoothly to full speed.
                if (want < hotWant) want = hotWant;
                want = Fans.Clamp(Fans.PercentToRaw(want), 0, 255);

                int prev = dutyNow[ch];
                int next;
                if (prev < 0)
                {
                    next = want;
                }
                else
                {
                    int diff = want - prev;
                    if (Math.Abs(diff) < 3)
                    {
                        next = prev;   // deadband: no EC traffic for small moves
                    }
                    else
                    {
                        int step = diff > 0 ? 12 : 6;   // spin up faster than we spin down
                        next = prev + Math.Sign(diff) * Math.Min(step, Math.Abs(diff));
                    }
                }

                if (next != prev)
                {
                    dutyNow[ch] = next;
                    write = true;
                }
            }

            if (write)
                Fans.SetDuty(dutyNow[0], dutyNow[1], fanCount > 2 ? dutyNow[2] : 0);

            if (mode == FanMode.Smart)
            {
                lblSmart.Text = string.Format(
                    "load {0}%   trend CPU {1:+0.0;-0.0;0.0} C/s   duty {2}% / {3}%",
                    (int)Math.Round(load.Busy * 100),
                    Trend(0),
                    Fans.RawToPercent(Math.Max(0, dutyNow[0])),
                    Fans.RawToPercent(Math.Max(0, dutyNow[1])));
            }
            else
            {
                lblSmart.Text = string.Format("duty {0}% / {1}%",
                    Fans.RawToPercent(Math.Max(0, dutyNow[0])),
                    Fans.RawToPercent(Math.Max(0, dutyNow[1])));
            }
        }

        double Trend(int ch)
        {
            if (histCount < 4) return 0;
            int span = Math.Min(15, histCount - 1);
            int newest = (histPos - 1 + 16) % 16;
            int oldest = (newest - span + 16) % 16;
            return (hist[newest, ch] - hist[oldest, ch]) / (double)span;
        }

        void UpdateModeUi()
        {
            Button[] buttons = new Button[] { btnManual, btnCustom, btnQuiet, btnSmart, btnAuto };
            FanMode[] modes = new FanMode[] { FanMode.Manual, FanMode.Custom, FanMode.Quiet, FanMode.Smart, FanMode.Auto };
            for (int i = 0; i < buttons.Length; i++)
            {
                bool active = modes[i] == mode;
                buttons[i].Font = active ? new Font("Segoe UI Semibold", 9f) : new Font("Segoe UI", 9f);
            }
            lblMode.Text = "mode: " + mode.ToString().ToLowerInvariant()
                + (mode == FanMode.Locked ? "   (fans idle, power capped)" : "");
        }

        void EditCurve()
        {
            using (CurveDialog d = new CurveDialog(settings.Custom(), fanCount))
            {
                if (d.ShowDialog(this) != DialogResult.OK || d.Result == null) return;

                Array.Copy(d.Result.T2, settings.T2, 3);
                Array.Copy(d.Result.D2, settings.D2, 3);
                Array.Copy(d.Result.T3, settings.T3, 3);
                Array.Copy(d.Result.D3, settings.D3, 3);
                settings.MinDuty = d.Result.MinDuty;
                settings.Save();

                // If the active mode uses this curve, pick the change up now.
                if (mode == FanMode.Custom || mode == FanMode.Smart)
                {
                    dutyNow[0] = dutyNow[1] = dutyNow[2] = -1;
                    if (lastT != null && lastT.Ok) ControlTick(lastT);
                }
                lblStatus.Text = "custom curve saved: minimum " + settings.MinDuty + "%";
            }
        }

        // A separate window, like Edit curve, so locked mode's settings never
        // sit among the normal ones. Every control maps 1:1 to a Settings field.
        void ShowLockedSetup()
        {
            using (LockedDialog d = new LockedDialog(settings))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;

                settings.LockedPowerEnabled = d.Enabled;
                settings.LockedMaxState = d.MaxState;
                settings.LockedMinState = d.MinState;
                settings.LockedBoostOff = d.BoostOff;
                settings.LockedEpp = d.Epp;
                settings.LockedMaxCores = d.MaxCores;
                settings.Save();

                chkLocked.Checked = settings.LockedPowerEnabled;
                UpdateLockedSummary();
                lblStatus.Text = "locked mode setup saved";

                // Re-entering makes a running lock pick the new numbers up.
                if (locked)
                {
                    FanMode back = prevMode;
                    ExitLockedMode();
                    if (settings.LockedPowerEnabled)
                    {
                        prevMode = back;
                        EnterLockedMode();
                    }
                }
            }
        }
    }

    // Settings window for locked mode. Deliberately a separate dialog from the
    // main window's curve editor: these numbers only ever apply while locked.
    sealed class LockedDialog : Form
    {
        readonly Settings s;
        readonly CheckBox chkPower, chkBoost;
        readonly NumericUpDown nMax, nMin, nEpp, nCores;
        readonly Label lblCurve;

        public new bool Enabled { get { return chkPower.Checked; } }
        public int MaxState { get { return (int)nMax.Value; } }
        public int MinState { get { return (int)nMin.Value; } }
        public bool BoostOff { get { return chkBoost.Checked; } }
        public int Epp { get { return (int)nEpp.Value; } }
        public int MaxCores { get { return (int)nCores.Value; } }

        public LockedDialog(Settings settings)
        {
            s = settings;

            Text = "Clevo Fan Control - locked mode setup";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(470, 352);
            Font = new Font("Segoe UI", 9f);

            chkPower = new CheckBox();
            chkPower.Text = "Use this mode while the session is locked";
            chkPower.AutoSize = true;
            chkPower.Location = new Point(16, 16);
            chkPower.Checked = s.LockedPowerEnabled;
            Controls.Add(chkPower);

            GroupBox gPower = new GroupBox();
            gPower.Text = "Power cap (the hidden power plan)";
            gPower.Location = new Point(12, 46);
            gPower.Size = new Size(446, 200);
            Controls.Add(gPower);

            nMax = Makespin(gPower, "Maximum processor state", 22, s.LockedMaxState, 5, 100);
            Label maxHint = new Label();
            maxHint.Text = "25 % is about 0.9 GHz here. 50 % is still above base clock.";
            maxHint.ForeColor = Color.DimGray;
            maxHint.AutoSize = true;
            maxHint.Location = new Point(16, 70);
            gPower.Controls.Add(maxHint);

            nMin = Makespin(gPower, "Minimum processor state", 94, s.LockedMinState, 1, 100);
            nEpp = Makespin(gPower, "Energy preference (EPP)", 126, s.LockedEpp, 0, 100);

            nCores = Makespin(gPower, "Cores left unparked", 154, s.LockedMaxCores, 10, 100);
            Label coreHint = new Label();
            coreHint.Text = "Background workers need cores; parking more slows agents more.";
            coreHint.ForeColor = Color.DimGray;
            coreHint.AutoSize = true;
            coreHint.Location = new Point(16, 176);
            gPower.Controls.Add(coreHint);

            chkBoost = new CheckBox();
            chkBoost.Text = "No turbo boost while locked (avoids heat spikes)";
            chkBoost.AutoSize = true;
            chkBoost.Location = new Point(16, 228);
            Controls.Add(chkBoost);
            chkBoost.Checked = s.LockedBoostOff;

            lblCurve = new Label();
            lblCurve.AutoSize = true;
            lblCurve.Location = new Point(16, 260);
            lblCurve.Text = "Fan curve while locked is fixed and gentle:";
            Controls.Add(lblCurve);

            FanCurve c = FanCurve.Locked();
            Label curveLine = new Label();
            curveLine.AutoSize = true;
            curveLine.ForeColor = Color.DimGray;
            curveLine.Location = new Point(16, 280);
            curveLine.Text = string.Format("0 % up to {0} C  ->  {1} % at {2} C  ->  {3} % at {4} C  ->  full at 100 C",
                c.T2[0], c.D2[0], c.T2[0], c.D3[0], c.T3[0]);
            Controls.Add(curveLine);

            Label guardLine = new Label();
            guardLine.AutoSize = true;
            guardLine.ForeColor = Color.DimGray;
            guardLine.Location = new Point(16, 298);
            guardLine.Text = "The Safety temperature guard still overrides this at any temperature.";
            Controls.Add(guardLine);

            Button ok = new Button();
            ok.Text = "OK";
            ok.Location = new Point(262, 320);
            ok.Size = new Size(88, 26);
            ok.DialogResult = DialogResult.OK;
            AcceptButton = ok;
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.Location = new Point(358, 320);
            cancel.Size = new Size(88, 26);
            cancel.DialogResult = DialogResult.Cancel;
            CancelButton = cancel;
            Controls.Add(cancel);
        }

        // A labelled spinner, right-aligned label so the numbers line up.
        static NumericUpDown Makespin(Control parent, string text, int y, int value, int lo, int hi)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Location = new Point(16, y + 3);
            parent.Controls.Add(l);

            NumericUpDown n = new NumericUpDown();
            n.Minimum = lo;
            n.Maximum = hi;
            n.Location = new Point(214, y);
            n.Size = new Size(58, 23);
            n.Value = Fans.Clamp(value, lo, hi);
            parent.Controls.Add(n);
            return n;
        }
    }
}
