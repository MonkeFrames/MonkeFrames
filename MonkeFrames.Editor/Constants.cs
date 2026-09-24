using MonkeFrames.Compiler.Models;
using MonkeFrames.Editor.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace MonkeFrames.Editor;

public static class Constants
{
    public const string Name = "MonkeFrames";
    public const string Guid = "dev.sirkingbinx.monkeframes";
    public const string Version = "2.0";
    public static readonly string VersionID = $"{Version} Beta 1";

    private static string _buildDate;
    public static string BuildDate
    {
        get
        {
            _buildDate ??= Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attr => attr.Key == "BuildTime")?.Value;

            return _buildDate;
        }
    }

    public static string DataFolder = "";
    public static string MonkeFramesAssemblyFolder = "";
    public static readonly Exporter Exporter = new Exporter(Guid, "MonkeFrames");

    public static void Init()
    {
        DataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MonkeFrames");

        string[] folders = ["projects", "exports", "replays", "objects"];
        foreach (string folder in folders)
            Directory.CreateDirectory(SystemUtilities.Combine(DataFolder, folder));

            MonkeFramesAssemblyFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
    }

    public static readonly Dictionary<string, string> Contributors = new()
    {
        {"SirKingBinx", "Developer" },
        {"uhJames", "Developer" },
        {"YourBoiAlex", "Developer" },
        {"Olibobs", "Developer" },
        {"", "" },
        {"MrNubbaWubington", "Tester" },
        {"nebwella", "Tester" },
        {"ritesies", "Tester" },
        {"tfsdemon", "Tester" },
        {"Vistro", "Tester" },
        {"arctrie", "Tester" },
        {"lucid", "Tester" },
        {"masondoesxd", "Tester" },
        {"Sl4bs", "Tester" },
        {"TheGreatAqua", "Tester" },
        {"WaterMan", "Tester" },
        {"xtreme", "Tester" },
        {"Aspire", "Tester" },
        {"itzPX", "Tester" },
        {"AGoofyGoose", "Tester" },
        {"AllMightyMonk", "Tester" },
        {"DumDum", "Tester" },
        {"embee", "Tester" },
        {"EmperorPop", "Tester" },
        {"eyeoftheseer", "Tester" },
        {"I drift like Gojo", "Tester" },
        {"JulyDog", "Tester" },
        {"Medievalz", "Tester" },
        {"Micro", "Tester" },
        {"Penny2819", "Tester" },
        {"Pz", "Tester" },
        {"Royradio", "Tester" },
        {"RTXFjp", "Tester" },
        {"swmb", "Tester" },
        {"tehbaconvr", "Tester" },
        {"Violet", "Tester" },
        {"Gobo", "Tester" },
        {"Atlantic", "Tester" },
        {"Cap", "Tester" },
        {"Circuits", "Tester" },
        {"ItzTapu", "Tester" },
        {"munklurvr", "Tester" },
        {"quantum", "Tester" },
        {"rusty", "Tester" },
        {"Hexann", "Tester" },
    };
}