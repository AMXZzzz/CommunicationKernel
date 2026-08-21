using System;
using System.Windows.Forms;

namespace MEWTOCOL_Slave {
    static class Program {
        [STAThread]
        static void Main () {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
