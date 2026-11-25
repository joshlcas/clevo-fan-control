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
            const double x3 = 100.0, y3 = 100.0;

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

    enum FanMode { Auto, Manual, Custom, Quiet, Smart }

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

        public FanCurve Custom()
        {
            return new FanCurve(T2, D2, T3, D3, MinDuty);
        }

        // Same shape as Custom, shifted 3 C later and trimmed 2 / 4 points, so
        // it stays just under Custom wherever the user moves the curve.
        public FanCurve QuietCurve()
        {
            int[] t2 = new int[3], d2 = new int[3], t3 = new int[3], d3 = new int[3];
            for (int i = 0; i < 3; i++)
            {
                t2[i] = T2[i] + 3;
                d2[i] = D2[i] - 2;
                t3[i] = T3[i] + 3;
                d3[i] = D3[i] - 4;
            }
            return new FanCurve(t2, d2, t3, d3, MinDuty);
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

            s.GuardTemp = Fans.Clamp(s.GuardTemp, 55, 105);
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
        CheckBox chkLink, chkGuard, chkRestore, chkReapply, chkAutoStart;
        Button btnApply, btnMax, btnOff, btnManual, btnCustom, btnQuiet, btnSmart, btnAuto, btnEdit;
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

        public MainForm(Settings s, bool startHidden, EventWaitHandle showSignal)
        {
            this.settings = s;
            this.startHidden = startHidden;
            this.showSignal = showSignal;

            Text = "Clevo Fan Control";
            ClientSize = new Size(640, 716);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            Font = new Font("Segoe UI", 9f);
            Icon = Logo.ToIcon(32);

            BuildUi();
            ApplySettingsToUi();

            poll.Interval = 1000;
            poll.Tick += delegate { Poll(); };
            reapply.Interval = 3000;
            reapply.Tick += delegate { Reapply(); };

            FormClosing += OnFormClosing;

            // No Load handler: with --tray the form is never shown, so Load is
            // never raised. Start the controller from the constructor instead.
            SetupTray();
            Poll();                                   // seed lastT
            EnterMode(settings.Mode, true);
            settings.Save();                          // make the file exist on first run
            poll.Start();
            reapply.Start();
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

            GroupBox gStatus = MakeGroup("Live", 66, 98);
            lblCpu = MakeMono(gStatus, 14, 20);
            lblGpu = MakeMono(gStatus, 14, 44);
            lblInfo = new Label();
            lblInfo.ForeColor = Color.DimGray;
            lblInfo.AutoSize = true;
            lblInfo.Location = new Point(14, 70);
            gStatus.Controls.Add(lblInfo);

            GroupBox gMode = MakeGroup("Fan mode", 170, 132);
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

            GroupBox gManual = MakeGroup("Manual duty", 308, 172);
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

            GroupBox gSafety = MakeGroup("Safety", 486, 104);
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
            chkReapply.Text = "Keep re-applying the current duty (every 3 s)";
            chkReapply.AutoSize = true;
            chkReapply.Location = new Point(16, 78);
            gSafety.Controls.Add(chkReapply);

            GroupBox gStartup = MakeGroup("Startup and tray", 596, 112);
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

        GroupBox MakeGroup(string text, int y, int h)
        {
            GroupBox g = new GroupBox();
            g.Text = text;
            g.Location = new Point(12, y);
            g.Size = new Size(616, h);
            Controls.Add(g);
            return g;
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
            chkAutoStart.Checked = AutoRun.Enabled();
            settings.AutoStart = chkAutoStart.Checked;
            // Re-write the Run entry so a moved exe does not leave a dead path.
            if (settings.AutoStart) AutoRun.Set(true);
            UpdatePct();
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
            settings.Mode = mode;
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
                    ApplyCurveMode(settings.QuietCurve(), "quiet");
                    break;

                case FanMode.Smart:
                    ApplyCurveMode(settings.Custom(), "smart");
                    break;
            }

            if (!restoring)
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
        }

        void Reapply()
        {
            if (guardActive) return;

            if (mode == FanMode.Manual)
            {
                if (!chkReapply.Checked) return;
                Fans.SetDuty(lastCpu, lastGpu, fanCount > 2 ? lastGpu : 0);
                return;
            }

            if (mode == FanMode.Custom || mode == FanMode.Quiet || mode == FanMode.Smart)
            {
                if (dutyNow[0] < 0) return;
                Fans.SetDuty(dutyNow[0], dutyNow[1], fanCount > 2 ? dutyNow[2] : 0);
            }
        }

        void Poll()
        {
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
            lblInfo.Text = string.Format("fans detected: {0}   -   mode: {1}{2}",
                fanCount, mode.ToString().ToLowerInvariant(), guardActive ? "   -   GUARD ACTIVE" : "");

            if (mode == FanMode.Auto)
                lblSmart.Text = "firmware owns the fans (EC curve)";
            else if (mode == FanMode.Manual)
                lblSmart.Text = string.Format("manual duty {0}% / {1}%",
                    Fans.RawToPercent(lastCpu), Fans.RawToPercent(lastGpu));

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
        }

        void EnterModeSilent(FanMode m)
        {
            mode = m;
            guardActive = false;
            dutyNow[0] = dutyNow[1] = dutyNow[2] = -1;
            UpdateModeUi();
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
                lblStatus.Text = "guard: " + hottest + " C reached " + guard + " C - full speed";
            }
            else if (guardActive && hottest <= guard - 6)
            {
                guardActive = false;
                dutyNow[0] = dutyNow[1] = dutyNow[2] = -1;   // let the curve take over at once
                lblStatus.Text = "guard released";
            }
        }

        void ControlTick(Telemetry t)
        {
            if (mode != FanMode.Custom && mode != FanMode.Quiet && mode != FanMode.Smart) return;
            if (guardActive) return;
            if (t == null || !t.Ok) return;

            FanCurve c = mode == FanMode.Quiet ? settings.QuietCurve() : settings.Custom();

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
            lblMode.Text = "mode: " + mode.ToString().ToLowerInvariant();
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
    }
}
