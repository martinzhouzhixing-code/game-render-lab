using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GameRenderLab.Endfield
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if ((args.Length == 2 || args.Length == 3) && args[0] == "--self-test")
                return SelfTest.Run(args[1], args.Length == 3 ? args[2] : null);
            if (args.Length == 2 && args[0] == "--find")
            {
                try { File.WriteAllLines(args[1], EndfieldLocation.Find()); return 0; }
                catch (Exception error) { File.WriteAllText(args[1], error.Message); return 1; }
            }

            bool operation = args.Length == 2 && (args[0] == "--install" || args[0] == "--rollback");
            if (args.Length != 0 && !operation) return 2;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new InstallerWindow(operation ? args[0] : null, operation ? args[1] : null));
            return 0;
        }

        internal static bool IsAdministrator()
        {
            using (var identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    internal sealed class InstallerWindow : Form
    {
        private readonly ComboBox locations = new ComboBox();
        private readonly Label status = new Label();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly Button discover = new Button();
        private readonly Button browse = new Button();
        private readonly Button install = new Button();
        private readonly Button rollback = new Button();
        private readonly string operation;
        private readonly string operationDirectory;
        private bool busy;
        private bool applying;

        internal InstallerWindow(string operation, string directory)
        {
            this.operation = operation;
            operationDirectory = directory;
            Text = "Game Render Lab · 终末地安装器";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(640, 365);
            MinimumSize = new Size(650, 400);
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.FromArgb(23, 28, 37);
            ForeColor = Color.FromArgb(233, 238, 248);
            AutoScaleMode = AutoScaleMode.Dpi;

            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 7 };
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 41));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            Controls.Add(panel);
            panel.Controls.Add(new Label { Text = "终末地 · DLSS 5", Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, 19, FontStyle.Bold) }, 0, 0);
            panel.Controls.Add(new Label { Text = "Vulkan  ·  汉化面板  ·  安装前备份\n请取消启动器中的 DirectX 11 启动，安装时保持游戏关闭。", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(182, 197, 219) }, 0, 1);

            locations.Dock = DockStyle.Fill;
            locations.DropDownStyle = ComboBoxStyle.DropDownList;
            locations.SelectedIndexChanged += delegate { RefreshButtons(); };
            panel.Controls.Add(locations, 0, 2);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            panel.Controls.Add(buttons, 0, 3);
            AddButton(buttons, discover, "自动查找", delegate { FindLocations(); });
            AddButton(buttons, browse, "手动选择", delegate { SelectLocation(); });
            AddButton(buttons, install, "一键安装", delegate { BeginOperation("--install"); });
            AddButton(buttons, rollback, "回滚安装", delegate { BeginOperation("--rollback"); });
            install.BackColor = Color.FromArgb(39, 97, 169);

            progress.Dock = DockStyle.Fill;
            progress.Margin = new Padding(3, 5, 3, 4);
            panel.Controls.Add(progress, 0, 4);
            status.Dock = DockStyle.Fill;
            status.Padding = new Padding(3, 6, 3, 0);
            status.Text = "正在查找已安装的《终末地》…";
            panel.Controls.Add(status, 0, 5);
            panel.Controls.Add(new Label { Text = "适用首版验证范围 RTX 50。首次安装可能需联网下载固定来源组件。", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(174, 185, 201) }, 0, 6);
            Shown += delegate
            {
                if (this.operation == null) FindLocations();
                else
                {
                    locations.Items.Add(operationDirectory);
                    locations.SelectedIndex = 0;
                    RunOperation(this.operation, operationDirectory);
                }
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (applying)
                {
                    e.Cancel = true;
                    status.Text = "安装或回滚正在进行，请等待当前操作结束。";
                }
            };
            RefreshButtons();
        }

        private static void AddButton(Control container, Button button, string text, EventHandler onClick)
        {
            button.Text = text;
            button.Size = new Size(116, 33);
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = Color.FromArgb(42, 52, 68);
            button.ForeColor = Color.White;
            button.Click += onClick;
            container.Controls.Add(button);
        }

        private void RefreshButtons()
        {
            bool normal = operation == null && !busy;
            discover.Enabled = browse.Enabled = locations.Enabled = normal;
            install.Enabled = rollback.Enabled = normal && locations.SelectedItem != null;
            progress.Style = busy ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks;
        }

        private async void FindLocations()
        {
            if (busy) return;
            busy = true;
            RefreshButtons();
            status.Text = "正在检查启动器、安装注册表和常见游戏目录…";
            try
            {
                var found = await Task.Run(() => EndfieldLocation.Find());
                locations.Items.Clear();
                locations.Items.AddRange(found.Cast<object>().ToArray());
                if (found.Count > 0) locations.SelectedIndex = 0;
                status.Text = found.Count == 0 ? "没有自动找到签名有效的终末地。可以手动选择 Endfield.exe。" :
                    "找到 " + found.Count + " 份游戏安装。请确认路径并点击「一键安装」。";
            }
            catch (Exception error) { status.Text = error.Message; }
            finally { busy = false; RefreshButtons(); }
        }

        private void SelectLocation()
        {
            using (var picker = new OpenFileDialog { Filter = "终末地|Endfield.exe", CheckFileExists = true, Title = "选择 Endfield.exe" })
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string path = EndfieldLocation.Verify(Path.GetDirectoryName(picker.FileName));
                    if (!locations.Items.Contains(path)) locations.Items.Add(path);
                    locations.SelectedItem = path;
                    status.Text = "已验证终末地程序。";
                }
                catch (Exception error) { status.Text = error.Message; }
            }
        }

        private void BeginOperation(string mode)
        {
            string directory = locations.SelectedItem as string;
            if (directory == null) return;
            if (Program.IsAdministrator()) { RunOperation(mode, directory); return; }
            try
            {
                var start = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, mode + " \"" + directory + "\"")
                { UseShellExecute = true, Verb = "runas" };
                using (var process = Process.Start(start)) { }
                Close();
            }
            catch (Win32Exception error)
            {
                status.Text = error.NativeErrorCode == 1223 ? "已取消 Windows 管理员确认。没有安装组件。" : error.Message;
            }
        }

        private async void RunOperation(string mode, string directory)
        {
            if (!Program.IsAdministrator()) { status.Text = "安装与回滚需要 Windows 管理员权限。"; return; }
            applying = true;
            busy = true;
            RefreshButtons();
            var report = new Progress<string>(message => status.Text = message);
            try
            {
                await Task.Run(() => Deployment.Run(directory, mode == "--rollback", report));
                status.Text = mode == "--rollback" ? "已恢复这次安装管理的文件。" :
                    "安装完成。使用官方启动器并保持 DirectX 11 未勾选。进游戏按 Home 查看 DLSS 5 面板。";
                progress.Value = 100;
            }
            catch (Exception error)
            {
                status.Text = error.Message;
                MessageBox.Show(this, error.Message, "安装器状态", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { applying = false; busy = false; RefreshButtons(); }
        }
    }
}
