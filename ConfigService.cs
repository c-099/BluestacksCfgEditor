using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace BluestacksCfgEditor;

internal static class ConfigService
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    internal static JsonObject LoadConfig(string path)
    {
        using FileStream stream = File.OpenRead(path);
        JsonNode? node = JsonNode.Parse(stream);
        return node as JsonObject
            ?? throw new InvalidDataException("The config root must be a JSON object.");
    }

    internal static void SaveConfig(JsonObject document, string path)
    {
        RemoveEmbeddedWrapperSettings(document);
        SaveJsonObject(document, path);
    }

    internal static LiveSaveResult SaveToLive(JsonObject document, string packageName)
    {
        string? validationError = ValidateForLiveSave(document);
        if (validationError is not null)
        {
            throw new InvalidDataException(validationError);
        }

        string livePath = GetLiveConfigPath(packageName);
        string liveFolder = Path.GetDirectoryName(livePath)
            ?? throw new DirectoryNotFoundException("The live config folder could not be resolved.");

        if (!Directory.Exists(liveFolder))
        {
            throw new DirectoryNotFoundException($"The BlueStacks user config folder was not found: {liveFolder}");
        }

        string? backupPath = null;
        if (File.Exists(livePath))
        {
            backupPath = $"{livePath}.bak";
            File.Copy(livePath, backupPath, overwrite: true);
        }

        SaveConfig(document, livePath);
        WriteReloadMarker(livePath);

        return new LiveSaveResult(livePath, backupPath);
    }

    internal static string? ValidateForLiveSave(JsonObject document)
    {
        if (document["MetaData"] is not JsonObject)
        {
            return "Config is missing the MetaData object.";
        }

        if (document["ControlSchemes"] is not JsonArray)
        {
            return "Config is missing the ControlSchemes array.";
        }

        return null;
    }

    internal static string DiscoverBlueStacksDataRoot()
    {
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using RegistryKey? subKey = baseKey.OpenSubKey(@"SOFTWARE\BlueStacks_nxt");
                string? value = subKey?.GetValue("UserDefinedDir") as string;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
            catch
            {
                // Ignore registry view failures and fall back.
            }
        }

        return @"C:\ProgramData\BlueStacks_nxt";
    }

    internal static string GetLiveConfigPath(string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            throw new ArgumentException("The package name is empty.", nameof(packageName));
        }

        return Path.Combine(
            DiscoverBlueStacksDataRoot(),
            "Engine",
            "UserData",
            "InputMapper",
            "UserFiles",
            $"{packageName.Trim()}.cfg");
    }

    internal static string GetWrapperConfigPath()
    {
        return Path.Combine(
            GetBlueStacksUserDataFolder(),
            ConfigDefinitions.WrapperConfigFileName);
    }

    internal static WrapperConfigEnsureResult EnsureWrapperConfigExists()
    {
        string userDataFolder = GetBlueStacksUserDataFolder();
        Directory.CreateDirectory(userDataFolder);

        string wrapperPath = GetWrapperConfigPath();
        if (File.Exists(wrapperPath))
        {
            return new WrapperConfigEnsureResult(wrapperPath, Created: false);
        }

        SaveWrapperSettings(CreateDefaultWrapperSettings(), wrapperPath);
        return new WrapperConfigEnsureResult(wrapperPath, Created: true);
    }

    internal static string GetBlueStacksUserDataFolder()
    {
        return Path.Combine(
            DiscoverBlueStacksDataRoot(),
            "Engine",
            "UserData");
    }

    internal static string DiscoverBlueStacksInstallRoot()
    {
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using RegistryKey? subKey = baseKey.OpenSubKey(@"SOFTWARE\BlueStacks_nxt");
                foreach (string valueName in new[] { "InstallDir", "InstallDir64" })
                {
                    string? value = subKey?.GetValue(valueName) as string;
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }
            catch
            {
                // Ignore registry view failures and fall back.
            }
        }

        return @"C:\Program Files\BlueStacks_nxt";
    }

    internal static string GetInstalledWrapperDllPath()
    {
        return Path.Combine(DiscoverBlueStacksInstallRoot(), "dinput8.dll");
    }

    internal static string GetBundledWrapperDllPath()
    {
        return Path.Combine(AppContext.BaseDirectory, "dinput8.dll");
    }

    internal static bool FilesAreIdentical(string firstPath, string secondPath)
    {
        FileInfo first = new(firstPath);
        FileInfo second = new(secondPath);
        if (first.Length != second.Length)
        {
            return false;
        }

        using FileStream firstStream = File.OpenRead(firstPath);
        using FileStream secondStream = File.OpenRead(secondPath);
        byte[] firstHash = SHA256.HashData(firstStream);
        byte[] secondHash = SHA256.HashData(secondStream);
        return CryptographicOperations.FixedTimeEquals(firstHash, secondHash);
    }

    internal static IReadOnlyList<string> DiscoverLivePackages()
    {
        string userFilesFolder = Path.Combine(
            DiscoverBlueStacksDataRoot(),
            "Engine",
            "UserData",
            "InputMapper",
            "UserFiles");

        if (!Directory.Exists(userFilesFolder))
        {
            return [];
        }

        return Directory.EnumerateFiles(userFilesFolder, "*.cfg", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => Path.GetFileNameWithoutExtension(file.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static JsonObject CreateDefaultWrapperSettings()
    {
        JsonObject wrapper = [];
        EnsureWrapperSettingDefaults(wrapper);
        return wrapper;
    }

    internal static JsonObject LoadWrapperSettings(JsonObject document)
    {
        string wrapperPath = GetWrapperConfigPath();
        JsonObject wrapper;

        if (File.Exists(wrapperPath))
        {
            wrapper = LoadConfig(wrapperPath);
        }
        else if (document[ConfigDefinitions.LegacyWrapperConfigKey] is JsonObject legacyWrapper)
        {
            wrapper = DeserializeJsonObject(ConfigDefinitions.SerializeNode(legacyWrapper));
        }
        else
        {
            wrapper = [];
        }

        EnsureWrapperSettingDefaults(wrapper);
        RemoveEmbeddedWrapperSettings(document);
        return wrapper;
    }

    internal static void SaveWrapperSettings(JsonObject wrapperSettings, string path)
    {
        EnsureWrapperSettingDefaults(wrapperSettings);
        NormalizeWrapperSettings(wrapperSettings);
        File.WriteAllText(path, SerializeDocument(wrapperSettings), Utf8NoBom);
    }

    internal static WrapperSaveResult SaveWrapperSettingsToLive(JsonObject wrapperSettings, string? packageName)
    {
        string wrapperPath = GetWrapperConfigPath();
        string? backupPath = null;
        if (File.Exists(wrapperPath))
        {
            backupPath = $"{wrapperPath}.bak";
            File.Copy(wrapperPath, backupPath, overwrite: true);
        }

        SaveWrapperSettings(wrapperSettings, wrapperPath);

        string? reloadPath = null;
        if (!string.IsNullOrWhiteSpace(packageName))
        {
            reloadPath = WriteReloadMarker(GetLiveConfigPath(packageName));
        }

        return new WrapperSaveResult(wrapperPath, backupPath, reloadPath);
    }

    internal static void RemoveEmbeddedWrapperSettings(JsonObject document)
    {
        document.Remove(ConfigDefinitions.LegacyWrapperConfigKey);
    }

    private static void EnsureWrapperSettingDefaults(JsonObject wrapper)
    {
        foreach (WrapperSettingDefinition definition in ConfigDefinitions.WrapperSettings)
        {
            if (wrapper[definition.Name] is null)
            {
                wrapper[definition.Name] = definition.DefaultValue;
            }
        }

        NormalizeWrapperSettings(wrapper);
    }

    private static void NormalizeWrapperSettings(JsonObject wrapper)
    {
        if (TryGetDouble(wrapper["gMOBASkillAimYBiasMinScale"], out double aimYMinScale) &&
            aimYMinScale >= 0.0)
        {
            wrapper["gMOBASkillAimYBiasMinScale"] = -1.0;
        }

        if (TryGetDouble(wrapper["gDpadDirectionSmoothingFactor"], out double smoothingFactor) &&
            (smoothingFactor < 0.0 || smoothingFactor > 1.0))
        {
            wrapper["gDpadDirectionSmoothingFactor"] = 0.45;
        }
    }

    internal static IReadOnlyDictionary<string, double> ReadWrapperSettings(JsonObject? wrapper)
    {
        Dictionary<string, double> values = new(StringComparer.Ordinal);

        foreach (WrapperSettingDefinition definition in ConfigDefinitions.WrapperSettings)
        {
            if (TryGetDouble(wrapper?[definition.Name], out double actual))
            {
                values[definition.Name] = actual;
            }
            else
            {
                values[definition.Name] = definition.DefaultValue;
            }
        }

        return values;
    }

    internal static void ApplyWrapperSettings(JsonObject wrapper, IReadOnlyDictionary<string, double> values)
    {
        EnsureWrapperSettingDefaults(wrapper);
        foreach ((string key, double value) in values)
        {
            wrapper[key] = value;
        }
    }

    internal static bool TryParseInteger(string text, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    internal static bool TryParseDouble(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value);

    internal static bool TryGetDouble(JsonNode? node, out double value)
    {
        switch (node)
        {
            case JsonValue jsonValue when jsonValue.TryGetValue(out double direct):
                value = direct;
                return true;
            case JsonValue jsonValue when jsonValue.TryGetValue(out int intValue):
                value = intValue;
                return true;
            case JsonValue jsonValue when jsonValue.TryGetValue(out long longValue):
                value = longValue;
                return true;
            case JsonValue jsonValue when jsonValue.TryGetValue(out string? text)
                && !string.IsNullOrWhiteSpace(text)
                && TryParseDouble(text, out double parsed):
                value = parsed;
                return true;
            default:
                value = 0;
                return false;
        }
    }

    internal static string SerializeDocument(JsonObject document)
    {
        string json = ConfigDefinitions.SerializeNode(document);
        return json + Environment.NewLine;
    }

    private static void SaveJsonObject(JsonObject document, string path)
    {
        string tempPath = Path.Combine(
            Path.GetDirectoryName(path) ?? AppContext.BaseDirectory,
            $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(tempPath, SerializeDocument(document), Utf8NoBom);

            if (File.Exists(path))
            {
                File.Replace(tempPath, path, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static JsonObject DeserializeJsonObject(string json)
    {
        JsonNode? node = JsonNode.Parse(json);
        return node as JsonObject
            ?? throw new InvalidDataException("The wrapper config root must be a JSON object.");
    }

    private static string WriteReloadMarker(string livePath)
    {
        string reloadPath = $"{livePath}.reload";
        File.WriteAllText(
            reloadPath,
            DateTime.Now.ToString("O", CultureInfo.InvariantCulture),
            Utf8NoBom);
        return reloadPath;
    }
}

internal sealed record LiveSaveResult(string LivePath, string? BackupPath);

internal sealed record WrapperSaveResult(string WrapperPath, string? BackupPath, string? ReloadPath);

internal sealed record WrapperConfigEnsureResult(string WrapperPath, bool Created);
