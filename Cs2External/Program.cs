using System;
using System.IO;
using System.Windows.Forms;

namespace Cs2External;

static class Program
{
    [STAThread]
    static void Main()
    {
        string logPath = Path.Combine(Path.GetTempPath(), "Cs2External_crash.log");
        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            File.WriteAllText(logPath, $"{DateTime.Now}: {ex}\n");
            MessageBox.Show($"Crash: {ex.Message}\n\nDetails written to:\n{logPath}", "Cs2External Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
