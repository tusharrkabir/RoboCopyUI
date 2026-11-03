using System.IO;
using System.Text.Json;

namespace RoboCopyUI;

public class UserSettings
{
    public int SettingsVersion { get; set; } = 5;
    public string Theme { get; set; } = "Dark";
    public string Language { get; set; } = L.DefaultLanguage;
    public int ThreadCount { get; set; } = 8;
    public bool AutoSelectThreads { get; set; } = true;
    public bool UnbufferedIO { get; set; }
    public bool PreserveTimestamps { get; set; } = true;
    public bool PreservePermissions { get; set; }
    public int RetryCount { get; set; } = 3;
    public int WaitSeconds { get; set; } = 5;
    public bool StartWithWindows { get; set; } = true;
    public bool ShortcutsEnabled { get; set; } = true;
    public bool ShortcutConflictNoticeShown { get; set; }
    public ShortcutPreferences Shortcuts { get; set; } = new();
    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RoboCopyUI", "settings.json");
    public static string? LoadNotice { get; private set; }

    public static UserSettings Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var settings = JsonSerializer.Deserialize<UserSettings>(json) ?? throw new JsonException(L.T("设置为空。"));
        var root = document.RootElement;
        var version = root.TryGetProperty(nameof(SettingsVersion), out var v) ? v.GetInt32() : 0;
        if (version < 2) settings.AutoSelectThreads = true;
        if (version < 3 && settings.RetryCount == 0 && settings.WaitSeconds == 0)
        {
            settings.RetryCount = 3;
            settings.WaitSeconds = 5;
        }
        settings.SettingsVersion = 5;
        if (version < 5)
        {
            settings.ShortcutConflictNoticeShown = false;
            // An existing custom binding takes precedence over the new default.
            if (settings.Shortcuts is { } keys && (keys.PreservePaste == keys.Paste || keys.PreservePaste == keys.PermanentDelete))
                keys.PreservePaste = null;
        }
        settings.Validate();
        return settings;
    }

    public void Validate()
    {
        if (RetryCount is < 0 or > 100) throw new ArgumentException(L.T("失败后重试次数须为 0–100 次，原设置需人工核对。"));
        if (WaitSeconds is < 0 or > 3600) throw new ArgumentException(L.T("重试等待时间须为 0–3600 秒，原设置需人工核对。"));
        if (ThreadCount is < 1 or > 128) throw new ArgumentException(L.T("线程数须为 1–128。"));
        if (Theme is not ("Dark" or "Light" or "Midnight")) throw new ArgumentException(L.T("主题设置无效。"));
        if(!L.Supports(Language))throw new ArgumentException(L.T("不支持所选界面语言。"));
        if(Shortcuts==null)throw new ArgumentException(L.T("快捷键设置为空。"));
        _=new ShortcutMap(Shortcuts);
    }

    public static UserSettings Load() => LoadFrom(SettingsPath);
    public RobocopyOptions TransferOptions(bool move=false, bool fullPreservation=false)=>new(){IsMove=move,FullPreservation=fullPreservation,
        ThreadCount=ThreadCount,AutoSelectThreadCount=AutoSelectThreads,UnbufferedIO=UnbufferedIO,
        PreserveTimestamps=PreserveTimestamps,RetryCount=RetryCount,WaitSeconds=WaitSeconds};
    public static UserSettings LoadFrom(string path)
    {
        LoadNotice = null;
        if (!File.Exists(path)) return new UserSettings();
        try { return Parse(File.ReadAllText(path)); }
        catch (JsonException ex)
        {
            var backup = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".json";
            File.Copy(path, backup, overwrite: false);
            LoadNotice = L.F("设置损坏，已备份至 {0}。本次使用默认值。{1}", backup, ex.Message);
            return new UserSettings();
        }
    }

    public static event Action<UserSettings>? SettingsSaved;
    public void Save(){SaveTo(SettingsPath);SettingsSaved?.Invoke(this);}
    public void SaveTo(string path)
    {
        Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, this, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
