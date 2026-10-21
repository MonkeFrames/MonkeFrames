using System;
using System.IO;
using System.Runtime.InteropServices;

namespace MonkeFrames.Editor.Utilities;

public class Win32Utilities
{
    [DllImport("Comdlg32.dll", SetLastError = true, ThrowOnUnmappableChar = true, CharSet = CharSet.Auto)]
    public static extern bool GetOpenFileName([In, Out] OpenFileName ofn);

    public static string OpenFile(string title = "Open File", string filter = "All Files\0*.*\0\0", string initialFolder = @"C:\")
    {
        OpenFileName ofn = new OpenFileName();
        ofn.structSize = Marshal.SizeOf(ofn);
        ofn.file = new string(new char[256]);
        ofn.maxFile = ofn.file.Length;
        ofn.fileTitle = new string(new char[64]);
        ofn.maxFileTitle = ofn.fileTitle.Length;
        ofn.initialDir = initialFolder;
        ofn.title = title;
        ofn.filter = filter;
        ofn.flags = 0x00080000 | 0x00001000 | 0x00000800; // OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST

        if (GetOpenFileName(ofn))
        {
            return ofn.file;
        }
        return null;
    }

    private const int OFN_OVERWRITEPROMPT = 0x00000002;
    private const int OFN_PATHMUSTEXIST = 0x00000800;

    public static void SaveFile(string title, string content, string extension, string filter, string initialDir = @"C:\")
    {
        OpenFileName ofn = new OpenFileName();

        ofn.structSize = Marshal.SizeOf(ofn);

        ofn.file = new string(new char[256]);
        ofn.maxFile = ofn.file.Length;
        ofn.fileTitle = new string(new char[64]);
        ofn.maxFileTitle = ofn.fileTitle.Length;

        ofn.title = title;
        ofn.initialDir = initialDir;

        ofn.filter = filter;
        ofn.defExt = extension;

        ofn.flags = OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST;

        if (GetOpenFileName(ofn))
        {
            string chosenFilePath = ofn.file;

            File.WriteAllText(chosenFilePath, content);
            MonkeFrames.Extensions.FramesLog.Log($"Wrote to {chosenFilePath}");
        }
    }

    public static void ShowMessageDialog(string title, string content)
    {
        IntPtr hwnd = GetActiveWindow();
        MessageBox(hwnd, content, title, 0x00000000 | 0x00000060);
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int MessageBox(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public class OpenFileName
    {
        public int structSize = 0;
        public IntPtr dlgOwner = IntPtr.Zero;
        public IntPtr instance = IntPtr.Zero;
        public string filter = null;
        public string customFilter = null;
        public int maxCustFilter = 0;
        public int filterIndex = 0;
        public string file = null;
        public int maxFile = 0;
        public string fileTitle = null;
        public int maxFileTitle = 0;
        public string initialDir = null;
        public string title = null;
        public int flags = 0;
        public short fileOffset = 0;
        public short fileExtension = 0;
        public string defExt = null;
        public IntPtr custData = IntPtr.Zero;
        public IntPtr hook = IntPtr.Zero;
        public string templateName = null;
    }
}
