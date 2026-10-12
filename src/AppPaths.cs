using System.Text.Json;

namespace DroidSwitcher;
internal record ToolSettings(string? DataDirectory=null,string? FactoryHome=null,string? DroidPath=null,string? DesktopPath=null);
internal static class AppPaths
{
    static readonly ToolSettings Settings=Load();
    static ToolSettings Load() {var path=Path.Combine(AppContext.BaseDirectory,"settings.json"); return File.Exists(path)?JsonSerializer.Deserialize<ToolSettings>(File.ReadAllText(path)) ?? new():new();}
    static string Absolute(string? setting,string fallback) {if(string.IsNullOrWhiteSpace(setting)) return fallback; if(!Path.IsPathRooted(setting)) throw new IOException("settings.json 中的目录必须是绝对路径。"); return Path.GetFullPath(setting);}
    public static string Data=>Absolute(Settings.DataDirectory,Directory.Exists("D:\\Apps\\DroidSwitcher")?"D:\\Apps\\DroidSwitcher\\data":Path.Combine(AppContext.BaseDirectory,"data"));
    public static string FactoryHome=>Absolute(Settings.FactoryHome,Path.Combine(Environment.GetEnvironmentVariable("FACTORY_HOME_OVERRIDE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".factory"));
    public static string Droid=>Absolute(Settings.DroidPath,FindDroid());
    public static string Desktop=>Absolute(Settings.DesktopPath,File.Exists("D:\\Apps\\FactoryDesktop\\Factory\\factory-desktop.exe")?"D:\\Apps\\FactoryDesktop\\Factory\\factory-desktop.exe":Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Factory","factory-desktop.exe"));
    static string FindDroid() {const string installed="D:\\Apps\\FactoryDroid\\droid.exe"; if(File.Exists(installed)) return installed; foreach(var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')) {if(string.IsNullOrWhiteSpace(dir)) continue; var path=Path.Combine(dir.Trim('"'),"droid.exe"); if(File.Exists(path)) return Path.GetFullPath(path);} return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"bin","droid.exe");}
}
