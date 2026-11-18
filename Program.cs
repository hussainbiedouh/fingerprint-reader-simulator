using System;
using System.Windows.Forms;

namespace WindowsFingerprintSimulator;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        FingerprintStore.SelfCheck();
        Application.Run(new MainForm());
    }
}
