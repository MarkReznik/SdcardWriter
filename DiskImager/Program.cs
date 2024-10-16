﻿using System;
using System.Windows.Forms;

namespace DynamicDevices.DiskWriter
{
    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            } catch(Exception e)
            {
                MessageBox.Show(e.Message + " | " + e.StackTrace);
            }
        }
    }
}
