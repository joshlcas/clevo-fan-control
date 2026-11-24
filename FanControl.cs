using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ClevoFan
{
    static class Native
    {
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadLibraryW(string path);

        [DllImport("kernel32")]
        public static extern IntPtr GetConsoleWindow();

        [DllImport("user32")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32")]
        public static extern bool SetProcessDPIAware();
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

            byte[] c = new byte[256];
            if (Dchu.GetDCHU_Data_Buffer(13, c) == 13) t.FanCount = c[12];

            t.Ok = true;
            return t;
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

        // "Quiet" preset: the machine's balanced curve with both mid points moved
        // 3 C later and trimmed 2 / 4 points, so it sits just under the original
        // everywhere below 100 C.
        static readonly int[] Q_T2 = new int[] { 68, 69, 69 };
        static readonly int[] Q_D2 = new int[] { 39, 46, 46 };
        static readonly int[] Q_T3 = new int[] { 83, 83, 83 };
        static readonly int[] Q_D3 = new int[] { 52, 55, 55 };

        // The curve this machine shipped with, captured before anything was written.
        static readonly int[] O_T2 = new int[] { 65, 66, 66 };
        static readonly int[] O_D2 = new int[] { 41, 48, 48 };
        static readonly int[] O_T3 = new int[] { 80, 80, 80 };
        static readonly int[] O_D3 = new int[] { 56, 59, 59 };

        public static byte[] BuildCurve(FanTable t, int[] qT2, int[] qD2, int[] qT3, int[] qD3)
        {
            byte[] b = new byte[256];
            int[] slot = new int[] { 2, 6, 10 };
            int[] slope = new int[] { 14, 20, 26 };

            for (int i = 0; i < 3; i++)
            {
                if (i >= t.FanCount) continue;

                int t1 = t.T1[i] > 0 ? t.T1[i] : 40;
                int d1 = t.D1[i] > 0 ? t.D1[i] : 25;
                int t2 = qT2[i], d2 = qD2[i], t3 = qT3[i], d3 = qD3[i];

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

        static string Describe(FanTable t, int[] qT2, int[] qD2, int[] qT3, int[] qD3)
        {
            StringBuilder sb = new StringBuilder();
            string[] names = new string[] { "CPU", "GPU1", "GPU2" };
            for (int i = 0; i < 3; i++)
            {
                if (i >= t.FanCount) continue;
                int t1 = t.T1[i] > 0 ? t.T1[i] : 40;
                int d1 = t.D1[i] > 0 ? t.D1[i] : 25;
                if (sb.Length > 0) sb.Append("   ");
                sb.AppendFormat("{0} {1}/{2}% {3}/{4}% {5}/{6}% 100/100%",
                    names[i], t1, d1, qT2[i], qD2[i], qT3[i], qD3[i]);
            }
            return sb.ToString();
        }

        static int ApplyCurve(FanTable t, int[] qT2, int[] qD2, int[] qT3, int[] qD3, out string desc)
        {
            desc = Describe(t, qT2, qD2, qT3, qD3);
            SetAuto();                        // drop any manual duty first
            int rc = Dchu.SetDCHU_Data(CmdCustomTable, BuildCurve(t, qT2, qD2, qT3, qD3), 256);
            if (rc != CmdCustomTable)
            {
                desc = "table write rejected (rc " + rc + ")";
                return rc;
            }
            return SetMode(6);                // custom
        }

        public static int ApplyQuiet(out string desc)
        {
            FanTable t = FanTable.Read();
            if (t == null) { desc = "could not read fan table"; return -1; }
            return ApplyCurve(t, Q_T2, Q_D2, Q_T3, Q_D3, out desc);
        }

        public static int ApplyOriginal(out string desc)
        {
            FanTable t = FanTable.Read();
            if (t == null) { desc = "could not read fan table"; return -1; }
            return ApplyCurve(t, O_T2, O_D2, O_T3, O_D3, out desc);
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

    static class Program
    {
        static string dllLoadedFrom;

        [STAThread]
        static int Main(string[] args)
        {
            if (!LoadDriverDll())
            {
                Console.Error.WriteLine("Could not load InsydeDCHU.dll. Is Control Center installed?");
                return 2;
            }

            if (args.Length > 0)
                return RunCli(args);

            // GUI: no console window wanted.
            IntPtr con = Native.GetConsoleWindow();
            if (con != IntPtr.Zero) Native.ShowWindow(con, 0);

            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
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
                        string desc;
                        int rc = Fans.ApplyQuiet(out desc);
                        Console.WriteLine("quiet -> rc " + rc + "  " + desc);
                        did = true;
                        break;
                    }

                    case "--original":
                    {
                        string desc;
                        int rc = Fans.ApplyOriginal(out desc);
                        Console.WriteLine("original -> rc " + rc + "  " + desc);
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
            Console.WriteLine("  FanControl.exe --status            read fans once");
            Console.WriteLine("  FanControl.exe --table             read the EC fan curve");
            Console.WriteLine("  FanControl.exe --set 60            both fans to 60%");
            Console.WriteLine("  FanControl.exe --duty 153          both fans to raw duty 153 (0-255)");
            Console.WriteLine("  FanControl.exe --cpu 60 --gpu 80   per-fan percent");
            Console.WriteLine("  FanControl.exe --max | --off       full speed / stopped");
            Console.WriteLine("  FanControl.exe --quiet             softer curve than the original");
            Console.WriteLine("  FanControl.exe --original          restore the curve this machine had");
            Console.WriteLine("  FanControl.exe --auto              firmware automatic (EC curve)");
            Console.WriteLine("  FanControl.exe --defaults          load firmware table (replaces current curve)");
            Console.WriteLine("  FanControl.exe --mode 0|1|3|5|6    firmware preset");
        }
    }

    sealed class MainForm : Form
    {
        readonly Timer poll = new Timer();
        readonly Timer reapply = new Timer();

        Label lblCpu, lblGpu, lblInfo, lblStatus, lblCpuPct, lblGpuPct;
        TrackBar tbCpu, tbGpu;
        NumericUpDown numCpu, numGpu, numGuard;
        CheckBox chkLink, chkGuard, chkRestore, chkReapply;
        Button btnApply, btnMax, btnAuto, btnOff, btnQuiet, btnOriginal;

        bool syncing;
        bool manualActive;
        bool guardActive;
        int lastCpu = 255, lastGpu = 255;
        int fanCount = 2;

        public MainForm()
        {
            Text = "Clevo Fan Control";
            ClientSize = new Size(620, 610);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            Font = new Font("Segoe UI", 9f);

            Label title = new Label();
            title.Text = "Clevo Fan Control";
            title.Font = new Font("Segoe UI Semibold", 15f);
            title.AutoSize = true;
            title.Location = new Point(14, 10);
            Controls.Add(title);

            Label sub = new Label();
            sub.Text = "X170KM-G  -  drives the EC through the same AcpiBridge path as Control Center";
            sub.ForeColor = Color.DimGray;
            sub.AutoSize = true;
            sub.Location = new Point(16, 42);
            Controls.Add(sub);

            GroupBox gStatus = new GroupBox();
            gStatus.Text = "Live";
            gStatus.Location = new Point(12, 70);
            gStatus.Size = new Size(596, 112);
            Controls.Add(gStatus);

            lblCpu = new Label();
            lblCpu.Font = new Font("Consolas", 10f);
            lblCpu.AutoSize = true;
            lblCpu.Location = new Point(14, 24);
            gStatus.Controls.Add(lblCpu);

            lblGpu = new Label();
            lblGpu.Font = new Font("Consolas", 10f);
            lblGpu.AutoSize = true;
            lblGpu.Location = new Point(14, 50);
            gStatus.Controls.Add(lblGpu);

            lblInfo = new Label();
            lblInfo.ForeColor = Color.DimGray;
            lblInfo.AutoSize = true;
            lblInfo.Location = new Point(14, 78);
            gStatus.Controls.Add(lblInfo);

            GroupBox gManual = new GroupBox();
            gManual.Text = "Manual duty";
            gManual.Location = new Point(12, 190);
            gManual.Size = new Size(596, 226);
            Controls.Add(gManual);

            Label l1 = new Label();
            l1.Text = "CPU";
            l1.AutoSize = true;
            l1.Location = new Point(16, 33);
            gManual.Controls.Add(l1);

            tbCpu = new TrackBar();
            tbCpu.Minimum = 0;
            tbCpu.Maximum = 255;
            tbCpu.TickFrequency = 32;
            tbCpu.LargeChange = 16;
            tbCpu.SmallChange = 1;
            tbCpu.Location = new Point(60, 22);
            tbCpu.Size = new Size(330, 45);
            tbCpu.ValueChanged += delegate { if (!syncing) { numCpu.Value = tbCpu.Value; if (chkLink.Checked) { numGpu.Value = tbCpu.Value; tbGpu.Value = tbCpu.Value; } } };
            gManual.Controls.Add(tbCpu);

            numCpu = new NumericUpDown();
            numCpu.Minimum = 0;
            numCpu.Maximum = 255;
            numCpu.Location = new Point(400, 30);
            numCpu.Size = new Size(62, 23);
            numCpu.ValueChanged += delegate { syncing = true; tbCpu.Value = (int)numCpu.Value; syncing = false; if (chkLink.Checked) { numGpu.Value = numCpu.Value; tbGpu.Value = (int)numCpu.Value; } UpdatePct(); };
            gManual.Controls.Add(numCpu);

            lblCpuPct = new Label();
            lblCpuPct.AutoSize = true;
            lblCpuPct.Location = new Point(470, 33);
            lblCpuPct.Text = "0%";
            gManual.Controls.Add(lblCpuPct);

            Label l2 = new Label();
            l2.Text = "GPU";
            l2.AutoSize = true;
            l2.Location = new Point(16, 93);
            gManual.Controls.Add(l2);

            tbGpu = new TrackBar();
            tbGpu.Minimum = 0;
            tbGpu.Maximum = 255;
            tbGpu.TickFrequency = 32;
            tbGpu.LargeChange = 16;
            tbGpu.SmallChange = 1;
            tbGpu.Location = new Point(60, 82);
            tbGpu.Size = new Size(330, 45);
            tbGpu.ValueChanged += delegate { if (!syncing) { numGpu.Value = tbGpu.Value; if (chkLink.Checked) { numCpu.Value = tbGpu.Value; tbCpu.Value = tbGpu.Value; } } };
            gManual.Controls.Add(tbGpu);

            numGpu = new NumericUpDown();
            numGpu.Minimum = 0;
            numGpu.Maximum = 255;
            numGpu.Location = new Point(400, 90);
            numGpu.Size = new Size(62, 23);
            numGpu.ValueChanged += delegate { syncing = true; tbGpu.Value = (int)numGpu.Value; syncing = false; if (chkLink.Checked) { numCpu.Value = numGpu.Value; tbCpu.Value = (int)numGpu.Value; } UpdatePct(); };
            gManual.Controls.Add(numGpu);

            lblGpuPct = new Label();
            lblGpuPct.AutoSize = true;
            lblGpuPct.Location = new Point(470, 93);
            lblGpuPct.Text = "0%";
            gManual.Controls.Add(lblGpuPct);

            chkLink = new CheckBox();
            chkLink.Text = "Link CPU and GPU";
            chkLink.AutoSize = true;
            chkLink.Location = new Point(16, 132);
            chkLink.Checked = true;
            gManual.Controls.Add(chkLink);

            btnApply = MakeButton(gManual, "Apply", 16, 166, 80);
            btnApply.Click += delegate { ApplyManual(); };

            btnMax = MakeButton(gManual, "Full speed", 102, 166, 92);
            btnMax.Click += delegate { numCpu.Value = 255; numGpu.Value = 255; ApplyManual(); };

            btnAuto = MakeButton(gManual, "Automatic", 200, 166, 92);
            btnAuto.Click += delegate { SetAuto(); };

            btnOff = MakeButton(gManual, "Fans off", 298, 166, 88);
            btnOff.Click += delegate { numCpu.Value = 0; numGpu.Value = 0; ApplyManual(); };

            btnQuiet = MakeButton(gManual, "Quiet", 392, 166, 70);
            btnQuiet.Click += delegate { ApplyQuiet(); };

            btnOriginal = MakeButton(gManual, "Original", 468, 166, 84);
            btnOriginal.Click += delegate { ApplyOriginal(); };

            Label hint = new Label();
            hint.Text = "Duty is the raw PWM value the EC accepts (0-255). Quiet = softer curve, Original = the curve this machine had.";
            hint.ForeColor = Color.DimGray;
            hint.AutoSize = true;
            hint.Location = new Point(16, 198);
            gManual.Controls.Add(hint);

            GroupBox gSafety = new GroupBox();
            gSafety.Text = "Safety";
            gSafety.Location = new Point(12, 426);
            gSafety.Size = new Size(596, 140);
            Controls.Add(gSafety);

            chkGuard = new CheckBox();
            chkGuard.Text = "Temperature guard: full speed above";
            chkGuard.AutoSize = true;
            chkGuard.Location = new Point(16, 26);
            chkGuard.Checked = true;
            gSafety.Controls.Add(chkGuard);

            numGuard = new NumericUpDown();
            numGuard.Minimum = 55;
            numGuard.Maximum = 105;
            numGuard.Value = 92;
            numGuard.Location = new Point(292, 24);
            numGuard.Size = new Size(56, 23);
            gSafety.Controls.Add(numGuard);

            Label l3 = new Label();
            l3.Text = "C";
            l3.AutoSize = true;
            l3.Location = new Point(354, 28);
            gSafety.Controls.Add(l3);

            chkRestore = new CheckBox();
            chkRestore.Text = "Hand fans back to automatic when this window closes";
            chkRestore.AutoSize = true;
            chkRestore.Location = new Point(16, 58);
            chkRestore.Checked = true;
            gSafety.Controls.Add(chkRestore);

            chkReapply = new CheckBox();
            chkReapply.Text = "Keep re-applying the manual duty (every 3 s)";
            chkReapply.AutoSize = true;
            chkReapply.Location = new Point(16, 86);
            chkReapply.Checked = true;
            gSafety.Controls.Add(chkReapply);

            lblStatus = new Label();
            lblStatus.AutoSize = true;
            lblStatus.ForeColor = Color.DimGray;
            lblStatus.Location = new Point(16, 114);
            lblStatus.Text = "starting...";
            gSafety.Controls.Add(lblStatus);

            poll.Interval = 1000;
            poll.Tick += delegate { Poll(); };
            reapply.Interval = 3000;
            reapply.Tick += delegate { if (manualActive && chkReapply.Checked && !guardActive) ApplyManualSilent(); };

            Load += delegate { UpdatePct(); Poll(); poll.Start(); reapply.Start(); };
            FormClosing += delegate
            {
                poll.Stop();
                reapply.Stop();
                if (chkRestore.Checked) Fans.SetAuto();
            };
        }

        Button MakeButton(Control parent, string text, int x, int y, int w)
        {
            Button b = new Button();
            b.Text = text;
            b.Location = new Point(x, y);
            b.Size = new Size(w, 28);
            parent.Controls.Add(b);
            return b;
        }

        void UpdatePct()
        {
            lblCpuPct.Text = Fans.RawToPercent((int)numCpu.Value) + "%";
            lblGpuPct.Text = Fans.RawToPercent((int)numGpu.Value) + "%";
        }

        void Poll()
        {
            Telemetry t = Telemetry.Read();
            if (!t.Ok)
            {
                lblCpu.Text = "CPU  --";
                lblGpu.Text = "GPU  --";
                lblInfo.Text = "read failed";
                return;
            }

            fanCount = t.FanCount;
            lblCpu.Text = string.Format("CPU  {0,5} RPM   duty {1,3} ({2,3}%)   {3} C",
                Telemetry.ToRpm(t.CpuRpmRaw), t.CpuDuty, Fans.RawToPercent(t.CpuDuty), t.CpuTemp);
            lblGpu.Text = string.Format("GPU  {0,5} RPM   duty {1,3} ({2,3}%)   {3} C",
                Telemetry.ToRpm(t.GpuRpmRaw), t.GpuDuty, Fans.RawToPercent(t.GpuDuty), t.GpuTemp);
            lblInfo.Text = string.Format("fans detected: {0}   -   manual: {1}{2}",
                fanCount, manualActive ? "on" : "off", guardActive ? "   -   GUARD ACTIVE" : "");

            if (!chkGuard.Checked || !manualActive) return;

            int guard = (int)numGuard.Value;
            int hottest = Math.Max(t.CpuTemp, t.GpuTemp);
            if (t.FanCount > 2) hottest = Math.Max(hottest, t.Gpu2Temp);

            if (!guardActive && hottest >= guard)
            {
                guardActive = true;
                Fans.SetDuty(255, 255, 0);
                lblStatus.Text = "guard: " + hottest + " C reached " + guard + " C - full speed";
            }
            else if (guardActive && hottest <= guard - 6)
            {
                guardActive = false;
                ApplyManualSilent();
                lblStatus.Text = "guard released - manual duty restored";
            }
        }

        void ApplyManual()
        {
            int cpu = (int)numCpu.Value;
            int gpu = (int)numGpu.Value;
            int rc = Fans.SetDuty(cpu, gpu, 0);
            manualActive = true;
            guardActive = false;
            lastCpu = cpu;
            lastGpu = gpu;
            lblStatus.Text = "applied duty " + cpu + "/" + gpu + "  (rc " + rc + ")";
        }

        void ApplyManualSilent()
        {
            Fans.SetDuty(lastCpu, lastGpu, 0);
        }

        void ApplyQuiet()
        {
            string desc;
            int rc = Fans.ApplyQuiet(out desc);
            manualActive = false;
            guardActive = false;
            lblStatus.Text = "quiet curve: " + desc + "   (rc " + rc + ")";
        }

        void ApplyOriginal()
        {
            string desc;
            int rc = Fans.ApplyOriginal(out desc);
            manualActive = false;
            guardActive = false;
            lblStatus.Text = "original curve: " + desc + "   (rc " + rc + ")";
        }

        void SetAuto()
        {
            int rc = Fans.SetAuto();
            Fans.SetMode(0);
            manualActive = false;
            guardActive = false;
            lblStatus.Text = "automatic control restored  (rc " + rc + ")";
        }
    }
}
