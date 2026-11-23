using System;
using System.Windows.Forms;

namespace PsychoFlash.App
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                MessageBox.Show(e.Exception.Message, "PsychoFlash - unexpected error",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            Application.Run(new MainForm());
        }
    }
}
