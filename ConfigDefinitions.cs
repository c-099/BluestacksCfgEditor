using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;

namespace BluestacksCfgEditor;

internal enum FieldKind
{
    String,
    Int,
    Float,
    Bool,
    StringList,
}

internal sealed record FieldDefinition(string Name, FieldKind Kind);

internal sealed record WrapperSettingDefinition(string Name, double DefaultValue);

internal sealed record WrapperStringSettingDefinition(string Name, string DefaultValue);

internal static class ConfigDefinitions
{
    internal const string DefaultPackage = "com.supercell.brawlstars";
    internal const string LegacyWrapperConfigKey = "DInputWrapper";
    internal const string WrapperConfigFileName = "dinput8-config.json";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static readonly IReadOnlyList<FieldDefinition> CommonFields =
    [
        new("X", FieldKind.Float),
        new("Y", FieldKind.Float),
        new("ShowOnOverlay", FieldKind.Bool),
        new("StartCondition", FieldKind.String),
        new("EnableCondition", FieldKind.String),
        new("XExpr", FieldKind.String),
        new("YExpr", FieldKind.String),
        new("XOverlayOffset", FieldKind.String),
        new("YOverlayOffset", FieldKind.String),
        new("GuidanceCategory", FieldKind.String),
    ];

    internal static readonly IReadOnlyDictionary<string, IReadOnlyList<FieldDefinition>> TypeFields =
        new Dictionary<string, IReadOnlyList<FieldDefinition>>(StringComparer.Ordinal)
        {
            ["Dpad"] =
            [
                new("KeyUp", FieldKind.String),
                new("KeyDown", FieldKind.String),
                new("KeyLeft", FieldKind.String),
                new("KeyRight", FieldKind.String),
                new("GamepadStick", FieldKind.String),
                new("XRadius", FieldKind.Float),
                new("DeadzoneRadius", FieldKind.Float),
                new("Speed", FieldKind.Float),
                new("ActivationSpeed", FieldKind.Float),
                new("ActivationTime", FieldKind.Int),
            ],
            ["MOBASkill"] =
            [
                new("KeyActivate", FieldKind.String),
                new("KeyCancel", FieldKind.String),
                new("KeyAutocastToggle", FieldKind.String),
                new("OriginX", FieldKind.Float),
                new("OriginY", FieldKind.Float),
                new("CancelX", FieldKind.Float),
                new("CancelY", FieldKind.Float),
                new("YAxisRatio", FieldKind.Float),
                new("XRadius", FieldKind.Float),
                new("DeadZoneRadius", FieldKind.Float),
                new("Speed", FieldKind.Float),
                new("CancelSpeed", FieldKind.Float),
                new("MinSwipeRadius", FieldKind.Float),
                new("NoCancelTime", FieldKind.Int),
                new("MinSkillTime", FieldKind.Int),
                new("MinSkillHoldTime", FieldKind.Int),
                new("IsCancelSkillEnabled", FieldKind.Bool),
                new("AutoAttack", FieldKind.Bool),
                new("StopMOBADpad", FieldKind.Bool),
                new("AutocastEnabled", FieldKind.Bool),
                new("AdvancedMode", FieldKind.Bool),
                new("OriginXExpr", FieldKind.String),
                new("OriginYExpr", FieldKind.String),
                new("CancelXExpr", FieldKind.String),
                new("CancelYExpr", FieldKind.String),
                new("CancelXOverlayOffset", FieldKind.String),
                new("CancelYOverlayOffset", FieldKind.String),
                new("CancelShowOnOverlayExpr", FieldKind.String),
                new("SelectedSkillType", FieldKind.String),
            ],
            ["TapRepeat"] =
            [
                new("Key", FieldKind.String),
                new("Count", FieldKind.Int),
                new("Delay", FieldKind.Int),
                new("RepeatUntilKeyUp", FieldKind.Bool),
            ],
            ["Script"] =
            [
                new("Key", FieldKind.String),
                new("Commands", FieldKind.StringList),
            ],
        };

    internal static readonly IReadOnlyList<WrapperSettingDefinition> WrapperSettings =
    [
        new("gDebugConsoleEnabled", 0.0),
        new("gMOBASkillEdgeThresholdPercent", 25.0),
        new("gMOBASkillMaxAimXBiasPercent", 1.5),
        new("gMOBASkillLeftEdgeXPercent", 9.8),
        new("gMOBASkillRightEdgeXPercent", 90.2),
        new("gMOBASkillAimYBiasScaleStartPercent", 12.0),
        new("gMOBASkillAimYBiasScaleEndPercent", 30.0),
        new("gMOBASkillAimYBiasMinScale", -1.0),
        new("gDpadFullExtensionEnabled", 1.0),
        new("gDpadDirectionSmoothingFactor", 0.45),
        new("gDpadZeroHoldMs", 80.0),
        new("gDpadKeyboardZeroHoldMs", 120.0),
        new("gCustomCursorEnabled", 0.0),
    ];

    internal static readonly IReadOnlyList<WrapperStringSettingDefinition> WrapperStringSettings =
    [
        new("gCustomCursorMousePath", ""),
        new("gCustomCursorMobaPath", ""),
        new("gCustomCursorMobaRightPath", ""),
        new("gCustomCursorBlankPath", ""),
    ];

    internal static string SerializeNode(JsonNode node) =>
        node.ToJsonString(JsonOptions);

    internal static string FormatDouble(double value) =>
        value.ToString("G", CultureInfo.InvariantCulture);
}
