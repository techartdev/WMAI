// RegDword - sets a registry DWORD on the phone from a process running on the
// device (RAPI may not write protected keys such as HKLM\Drivers).
//   RegDword.exe HKLM "Drivers\Console" OutputTo 0
// Writes regdword.log next to the exe with the before/after values.
using System;
using System.IO;
using System.Text;
using Microsoft.Win32;

class RegDword
{
    [MTAThread]
    static void Main(string[] args)
    {
        string exe = System.Reflection.Assembly.GetExecutingAssembly().GetName().CodeBase;
        using (StreamWriter log = new StreamWriter(Path.Combine(Path.GetDirectoryName(exe), "regdword.log"), false, Encoding.UTF8))
        {
            try
            {
                RegistryKey root = args[0].ToUpper() == "HKCU" ? Registry.CurrentUser : Registry.LocalMachine;
                RegistryKey k = root.CreateSubKey(args[1]);
                log.WriteLine("before: " + k.GetValue(args[2]));
                k.SetValue(args[2], int.Parse(args[3]));
                log.WriteLine("after: " + k.GetValue(args[2]));
                k.Close();
                log.WriteLine("OK");
            }
            catch (Exception ex)
            {
                log.WriteLine("FAILED: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
