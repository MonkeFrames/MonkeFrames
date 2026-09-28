using System;
using System.Runtime.InteropServices;

namespace MonkeFrames.Plugins;

/// <summary>
/// MFPlugin is a base class that all plugins inherit from.
/// </summary>
public abstract class MFPlugin
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
    public void CreateMenu(string menuPath, Action action)
    {
        string[] menuPaths = menuPath.Split("/", StringSplitOptions.RemoveEmptyEntries);
        if (menuPaths.Length != 2)
            throw new ArgumentException("Menu path must be formatted as \"Menu Name/Button Text\"", nameof(menuPath));
    
        if (!MFPluginManager.Menus.ContainsKey(menuPaths[0].Trim()))
            MFPluginManager.Menus.Add(menuPaths[0].Trim(), new());
        
        MFPluginManager.Menus[menuPaths[0]].Add(menuPaths[1].Trim(), action);
    }

    /// <summary>
    /// Create a new window.
    /// </summary>
    /// <param name="menuPath">The path to the menu button. Formatted as `Menu Name/Button Text`.</param>
    /// <param name="action">The action to call when the menu button is pressed.</param>
    public MFWindow CreateWindow(string windowName, int sizeX, int sizeY, Action onOpen = null!, Action onClose = null!, Action onDraw = null!)
    {
        MFWindow window = new MFWindow(windowName,
            new System.Numerics.Vector2(sizeX, sizeY), onOpen, onClose, onDraw);
        
        MFPluginManager.Windows.Add(window);
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
    public MessageBoxResult ShowMessageBox(string title, string body, MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.None)
    {
        uint flags = (uint)buttons | (uint)icon;
        return (MessageBoxResult)MessageBox(IntPtr.Zero, body, title, flags);
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
    /// Called when MonkeFrames enables your plugin, or when the user manually enables it via GUI.
    /// </summary>
    public virtual void OnEnable() { }

    /// <summary>
    /// Called when MonkeFrames disables your plugin, or when the user manually disables it via GUI.
    /// </summary>
    public virtual void OnDisable() { }

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}