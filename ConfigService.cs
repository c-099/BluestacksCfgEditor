using System.Globalization;
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
            string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            backupPath = $"{livePath}.bak.{timestamp}";
            File.Copy(livePath, backupPath, overwrite: false);
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

    internal static JsonObject GetOrCreateWrapperSettings(JsonObject document)
    {
        JsonObject wrapper = document[ConfigDefinitions.WrapperConfigKey] as JsonObject ?? [];
        document[ConfigDefinitions.WrapperConfigKey] = wrapper;

        foreach (WrapperSettingDefinition definition in ConfigDefinitions.WrapperSettings)
        {
            if (wrapper[definition.Name] is null)
            {
                wrapper[definition.Name] = definition.DefaultValue;
            }
        }

        return wrapper;
    }

    internal static IReadOnlyDictionary<string, double> ReadWrapperSettings(JsonObject? document)
    {
        Dictionary<string, double> values = new(StringComparer.Ordinal);
        JsonObject? wrapper = document?[ConfigDefinitions.WrapperConfigKey] as JsonObject;

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

    internal static void ApplyWrapperSettings(JsonObject document, IReadOnlyDictionary<string, double> values)
    {
        JsonObject wrapper = GetOrCreateWrapperSettings(document);
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

    private static void WriteReloadMarker(string livePath)
    {
        string reloadPath = $"{livePath}.reload";
        File.WriteAllText(
            reloadPath,
            DateTime.Now.ToString("O", CultureInfo.InvariantCulture),
            Utf8NoBom);
    }
}

internal sealed record LiveSaveResult(string LivePath, string? BackupPath);
