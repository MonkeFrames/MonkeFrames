using MonkeFrames.Compiler.Models;
using System;
using System.Runtime.InteropServices;

namespace MonkeFrames.Extensions;

/// <summary>
/// MFPlugin is a base class that all plugins inherit from.
/// </summary>
public abstract class FramesExtension
{
    /// <summary>
    /// Info provides relevant metadata to MonkeFrames when loading your plugin.
    /// Loading will be skipped if this attribute is missing.
    /// </summary>
    /// <param name="guid">The GUID of the plugin.</param>
    /// <param name="name">The name of the plugin.</param>
    /// <param name="version">The version of the plugin.</param>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class Info(string guid, string name, string version) : Attribute
    {
        /// <summary>
        /// The GUID of the plugin.
        /// </summary>
        public string GUID = guid;

        /// <summary>
        /// The name of the plugin.
        /// </summary>
        public string Name = name;

        /// <summary>
        /// The version of the plugin.
        /// </summary>
        public Version Version = new Version(version);
    }

    /// <summary>
    /// Create a new menu button.
    /// </summary>
    /// <param name="menuPath">The path to the menu button. Formatted as `Menu Name/Button Text`.</param>
    /// <param name="action">The action to call when the menu button is pressed.</param>
    public static void CreateMenu(string menuPath, Action action)
    {
        string[] menuPaths = menuPath.Split("/", StringSplitOptions.RemoveEmptyEntries);
        if (menuPaths.Length != 2)
            throw new ArgumentException("Menu path must be formatted as \"Menu Name/Button Text\"", nameof(menuPath));
    
        string menuName = menuPaths[0].Trim();
        string itemName = menuPaths[1].Trim();
        if (!ExtensionManager.Menus.ContainsKey(menuName))
            ExtensionManager.Menus.Add(menuName, new());
        
        ExtensionManager.Menus[menuName][itemName] = action;
    }

    /// <summary>
    /// Create a new window.
    /// </summary>
    /// <param name="menuPath">The path to the menu button. Formatted as `Menu Name/Button Text`.</param>
    /// <param name="action">The action to call when the menu button is pressed.</param>
    public static FramesWindow CreateWindow(string windowName, int sizeX, int sizeY, Action onOpen = null!, Action onClose = null!, Action onDraw = null!)
    {
        FramesWindow window = new FramesWindow(windowName,
            new System.Numerics.Vector2(sizeX, sizeY), onOpen, onClose, onDraw);
        
        ExtensionManager.Windows.Add(window);
        return window;
    }

    /// <summary>
    /// Display a message box and wait for it to close.
    /// </summary>
    /// <param name="title">The caption displayed above the message box.</param>
    /// <param name="body">The body text of the message box.</param>
    /// <param name="buttons">The buttons displayed on the message box.</param>
    /// <param name="icon">The icon shown on the message box. This will also play a sound based on icons.</param>
    /// <returns>The user action performed on the message box. If X is pressed, it will return `MessageBoxResult.Cancel`.</returns>
    public static MessageBoxResult ShowMessageBox(string title, string body, MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.None)
    {
        uint flags = (uint)buttons | (uint)icon;
        return (MessageBoxResult)MessageBox(IntPtr.Zero, body, title, flags);
    }

    /// <summary>
    /// Display an Open File dialog to the user and return the path selected, or null if cancelled.
    /// </summary>
    /// <param name="title">The title of the window.</param>
    /// <param name="filter">The file filter to pass, formatted as \"Display Name|*.extension;Other Display Name|*.otherextension\"</param>
    /// <param name="initialDirectory">The directory that shows by default when opening the dialog.</param>
    /// <param name="flags">Flags to pass to the Windows shell.</param>
    /// <returns>Path to the selected file, or null if cancelled.</returns>
    public static string ShowOpenFileDialog(string title = "Select File", string filter = "All Files (*.*)|*.*", string initialDirectory = @"C:\", FileDialogFlags flags = FileDialogFlags.OFN_FILEMUSTEXIST)
    {
        var ofn = new OPENFILENAME();
        ofn.lStructSize = Marshal.SizeOf(ofn);
        
        ofn.lpstrFilter = filter.Replace('|', '\0').Replace(';', '\0');
        
        ofn.lpstrFile = new string(new char[256]);
        ofn.nMaxFile = ofn.lpstrFile.Length;

        ofn.lpstrInitialDir = initialDirectory;
        
        ofn.lpstrFileTitle = new string(new char[256]);
        ofn.nMaxFileTitle = ofn.lpstrFileTitle.Length;
        
        ofn.lpstrTitle = title;
        
        ofn.Flags = (int)flags; 

        if (GetOpenFileName(ref ofn))
            return ofn.lpstrFile;
        
        return null;
    }

    /// <summary>
    /// Display a save file dialog to the user and return the path selected, or null if cancelled.
    /// </summary>
    /// <param name="title">The title of the window.</param>
    /// <param name="filter">The file filter to pass, formatted as \"Display Name|*.extension;Other Display Name|*.otherextension\"</param>
    /// <param name="initialDirectory">The directory that shows by default when opening the dialog.</param>
    /// <param name="defaultExtension">The extension automatically appended to the file name if none is provided.</param>
    /// <param name="flags">Flags to pass to the Windows shell.</param>
    /// <returns>Path to the selected file, or null if cancelled.</returns>
    public static string ShowSaveFileDialog(string title = "Select File", string filter = "All Files (*.*)|*.*", string initialDirectory = @"C:\", string defaultExtension = "txt", FileDialogFlags flags = FileDialogFlags.OFN_OVERWRITEPROMPT | FileDialogFlags.OFN_PATHMUSTEXIST)
    {
        var ofn = new OPENFILENAME();
        ofn.lStructSize = Marshal.SizeOf(ofn);
        
        ofn.lpstrFilter = filter.Replace('|', '\0').Replace(';', '\0');
        
        ofn.lpstrFile = new string(new char[260]);
        ofn.nMaxFile = ofn.lpstrFile.Length;

        ofn.lpstrInitialDir = initialDirectory;
        
        ofn.lpstrFileTitle = new string(new char[260]);
        ofn.nMaxFileTitle = ofn.lpstrFileTitle.Length;
        
        ofn.lpstrTitle = title;
        ofn.lpstrDefExt = default;
        
        ofn.Flags = (int)flags; 

        if (GetSaveFileName(ref ofn))
            return ofn.lpstrFile;
        
        return null;
    }

    /// <summary>
    /// Called when MonkeFrames initially loads your plugin.
    /// All menu and MFWindow creation should be done here to ensure that they have been created
    /// by the time that the editor loads your plugin.
    /// </summary>
    public virtual void OnLoad() { }

    /// <summary>
    /// Called when MonkeFrames is unloading your plugin.
    /// </summary>
    public virtual void OnUnload() { }

    /// <summary>
    /// Called before MonkeFrames compiles a project.
    /// </summary>
    public virtual void OnCompilationStart(Project project) { }

    /// <summary>
    /// Called after MonkeFrames finishes compiling a project.
    /// </summary>
    public virtual void OnCompilationEnd(Project project) { }

    /// <summary>
    /// Called before MonkeFrames moves to a keyframe's position and rotation.
    /// The keyframe in the project will not change, this only affects the
    /// camera.
    /// 
    /// This is called during playback (Project > Play) and exporting (Project > Export to MP4),
    /// but not from the Keyframe Player (Window > Keyframe Player).
    /// 
    /// If multiple extensions return a keyframe, the first one that returned a value will have priority.
    /// </summary>
    /// <param name="keyframe">The keyframe the camera was going to move to.</param>
    /// keyframe instead of the one provided in the method.</returns>
    public virtual void OnKeyframeStep(ref Keyframe keyframe) { }

    /// <summary>
    /// Patch a method with a prefix or postfix delegate. Do not use this if you don't know what you
    /// are doing, bad calls to this method may crash MonkeFrames.
    /// </summary>
    /// <param name="methodName">The fully qualified name of the method, eg. MonkeFrames.Editor.Plugin:OnPlayerSpawned</param>
    /// <param name="patchMethod">The method to call as the patch.</param>
    /// <param name="patchType">The type of patch to apply.</param> 
    /// <see href="https://harmony.pardeike.net/v2/articles/patching-prefix.html">Harmony Prefix Docs</see>
    /// <see href="https://harmony.pardeike.net/v2/articles/patching-postfix.html">Harmony Postfix Docs</see>
    /// <exception cref="ArgumentException">Parameter has bad format or you are missing an Info attribute.</exception>
    /// <exception cref="Exception">Most likely an exception when patching with harmony.</exception>
    public void ApplyPatch(string methodName, Delegate patchMethod, PatchType patchType = PatchType.Prefix) {
        ExtensionManager.ManagerApplyPatch(GetType(), methodName, patchMethod, patchType);
    }

    public enum PatchType
    {
        Prefix,
        Postfix
    }

    #region MessageBox enums
    public enum MessageBoxButtons : uint
    {
        /// <summary>The message box contains one push button: OK. This is the default.</summary>
        OK = 0x00000000,
        
        /// <summary>The message box contains two push buttons: OK and Cancel.</summary>
        OKCancel = 0x00000001,
        
        /// <summary>The message box contains three push buttons: Abort, Retry, and Ignore.</summary>
        AbortRetryIgnore = 0x00000002,
        
        /// <summary>The message box contains three push buttons: Yes, No, and Cancel.</summary>
        YesNoCancel = 0x00000003,
        
        /// <summary>The message box contains two push buttons: Yes and No.</summary>
        YesNo = 0x00000004,
        
        /// <summary>The message box contains two push buttons: Retry and Cancel.</summary>
        RetryCancel = 0x00000005,
        
        /// <summary>The message box contains three push buttons: Cancel, Try Again, Continue.</summary>
        CancelTryContinue = 0x00000006
    }

    public enum MessageBoxIcon : uint
    {
        /// <summary>No icon.</summary>
        None = 0x00000000,

        /// <summary>An icon consisting of a white sign in a circle with a red background (Stop, Error, Hand).</summary>
        Hand = 0x00000010,
        Stop = 0x00000010,
        Error = 0x00000010,

        /// <summary>An icon consisting of a question mark in a circle.</summary>
        Question = 0x00000020,

        /// <summary>An icon consisting of an exclamation point in a triangle with a yellow background (Exclamation, Warning).</summary>
        Exclamation = 0x00000030,
        Warning = 0x00000030,

        /// <summary>An icon consisting of a lowercase letter i in a circle (Asterisk, Information).</summary>
        Asterisk = 0x00000040,
        Information = 0x00000040
    }

    public enum MessageBoxResult : int
    {
        /// <summary>The OK button was selected.</summary>
        OK = 1,

        /// <summary>The Cancel button was selected.</summary>
        Cancel = 2,

        /// <summary>The Abort button was selected.</summary>
        Abort = 3,

        /// <summary>The Retry button was selected.</summary>
        Retry = 4,

        /// <summary>The Ignore button was selected.</summary>
        Ignore = 5,

        /// <summary>The Yes button was selected.</summary>
        Yes = 6,

        /// <summary>The No button was selected.</summary>
        No = 7,

        /// <summary>The Try Again button was selected.</summary>
        TryAgain = 10,

        /// <summary>The Continue button was selected.</summary>
        Continue = 11
    }
    #endregion

    #region File Dialog
    [Flags]
    public enum FileDialogFlags : int
    {
        /// <summary>The user can type only names of existing directories.</summary>
        OFN_READONLY = 0x00000001,
        /// <summary>Causes the Save As dialog box to prompt the user for permission to overwrite an existing file.</summary>
        OFN_OVERWRITEPROMPT = 0x00000002,
        /// <summary>Hides the Read Only check box.</summary>
        OFN_HIDEREADONLY = 0x00000004,
        /// <summary>Causes the dialog box to restore the current directory to its original value if the user changed it.</summary>
        OFN_NOCHANGEDIR = 0x00000008,
        /// <summary>Causes the dialog box to use the default help procedure.</summary>
        OFN_SHOWHELP = 0x00000010,
        /// <summary>Enables hook procedures specified in the lpfnHook member.</summary>
        OFN_ENABLEHOOK = 0x00000020,
        /// <summary>Enables dialog box templates.</summary>
        OFN_ENABLETEMPLATE = 0x00000040,
        /// <summary>Enables dialog box templates by handle.</summary>
        OFN_ENABLETEMPLATEHANDLE = 0x00000080,
        /// <summary>The lpstrFilter buffer contains no invalid characters.</summary>
        OFN_NOVALIDATE = 0x00000100,
        /// <summary>Allows the user to select more than one file.</summary>
        OFN_ALLOWMULTISELECT = 0x00000200,
        /// <summary>Specifies that the extension of the returned filename is different from the extension specified by lpstrDefExt.</summary>
        OFN_EXTENSIONDIFFERENT = 0x00000400,
        /// <summary>The user can type only valid paths.</summary>
        OFN_PATHMUSTEXIST = 0x00000800,
        /// <summary>The user can type only names of existing files.</summary>
        OFN_FILEMUSTEXIST = 0x00001000,
        /// <summary>The dialog box prompts the user for permission to create a file that does not currently exist.</summary>
        OFN_CREATEPROMPT = 0x00002000,
        /// <summary>Causes the dialog box to share violations or network errors.</summary>
        OFN_SHAREAWARE = 0x00004000,
        /// <summary>Specifies that the returned file does not have the Read Only attribute and is not in a write-protected directory.</summary>
        OFN_NOREADONLYRETURN = 0x00008000,
        /// <summary>Specifies that the file is not to be added to the recent documents list.</summary>
        OFN_NOTESTFILECREATE = 0x00010000,
        /// <summary>Forces the hiding of the Read Only check box.</summary>
        OFN_NONETWORKBUTTON = 0x00020000,
        /// <summary>Directs the dialog box to return the path and file name of the selected shortcut (.lnk) file.</summary>
        OFN_NODEREFERENCELINKS = 0x00100000,
        /// <summary>Causes the dialog box to use the Explorer-style user interface.</summary>
        OFN_EXPLORER = 0x00080000,
        /// <summary>Prevents the system from adding a link to the selected file in the recent documents list.</summary>
        OFN_DONTADDTORECENT = 0x02000000,
        /// <summary>Forces the dialog box to show hidden and system files.</summary>
        OFN_FORCESHOWHIDDEN = 0x10000000
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct OPENFILENAME
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public string lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public string lpstrFile;
        public int nMaxFile;
        public string lpstrFileTitle;
        public int nMaxFileTitle;
        public string lpstrInitialDir;
        public string lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }
    #endregion

    #region Win32
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool GetOpenFileName(ref OPENFILENAME ofn);

    [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool GetSaveFileName(ref OPENFILENAME ofn);
    #endregion
}