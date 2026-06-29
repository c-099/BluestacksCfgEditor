using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BluestacksCfgEditor;

internal sealed class MainForm : Form
{
    private const int WmClose = 0x0010;
    private const int WmNcLButtonDown = 0x00A1;
    private const int WmSysCommand = 0x0112;
    private const int HtClose = 20;
    private const int ScClose = 0xF060;

    private readonly ComboBox _packageComboBox = new();
    private readonly Label _statusLabel = new();
    private readonly ListBox _schemeListBox = new();
    private readonly ListBox _controlListBox = new();
    private readonly Panel _previewPanel = new();
    private readonly Label _schemeSummaryLabel = new();
    private readonly Label _controlTitleLabel = new();
    private readonly TableLayoutPanel _commonFieldsTable = new();
    private readonly TableLayoutPanel _typeFieldsTable = new();
    private readonly Label _typeInfoLabel = new();
    private readonly TextBox _advancedJsonTextBox = new();
    private readonly Button _applyAdvancedJsonButton = new();
    private readonly Button _openLiveButton = new();
    private readonly Button _saveToLiveButton = new();
    private readonly ContextMenuStrip _schemeContextMenu = new();
    private readonly Dictionary<string, FieldEditor> _commonEditors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FieldEditor> _typeEditors = new(StringComparer.Ordinal);
    private readonly List<PreviewMarker> _previewMarkers = [];

    private JsonObject? _document;
    private JsonObject _wrapperSettings = ConfigService.CreateDefaultWrapperSettings();
    private string? _configPath;
    private string? _wrapperConfigPath;
    private int _selectedSchemeIndex;
    private int _selectedControlIndex;
    private bool _isLoadingUi;
    private bool _isClosing;
    private bool _liveFunctionalityEnabled = true;
    private FieldEditor? _pendingFieldEditor;
    private bool _advancedJsonDirty;
    private RectangleF _previewBounds;

    private bool IsShuttingDown => _isClosing || IsDisposed || Disposing;

    internal MainForm()
    {
        InitializeComponent();
        Shown += (_, _) => BeginInvoke(() =>
        {
            if (!IsShuttingDown)
            {
                PerformStartupEnvironmentChecks();
            }
        });
        BuildFieldTables();
        TryUpdateLivePathStatus();
        LoadEmptyState();
    }

    private void InitializeComponent()
    {
        SuspendLayout();

        Text = "BlueStacks CFG Editor";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1000, 780);
        ClientSize = new Size(1200, 900);

        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(10),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        FlowLayoutPanel toolbar = new()
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
        };
        root.Controls.Add(toolbar, 0, 0);

        Button openConfigButton = CreateToolbarButton("Open Config", (_, _) => RunUiAction(OpenConfig, "Open Failed"));
        Button saveAsButton = CreateToolbarButton("Save As", (_, _) => RunUiAction(SaveAs, "Save Failed"));
        Button wrapperSettingsButton = CreateToolbarButton("Wrapper Settings", (_, _) => RunUiAction(OpenWrapperSettings, "Wrapper Settings Failed"));
        _openLiveButton.Text = "Open Live";
        _openLiveButton.AutoSize = true;
        _openLiveButton.Margin = new Padding(4, 2, 4, 2);
        _openLiveButton.Click += (_, _) => RunUiAction(OpenLiveConfig, "Open Live Failed");
        _saveToLiveButton.Text = "Save To Live";
        _saveToLiveButton.AutoSize = true;
        _saveToLiveButton.Margin = new Padding(4, 2, 4, 2);
        _saveToLiveButton.Click += (_, _) => RunUiAction(SaveToLive, "Live Save Failed");

        toolbar.Controls.Add(openConfigButton);
        toolbar.Controls.Add(saveAsButton);
        toolbar.Controls.Add(wrapperSettingsButton);
        toolbar.Controls.Add(CreateToolbarSeparator());
        toolbar.Controls.Add(new Label { Text = "Package", AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(8, 8, 4, 0) });

        _packageComboBox.Width = 300;
        _packageComboBox.DropDownStyle = ComboBoxStyle.DropDown;
        _packageComboBox.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _packageComboBox.AutoCompleteSource = AutoCompleteSource.ListItems;
        _packageComboBox.Margin = new Padding(0, 4, 8, 0);
        foreach (string packageName in ConfigService.DiscoverLivePackages())
        {
            _packageComboBox.Items.Add(packageName);
        }

        _packageComboBox.Text = _packageComboBox.Items.Count > 0
            ? _packageComboBox.Items[0]?.ToString() ?? string.Empty
            : ConfigDefinitions.DefaultPackage;
        _packageComboBox.SelectionChangeCommitted += (_, _) => TryUpdateLivePathStatus();
        _packageComboBox.Leave += (_, _) =>
        {
            if (!IsShuttingDown)
            {
                TryUpdateLivePathStatus();
            }
        };
        _packageComboBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                TryUpdateLivePathStatus();
            }
        };
        toolbar.Controls.Add(_packageComboBox);
        toolbar.Controls.Add(_openLiveButton);
        toolbar.Controls.Add(_saveToLiveButton);

        _statusLabel.AutoSize = false;
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.AutoEllipsis = true;
        _statusLabel.Margin = new Padding(0, 6, 0, 6);
        _statusLabel.Text = "Open a .cfg or .json file to begin";
        root.Controls.Add(_statusLabel, 0, 1);

        SplitContainer bodySplit = new()
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
        };
        root.Controls.Add(bodySplit, 0, 2);

        TableLayoutPanel sidebar = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(0, 0, 8, 0),
        };
        sidebar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
        sidebar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        sidebar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        bodySplit.Panel1.Controls.Add(sidebar);

        sidebar.Controls.Add(new Label { Text = "Control Schemes", AutoSize = true, Dock = DockStyle.Fill }, 0, 0);

        _schemeListBox.Dock = DockStyle.Fill;
        _schemeListBox.IntegralHeight = false;
        _schemeListBox.SelectedIndexChanged += (_, _) => OnSchemeSelectionChanged();
        _schemeListBox.MouseDoubleClick += (_, e) => RunUiAction(() => SelectSchemeForConfigAndSaveLive(e.Location), "Select Scheme Failed");
        _schemeListBox.MouseDown += (_, e) => SelectSchemeItemUnderMouse(e);
        ConfigureSchemeContextMenu();
        _schemeListBox.ContextMenuStrip = _schemeContextMenu;
        sidebar.Controls.Add(_schemeListBox, 0, 1);

        _schemeSummaryLabel.AutoSize = true;
        _schemeSummaryLabel.Dock = DockStyle.Fill;
        _schemeSummaryLabel.Margin = new Padding(0, 8, 0, 4);
        sidebar.Controls.Add(_schemeSummaryLabel, 0, 2);

        _controlListBox.Dock = DockStyle.Fill;
        _controlListBox.IntegralHeight = false;
        _controlListBox.SelectedIndexChanged += (_, _) => OnControlSelectionChanged();
        sidebar.Controls.Add(_controlListBox, 0, 3);

        sidebar.Controls.Add(new Label { Text = "Preview", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 4) }, 0, 4);

        _previewPanel.Dock = DockStyle.Fill;
        _previewPanel.BackColor = Color.FromArgb(15, 23, 42);
        _previewPanel.Paint += (_, e) => DrawPreview(e.Graphics);
        _previewPanel.MouseClick += (_, e) => OnPreviewClicked(e.Location);
        sidebar.Controls.Add(_previewPanel, 0, 5);

        TableLayoutPanel mainArea = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(8, 0, 0, 0),
        };
        mainArea.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        mainArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        bodySplit.Panel2.Controls.Add(mainArea);

        _controlTitleLabel.AutoSize = true;
        _controlTitleLabel.Font = new Font(Font, FontStyle.Bold);
        mainArea.Controls.Add(_controlTitleLabel, 0, 0);

        TabControl tabs = new()
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 8, 0, 0),
        };
        mainArea.Controls.Add(tabs, 0, 1);

        TabPage propertiesTab = new() { Text = "Properties" };
        TableLayoutPanel propertiesLayout = new()
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 4,
        };
        propertiesLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        propertiesLayout.Controls.Add(CreateSectionLabel("Common"), 0, 0);
        propertiesLayout.Controls.Add(_commonFieldsTable, 0, 1);
        _typeInfoLabel.AutoSize = true;
        _typeInfoLabel.Font = new Font(Font, FontStyle.Bold);
        _typeInfoLabel.Margin = new Padding(0, 18, 0, 4);
        propertiesLayout.Controls.Add(_typeInfoLabel, 0, 2);
        propertiesLayout.Controls.Add(_typeFieldsTable, 0, 3);
        propertiesTab.Controls.Add(WrapInScrollPanel(propertiesLayout, padding: new Padding(10)));
        tabs.TabPages.Add(propertiesTab);

        TabPage advancedTab = new() { Text = "Advanced JSON" };
        TableLayoutPanel advancedLayout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(10),
        };
        advancedLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        advancedLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _advancedJsonTextBox.Dock = DockStyle.Fill;
        _advancedJsonTextBox.Multiline = true;
        _advancedJsonTextBox.AcceptsReturn = true;
        _advancedJsonTextBox.AcceptsTab = true;
        _advancedJsonTextBox.ScrollBars = ScrollBars.Both;
        _advancedJsonTextBox.WordWrap = false;
        _advancedJsonTextBox.TextChanged += (_, _) =>
        {
            if (!_isLoadingUi)
            {
                _advancedJsonDirty = true;
            }
        };
        advancedLayout.Controls.Add(_advancedJsonTextBox, 0, 0);
        _applyAdvancedJsonButton.Text = "Apply JSON To Selected Control";
        _applyAdvancedJsonButton.AutoSize = true;
        _applyAdvancedJsonButton.Anchor = AnchorStyles.Left;
        _applyAdvancedJsonButton.Click += (_, _) => RunUiAction(ApplyAdvancedJson, "Advanced JSON Failed");
        advancedLayout.Controls.Add(_applyAdvancedJsonButton, 0, 1);
        advancedTab.Controls.Add(advancedLayout);
        tabs.TabPages.Add(advancedTab);

        ResumeLayout(performLayout: true);

        Shown += (_, _) => BeginInvoke(() =>
        {
            if (!IsShuttingDown)
            {
                ConfigureInitialSplitterLayout(bodySplit);
            }
        });
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _isClosing = true;
        e.Cancel = false;
        base.OnFormClosing(e);
        e.Cancel = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmClose ||
            (m.Msg == WmNcLButtonDown && (int)m.WParam == HtClose) ||
            (m.Msg == WmSysCommand && ((int)m.WParam & 0xFFF0) == ScClose))
        {
            _isClosing = true;
        }

        base.WndProc(ref m);
    }

    private void PerformStartupEnvironmentChecks()
    {
        if (IsShuttingDown)
        {
            return;
        }

        EnsureWrapperConfigExists();
        if (IsShuttingDown)
        {
            return;
        }

        EnsureWrapperInstalledOrDisableLiveFunctionality();
    }

    private void EnsureWrapperConfigExists()
    {
        try
        {
            WrapperConfigEnsureResult result = ConfigService.EnsureWrapperConfigExists();
            _wrapperConfigPath = result.WrapperPath;
            if (result.Created)
            {
                _wrapperSettings = ConfigService.LoadWrapperSettings(_document ?? []);
                SetStatus($"Created wrapper settings: {result.WrapperPath}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            SetStatus($"Wrapper settings failed: {ConfigService.GetWrapperConfigPath()} ({ex.Message})");
        }
    }

    private void EnsureWrapperInstalledOrDisableLiveFunctionality()
    {
        if (IsShuttingDown)
        {
            return;
        }

        string installedWrapperPath = ConfigService.GetInstalledWrapperDllPath();
        string sourceWrapperPath = ConfigService.GetBundledWrapperDllPath();
        if (!File.Exists(sourceWrapperPath))
        {
            SetStatus($"Live config editing enabled; bundled wrapper source not found at {sourceWrapperPath}");
            return;
        }

        bool installedWrapperExists = File.Exists(installedWrapperPath);
        if (!installedWrapperExists)
        {
            SetStatus($"Live config editing enabled; wrapper is not installed at {installedWrapperPath}");
            return;
        }

        try
        {
            if (ConfigService.FilesAreIdentical(installedWrapperPath, sourceWrapperPath))
            {
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Live config editing enabled; installed wrapper could not be read ({ex.Message})");
            return;
        }

        SetStatus("Live config editing enabled; installed wrapper does not match the bundled wrapper.");
    }

    private void DisableLiveFunctionality(string status)
    {
        if (IsShuttingDown)
        {
            return;
        }

        _liveFunctionalityEnabled = false;
        _openLiveButton.Enabled = false;
        _saveToLiveButton.Enabled = false;
        SetStatus(status);
    }

    private static Button CreateToolbarButton(string text, EventHandler onClick) =>
        new Button()
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(0, 0, 8, 0),
        }.Also(button => button.Click += onClick);

    private static Control CreateToolbarSeparator() =>
        new Label
        {
            BorderStyle = BorderStyle.Fixed3D,
            Width = 2,
            Height = 28,
            Margin = new Padding(8, 0, 8, 0),
        };

    private void ConfigureSchemeContextMenu()
    {
        _schemeContextMenu.Items.Add("Clone", null, (_, _) => RunUiAction(CloneSelectedScheme, "Clone Scheme Failed"));
        _schemeContextMenu.Items.Add("Delete", null, (_, _) => RunUiAction(DeleteSelectedScheme, "Delete Scheme Failed"));
        _schemeContextMenu.Items.Add("Select", null, (_, _) => RunUiAction(SelectCurrentSchemeForConfig, "Select Scheme Failed"));
        _schemeContextMenu.Items.Add("Rename", null, (_, _) => RunUiAction(RenameSelectedScheme, "Rename Scheme Failed"));
        _schemeContextMenu.Opening += (_, e) =>
        {
            bool hasScheme = _schemeListBox.SelectedIndex >= 0 && GetSelectedScheme() is not null;
            foreach (ToolStripItem item in _schemeContextMenu.Items)
            {
                item.Enabled = hasScheme;
            }

            e.Cancel = !hasScheme;
        };
    }

    private static Label CreateSectionLabel(string text) =>
        new()
        {
            Text = text,
            AutoSize = true,
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 4),
        };

    private static Panel WrapInScrollPanel(Control child, Padding? padding = null)
    {
        Panel panel = new()
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = padding ?? Padding.Empty,
        };
        child.Dock = DockStyle.Top;
        panel.Controls.Add(child);
        return panel;
    }

    private void BuildFieldTables()
    {
        ConfigureFieldTable(_commonFieldsTable);
        PopulateFieldTable(_commonFieldsTable, ConfigDefinitions.CommonFields, _commonEditors, rightAlignLabels: true);

        ConfigureFieldTable(_typeFieldsTable);
    }

    private static void ConfigureFieldTable(TableLayoutPanel table)
    {
        table.AutoSize = true;
        table.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        table.ColumnCount = 4;
        table.RowCount = 0;
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        table.GrowStyle = TableLayoutPanelGrowStyle.AddRows;
    }

    private void PopulateFieldTable(
        TableLayoutPanel table,
        IReadOnlyList<FieldDefinition> definitions,
        Dictionary<string, FieldEditor> editors,
        bool rightAlignLabels = false)
    {
        Dictionary<string, FieldDefinition> definitionsByName =
            definitions.ToDictionary(definition => definition.Name, StringComparer.Ordinal);
        HashSet<string> added = new(StringComparer.Ordinal);

        foreach (FieldDefinition definition in definitions)
        {
            if (!added.Add(definition.Name))
            {
                continue;
            }

            int row = AddFieldRow(table);
            string? yCounterpartName = GetYCounterpartName(definition.Name);
            if (yCounterpartName is not null
                && definitionsByName.TryGetValue(yCounterpartName, out FieldDefinition? yDefinition)
                && added.Add(yDefinition.Name))
            {
                editors[definition.Name] = AddFieldEditor(table, definition, CommitStandardField, row, 0, 1, rightAlignLabels);
                editors[yDefinition.Name] = AddFieldEditor(table, yDefinition, CommitStandardField, row, 2, 3, rightAlignLabels);
            }
            else
            {
                editors[definition.Name] = AddFieldEditor(table, definition, CommitStandardField, row, 0, 1, rightAlignLabels, 3);
            }
        }
    }

    private static int AddFieldRow(TableLayoutPanel table)
    {
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return row;
    }

    private static string? GetYCounterpartName(string name)
    {
        int xIndex = name.IndexOf('X');
        return xIndex < 0
            ? null
            : string.Concat(name.AsSpan(0, xIndex), "Y", name.AsSpan(xIndex + 1));
    }

    private FieldEditor AddFieldEditor(
        TableLayoutPanel table,
        FieldDefinition definition,
        Action<FieldEditor> onCommit,
        int row,
        int labelColumn,
        int editorColumn,
        bool rightAlignLabel = false,
        int editorColumnSpan = 1)
    {

        Label label = new()
        {
            Text = definition.Name,
            AutoSize = true,
            Anchor = rightAlignLabel ? AnchorStyles.Right : AnchorStyles.Left,
            TextAlign = rightAlignLabel ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 8, 8, 0),
        };
        table.Controls.Add(label, labelColumn, row);

        Control editorControl;
        switch (definition.Kind)
        {
            case FieldKind.Bool:
                CheckBox checkBox = new()
                {
                    AutoSize = true,
                    Anchor = AnchorStyles.Left,
                    Margin = new Padding(0, 4, 0, 0),
                };
                checkBox.CheckedChanged += (_, _) =>
                {
                    if (!IsShuttingDown)
                    {
                        onCommit(new FieldEditor(definition, checkBox));
                    }
                };
                editorControl = checkBox;
                break;
            case FieldKind.StringList:
                TextBox listTextBox = new()
                {
                    Multiline = true,
                    AcceptsReturn = true,
                    ScrollBars = ScrollBars.Vertical,
                    WordWrap = false,
                    Height = 110,
                    Dock = DockStyle.Fill,
                    Margin = new Padding(0, 4, 0, 0),
                };
                listTextBox.Leave += (_, _) =>
                {
                    if (!IsShuttingDown)
                    {
                        onCommit(new FieldEditor(definition, listTextBox));
                    }
                };
                listTextBox.KeyDown += (_, e) =>
                {
                    if (e.KeyCode == Keys.Enter && !e.Shift)
                    {
                        e.SuppressKeyPress = true;
                        if (!IsShuttingDown)
                        {
                            onCommit(new FieldEditor(definition, listTextBox));
                        }
                    }
                };
                editorControl = listTextBox;
                break;
            default:
                TextBox textBox = new()
                {
                    Dock = DockStyle.Top,
                    Margin = new Padding(0, 4, 0, 0),
                };
                textBox.Leave += (_, _) =>
                {
                    if (!IsShuttingDown)
                    {
                        onCommit(new FieldEditor(definition, textBox));
                    }
                };
                textBox.KeyDown += (_, e) =>
                {
                    if (e.KeyCode == Keys.Enter)
                    {
                        e.SuppressKeyPress = true;
                        if (!IsShuttingDown)
                        {
                            onCommit(new FieldEditor(definition, textBox));
                        }
                    }
                };
                editorControl = textBox;
                break;
        }

        editorControl.Enter += (_, _) =>
        {
            if (!IsShuttingDown)
            {
                _pendingFieldEditor = new FieldEditor(definition, editorControl);
            }
        };
        table.Controls.Add(editorControl, editorColumn, row);
        if (editorColumnSpan > 1)
        {
            table.SetColumnSpan(editorControl, editorColumnSpan);
        }

        return new FieldEditor(definition, editorControl);
    }

    private void OpenConfig()
    {
        using OpenFileDialog dialog = new()
        {
            Title = "Open BlueStacks Config",
            Filter = "CFG / JSON (*.cfg;*.json)|*.cfg;*.json|All Files (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        LoadConfigFromPath(dialog.FileName);
    }

    private void OpenLiveConfig()
    {
        if (!_liveFunctionalityEnabled)
        {
            MessageBox.Show(this, "Live functionality is disabled because the BlueStacks wrapper is not installed.", "Live Disabled", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string livePath;
        try
        {
            livePath = UpdateLivePathStatus();
        }
        catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException)
        {
            MessageBox.Show(this, ex.Message, "Live Config Unavailable", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (!File.Exists(livePath))
        {
            MessageBox.Show(this, $"BlueStacks live config was not found:\n{livePath}", "Live Config Not Found", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        LoadConfigFromPath(livePath);
    }

    private void LoadConfigFromPath(string path)
    {
        JsonObject document = ConfigService.LoadConfig(path);
        _document = document;
        _configPath = path;
        _wrapperSettings = ConfigService.LoadWrapperSettings(document);
        _wrapperConfigPath = ConfigService.GetWrapperConfigPath();
        _selectedSchemeIndex = 0;
        _selectedControlIndex = 0;
        RefreshSchemeList();
        RefreshControlList();
        LoadSelectedControlIntoUi();
        SetStatus($"Loaded {path}; wrapper settings: {_wrapperConfigPath}");
    }

    private void SaveAs()
    {
        if (_document is null)
        {
            MessageBox.Show(this, "Open a config file first.", "Nothing To Save", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!CommitPendingEditorsForSave())
        {
            return;
        }

        using SaveFileDialog dialog = new()
        {
            Title = "Save Config As",
            Filter = "CFG / JSON (*.cfg;*.json)|*.cfg;*.json|All Files (*.*)|*.*",
            DefaultExt = "cfg",
            FileName = _configPath is null ? "edited.cfg" : Path.GetFileName(_configPath),
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        ConfigService.SaveConfig(_document, dialog.FileName);
        _configPath = dialog.FileName;
        SetStatus($"Saved {dialog.FileName}");
    }

    private void SaveToLive()
    {
        if (!_liveFunctionalityEnabled)
        {
            MessageBox.Show(this, "Live functionality is disabled because the BlueStacks wrapper is not installed.", "Live Disabled", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_document is null)
        {
            MessageBox.Show(this, "Open a config file first.", "Nothing To Save", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!CommitPendingEditorsForSave())
        {
            return;
        }

        MarkCurrentSchemeSelectedForLiveSave();

        string packageName = _packageComboBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(packageName))
        {
            MessageBox.Show(this, "Package name is empty.", "Invalid Package", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        string? validationError = ConfigService.ValidateForLiveSave(_document);
        if (validationError is not null)
        {
            MessageBox.Show(this, validationError, "Invalid KMM Config", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        try
        {
            SaveToLive(packageName, "Saved live config");
        }
        catch (DirectoryNotFoundException ex)
        {
            MessageBox.Show(this, ex.Message, "Live Folder Not Found", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (InvalidDataException ex)
        {
            MessageBox.Show(this, ex.Message, "Invalid KMM Config", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SaveToLive(string packageName, string statusPrefix)
    {
        if (_document is null)
        {
            throw new InvalidOperationException("Open a config file first.");
        }

        LiveSaveResult result = ConfigService.SaveToLive(_document, packageName);
        string backupSuffix = result.BackupPath is null ? string.Empty : $" (backup: {Path.GetFileName(result.BackupPath)})";
        _configPath = result.LivePath;
        SetStatus($"{statusPrefix}: {result.LivePath}{backupSuffix}; reload requested");
    }

    private void RefreshSchemeList()
    {
        bool wasLoadingUi = _isLoadingUi;
        _isLoadingUi = true;
        _schemeListBox.BeginUpdate();
        try
        {
            _schemeListBox.Items.Clear();

            JsonArray? schemes = _document?["ControlSchemes"] as JsonArray;
            if (schemes is not null)
            {
                foreach (JsonNode? schemeNode in schemes)
                {
                    if (schemeNode is JsonObject schemeObject)
                    {
                        string label = schemeObject["Name"]?.GetValue<string?>() ?? "Unnamed scheme";
                        if (schemeObject["Selected"]?.GetValue<bool?>() == true)
                        {
                            label += " [selected]";
                        }

                        _schemeListBox.Items.Add(label);
                    }
                }
            }

            if (_schemeListBox.Items.Count > 0)
            {
                _selectedSchemeIndex = Math.Clamp(_selectedSchemeIndex, 0, _schemeListBox.Items.Count - 1);
                _schemeListBox.SelectedIndex = _selectedSchemeIndex;
            }
        }
        finally
        {
            _schemeListBox.EndUpdate();
            _isLoadingUi = wasLoadingUi;
        }

        JsonObject? selectedScheme = GetSelectedScheme();
        if (selectedScheme is null)
        {
            _schemeSummaryLabel.Text = "No schemes";
        }
        else
        {
            int controlCount = (selectedScheme["GameControls"] as JsonArray)?.Count ?? 0;
            string name = selectedScheme["Name"]?.GetValue<string?>() ?? "Unnamed";
            _schemeSummaryLabel.Text = $"Scheme: {name} ({controlCount} controls)";
        }
    }

    private void RefreshControlList()
    {
        bool wasLoadingUi = _isLoadingUi;
        _isLoadingUi = true;
        _controlListBox.BeginUpdate();
        try
        {
            _controlListBox.Items.Clear();

            JsonArray? controls = GetSelectedScheme()?["GameControls"] as JsonArray;
            if (controls is not null)
            {
                foreach (JsonNode? controlNode in controls)
                {
                    if (controlNode is JsonObject controlObject)
                    {
                        _controlListBox.Items.Add(FormatControlLabel(controlObject));
                    }
                }
            }

            if (_controlListBox.Items.Count > 0)
            {
                _selectedControlIndex = Math.Clamp(_selectedControlIndex, 0, _controlListBox.Items.Count - 1);
                _controlListBox.SelectedIndex = _selectedControlIndex;
            }
            else
            {
                _selectedControlIndex = 0;
            }
        }
        finally
        {
            _controlListBox.EndUpdate();
            _isLoadingUi = wasLoadingUi;
        }

        _previewPanel.Invalidate();
    }

    private void LoadSelectedControlIntoUi()
    {
        _isLoadingUi = true;
        try
        {
            JsonObject? control = GetSelectedControl();
            JsonObject? selectedScheme = GetSelectedScheme();
            if (selectedScheme is null)
            {
                _schemeSummaryLabel.Text = "No schemes";
            }
            else
            {
                int controlCount = (selectedScheme["GameControls"] as JsonArray)?.Count ?? 0;
                string schemeName = selectedScheme["Name"]?.GetValue<string?>() ?? "Unnamed";
                _schemeSummaryLabel.Text = $"Scheme: {schemeName} ({controlCount} controls)";
            }

            if (control is null)
            {
                _controlTitleLabel.Text = "No control selected";
                _typeInfoLabel.Text = "No control selected.";
                ClearEditors(_commonEditors.Values);
                ClearEditors(_typeEditors.Values);
                _advancedJsonTextBox.Clear();
                _advancedJsonDirty = false;
                _previewPanel.Invalidate();
                return;
            }

            _controlTitleLabel.Text = FormatControlLabel(control);
            LoadEditors(_commonEditors.Values, control);
            RebuildTypeEditorSection(control["Type"]?.GetValue<string?>());
            LoadEditors(_typeEditors.Values, control);
            _advancedJsonTextBox.Text = ConfigDefinitions.SerializeNode(control);
            _advancedJsonDirty = false;
            _previewPanel.Invalidate();
        }
        finally
        {
            _isLoadingUi = false;
        }
    }

    private void ClearEditors(IEnumerable<FieldEditor> editors)
    {
        foreach (FieldEditor editor in editors)
        {
            SetEditorValue(editor, null);
        }
    }

    private void LoadEditors(IEnumerable<FieldEditor> editors, JsonObject source)
    {
        foreach (FieldEditor editor in editors)
        {
            SetEditorValue(editor, source[editor.Definition.Name]);
        }
    }

    private void SetEditorValue(FieldEditor editor, JsonNode? value)
    {
        switch (editor.Control)
        {
            case CheckBox checkBox:
                checkBox.Checked = value?.GetValue<bool?>() ?? false;
                break;
            case TextBox textBox when editor.Definition.Kind == FieldKind.StringList:
                if (value is JsonArray array)
                {
                    List<string> lines = [];
                    foreach (JsonNode? node in array)
                    {
                        lines.Add(node?.GetValue<string?>() ?? string.Empty);
                    }

                    textBox.Text = string.Join(Environment.NewLine, lines);
                }
                else
                {
                    textBox.Clear();
                }

                break;
            case TextBox textBox when editor.Definition.Kind == FieldKind.Float:
                textBox.Text = ConfigService.TryGetDouble(value, out double number)
                    ? ConfigDefinitions.FormatDouble(number)
                    : string.Empty;
                break;
            case TextBox textBox:
                textBox.Text = value?.ToString() ?? string.Empty;
                break;
        }
    }

    private void RebuildTypeEditorSection(string? controlType)
    {
        _typeFieldsTable.SuspendLayout();
        _typeFieldsTable.Controls.Clear();
        _typeFieldsTable.RowStyles.Clear();
        _typeFieldsTable.RowCount = 0;
        _typeEditors.Clear();

        if (string.IsNullOrWhiteSpace(controlType))
        {
            _typeInfoLabel.Text = "Selected control has no Type.";
            _typeFieldsTable.ResumeLayout();
            return;
        }

        if (!ConfigDefinitions.TypeFields.TryGetValue(controlType, out IReadOnlyList<FieldDefinition>? definitions))
        {
            _typeInfoLabel.Text = $"Unsupported control type: {controlType}. Use Advanced JSON to edit additional properties.";
            _typeFieldsTable.ResumeLayout();
            return;
        }

        _typeInfoLabel.Text = $"Editing fields for {controlType}";
        PopulateFieldTable(_typeFieldsTable, definitions, _typeEditors, rightAlignLabels: true);

        _typeFieldsTable.ResumeLayout(performLayout: true);
    }

    private void CommitStandardField(FieldEditor editor)
    {
        _ = TryCommitStandardField(editor, resetInvalidValue: true);
    }

    private bool TryCommitStandardField(FieldEditor editor, bool resetInvalidValue)
    {
        if (_isLoadingUi || IsShuttingDown)
        {
            return true;
        }

        JsonObject? control = GetSelectedControl();
        if (control is null)
        {
            return true;
        }

        if (!TryCreateNodeFromEditor(editor, out JsonNode? newValue, out string? errorMessage))
        {
            MessageBox.Show(this, errorMessage, "Invalid Field Value", MessageBoxButtons.OK, MessageBoxIcon.Error);
            if (resetInvalidValue)
            {
                _isLoadingUi = true;
                try
                {
                    SetEditorValue(editor, control[editor.Definition.Name]);
                }
                finally
                {
                    _isLoadingUi = false;
                }
            }
            else
            {
                editor.Control.Focus();
            }

            return false;
        }

        control[editor.Definition.Name] = newValue;
        SyncSelectedControlJson(control);
        if (_pendingFieldEditor is FieldEditor pendingEditor &&
            ReferenceEquals(pendingEditor.Control, editor.Control))
        {
            _pendingFieldEditor = null;
        }

        RefreshSelectionDisplay();
        return true;
    }

    private bool CommitPendingFieldEditorForSave()
    {
        if (_pendingFieldEditor is not FieldEditor editor)
        {
            return true;
        }

        if (editor.Control.IsDisposed || !IsCurrentFieldEditor(editor))
        {
            _pendingFieldEditor = null;
            return true;
        }

        return TryCommitStandardField(editor, resetInvalidValue: false);
    }

    private bool CommitPendingEditorsForSave()
    {
        if (!CommitPendingFieldEditorForSave())
        {
            return false;
        }

        return CommitAdvancedJsonForSave();
    }

    private bool CommitAdvancedJsonForSave()
    {
        if (!_advancedJsonDirty || _document is null || IsShuttingDown)
        {
            return true;
        }

        JsonObject? existingControl = GetSelectedControl();
        JsonObject? scheme = GetSelectedScheme();
        JsonArray? controls = scheme?["GameControls"] as JsonArray;
        if (existingControl is null || controls is null)
        {
            _advancedJsonDirty = false;
            return true;
        }

        string currentJson = ConfigDefinitions.SerializeNode(existingControl);
        if (string.Equals(_advancedJsonTextBox.Text.Trim(), currentJson.Trim(), StringComparison.Ordinal))
        {
            _advancedJsonDirty = false;
            return true;
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(_advancedJsonTextBox.Text);
        }
        catch (JsonException ex)
        {
            MessageBox.Show(this, ex.Message, "Invalid Advanced JSON", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _advancedJsonTextBox.Focus();
            return false;
        }

        if (parsed is not JsonObject replacement)
        {
            MessageBox.Show(this, "Advanced control JSON must be a JSON object.", "Invalid Advanced JSON", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _advancedJsonTextBox.Focus();
            return false;
        }

        controls[_selectedControlIndex] = replacement;
        _advancedJsonDirty = false;
        RefreshControlList();
        LoadSelectedControlIntoUi();
        return true;
    }

    private bool IsCurrentFieldEditor(FieldEditor editor)
    {
        return (_commonEditors.TryGetValue(editor.Definition.Name, out FieldEditor? commonEditor) &&
                ReferenceEquals(commonEditor.Control, editor.Control)) ||
            (_typeEditors.TryGetValue(editor.Definition.Name, out FieldEditor? typeEditor) &&
                ReferenceEquals(typeEditor.Control, editor.Control));
    }

    private bool TryCreateNodeFromEditor(FieldEditor editor, out JsonNode? value, out string? errorMessage)
    {
        errorMessage = null;
        switch (editor.Control)
        {
            case CheckBox checkBox:
                value = JsonValue.Create(checkBox.Checked);
                return true;
            case TextBox textBox when editor.Definition.Kind == FieldKind.Int:
                {
                    string text = textBox.Text.Trim();
                    if (text.Length == 0)
                    {
                        value = JsonValue.Create(0);
                        return true;
                    }

                    if (ConfigService.TryParseInteger(text, out int parsed))
                    {
                        value = JsonValue.Create(parsed);
                        return true;
                    }

                    value = null;
                    errorMessage = $"{editor.Definition.Name} must be an integer.";
                    return false;
                }
            case TextBox textBox when editor.Definition.Kind == FieldKind.Float:
                {
                    string text = textBox.Text.Trim();
                    if (text.Length == 0)
                    {
                        value = JsonValue.Create(0d);
                        return true;
                    }

                    if (ConfigService.TryParseDouble(text, out double parsed))
                    {
                        value = JsonValue.Create(parsed);
                        return true;
                    }

                    value = null;
                    errorMessage = $"{editor.Definition.Name} must be a number using invariant formatting.";
                    return false;
                }
            case TextBox textBox when editor.Definition.Kind == FieldKind.StringList:
                {
                    JsonArray array = [];
                    string normalized = textBox.Text.Replace("\r\n", "\n", StringComparison.Ordinal);
                    if (normalized.Length > 0)
                    {
                        foreach (string line in normalized.Split('\n'))
                        {
                            array.Add(line);
                        }
                    }

                    value = array;
                    return true;
                }
            case TextBox textBox:
                value = JsonValue.Create(textBox.Text);
                return true;
            default:
                value = null;
                errorMessage = $"Unsupported editor for {editor.Definition.Name}.";
                return false;
        }
    }

    private void OpenWrapperSettings()
    {
        _wrapperSettings = ConfigService.LoadWrapperSettings(_document ?? []);
        using WrapperSettingsForm dialog = new(_wrapperSettings);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            string packageName = _packageComboBox.Text.Trim();
            WrapperSaveResult result = ConfigService.SaveWrapperSettingsToLive(_wrapperSettings, packageName);
            string backupSuffix = result.BackupPath is null ? string.Empty : $" (backup: {Path.GetFileName(result.BackupPath)})";
            string reloadSuffix = result.ReloadPath is null ? "; reload not requested because no package is selected" : $"; reload requested: {result.ReloadPath}";
            _wrapperConfigPath = result.WrapperPath;
            SetStatus($"Applied wrapper settings: {result.WrapperPath}{backupSuffix}{reloadSuffix}");
        }
    }

    private void ApplyAdvancedJson()
    {
        JsonObject? existingControl = GetSelectedControl();
        JsonObject? scheme = GetSelectedScheme();
        JsonArray? controls = scheme?["GameControls"] as JsonArray;
        if (existingControl is null || controls is null)
        {
            return;
        }

        JsonNode? parsed = JsonNode.Parse(_advancedJsonTextBox.Text);
        if (parsed is not JsonObject replacement)
        {
            MessageBox.Show(this, "Advanced control JSON must be a JSON object.", "Invalid JSON", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        controls[_selectedControlIndex] = replacement;
        _advancedJsonDirty = false;
        RefreshControlList();
        LoadSelectedControlIntoUi();
        SetStatus("Applied advanced JSON to selected control");
    }

    private void CloneSelectedScheme()
    {
        JsonArray? schemes = GetControlSchemes();
        JsonObject? selectedScheme = GetSelectedScheme();
        if (schemes is null || selectedScheme is null)
        {
            MessageBox.Show(this, "Open a config file with at least one control scheme first.", "No Scheme Selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (selectedScheme.DeepClone() is not JsonObject clonedScheme)
        {
            throw new InvalidDataException("The selected control scheme could not be cloned.");
        }

        string originalName = selectedScheme["Name"]?.GetValue<string?>() ?? "Unnamed scheme";
        string clonedName = CreateCopySchemeName(schemes, originalName);
        clonedScheme["Name"] = clonedName;
        clonedScheme["Selected"] = false;

        _selectedSchemeIndex++;
        _selectedControlIndex = 0;
        schemes.Insert(_selectedSchemeIndex, clonedScheme);
        RefreshSchemeList();
        RefreshControlList();
        LoadSelectedControlIntoUi();
        SetStatus($"Cloned scheme: {clonedName}");
    }

    private void DeleteSelectedScheme()
    {
        JsonArray? schemes = GetControlSchemes();
        JsonObject? selectedScheme = GetSelectedScheme();
        if (schemes is null || selectedScheme is null)
        {
            MessageBox.Show(this, "Open a config file with at least one control scheme first.", "No Scheme Selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string schemeName = selectedScheme["Name"]?.GetValue<string?>() ?? "Unnamed scheme";
        DialogResult result = MessageBox.Show(
            this,
            $"Delete control scheme \"{schemeName}\"?\n\nThis removes the scheme from the open config.",
            "Confirm Delete Scheme",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (result != DialogResult.Yes)
        {
            return;
        }

        schemes.RemoveAt(_selectedSchemeIndex);
        _selectedSchemeIndex = schemes.Count > 0
            ? Math.Clamp(_selectedSchemeIndex, 0, schemes.Count - 1)
            : 0;
        _selectedControlIndex = 0;

        RefreshSchemeList();
        RefreshControlList();
        LoadSelectedControlIntoUi();
        SetStatus($"Deleted scheme: {schemeName}");
    }

    private void RenameSelectedScheme()
    {
        JsonObject? selectedScheme = GetSelectedScheme();
        if (selectedScheme is null)
        {
            MessageBox.Show(this, "Open a config file with at least one control scheme first.", "No Scheme Selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string oldName = selectedScheme["Name"]?.GetValue<string?>() ?? "Unnamed scheme";
        string? newName = PromptForSchemeName(oldName);
        if (newName is null)
        {
            return;
        }

        selectedScheme["Name"] = newName;
        RefreshSchemeList();
        RefreshControlList();
        LoadSelectedControlIntoUi();
        SetStatus($"Renamed scheme: {oldName} -> {newName}");
    }

    private void SelectSchemeForConfig(Point location)
    {
        int clickedIndex = _schemeListBox.IndexFromPoint(location);
        if (clickedIndex < 0 || clickedIndex >= _schemeListBox.Items.Count)
        {
            return;
        }

        SelectSchemeForConfig(clickedIndex);
    }

    private void SelectSchemeForConfigAndSaveLive(Point location)
    {
        int clickedIndex = _schemeListBox.IndexFromPoint(location);
        if (clickedIndex < 0 || clickedIndex >= _schemeListBox.Items.Count)
        {
            return;
        }

        if (!CommitPendingEditorsForSave())
        {
            return;
        }

        SelectSchemeForConfig(clickedIndex);

        if (!_liveFunctionalityEnabled)
        {
            SetStatus("Selected scheme for config. Live save skipped because the wrapper is not installed.");
            return;
        }

        if (_document is null)
        {
            return;
        }

        string packageName = _packageComboBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(packageName))
        {
            MessageBox.Show(this, "Package name is empty.", "Invalid Package", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        string? validationError = ConfigService.ValidateForLiveSave(_document);
        if (validationError is not null)
        {
            MessageBox.Show(this, validationError, "Invalid KMM Config", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        string schemeName = GetSelectedScheme()?["Name"]?.GetValue<string?>() ?? "Unnamed scheme";
        try
        {
            SaveToLive(packageName, $"Selected scheme for config and saved live: {schemeName}");
        }
        catch (DirectoryNotFoundException ex)
        {
            MessageBox.Show(this, ex.Message, "Live Folder Not Found", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (InvalidDataException ex)
        {
            MessageBox.Show(this, ex.Message, "Invalid KMM Config", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SelectCurrentSchemeForConfig()
    {
        if (_selectedSchemeIndex < 0 || _selectedSchemeIndex >= _schemeListBox.Items.Count)
        {
            return;
        }

        SelectSchemeForConfig(_selectedSchemeIndex);
    }

    private void SelectSchemeForConfig(int selectedIndex)
    {
        JsonArray? schemes = GetControlSchemes();
        if (schemes is null || selectedIndex < 0 || selectedIndex >= schemes.Count)
        {
            return;
        }

        _selectedSchemeIndex = selectedIndex;
        _selectedControlIndex = 0;
        SetOnlySelectedScheme(selectedIndex);
        RefreshSchemeList();
        RefreshControlList();
        LoadSelectedControlIntoUi();

        string schemeName = GetSelectedScheme()?["Name"]?.GetValue<string?>() ?? "Unnamed scheme";
        SetStatus($"Selected scheme for config: {schemeName}");
    }

    private void SelectSchemeItemUnderMouse(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right)
        {
            return;
        }

        int clickedIndex = _schemeListBox.IndexFromPoint(e.Location);
        if (clickedIndex < 0 || clickedIndex >= _schemeListBox.Items.Count)
        {
            _schemeListBox.ClearSelected();
            return;
        }

        _schemeListBox.SelectedIndex = clickedIndex;
    }

    private void OnSchemeSelectionChanged()
    {
        if (_isLoadingUi || _isClosing || _schemeListBox.SelectedIndex < 0)
        {
            return;
        }

        _selectedSchemeIndex = _schemeListBox.SelectedIndex;
        _selectedControlIndex = 0;
        RefreshSchemeList();
        RefreshControlList();
        LoadSelectedControlIntoUi();
    }

    private void OnControlSelectionChanged()
    {
        if (_isLoadingUi || _isClosing || _controlListBox.SelectedIndex < 0)
        {
            return;
        }

        _selectedControlIndex = _controlListBox.SelectedIndex;
        LoadSelectedControlIntoUi();
    }

    private string? PromptForSchemeName(string currentName)
    {
        using Form dialog = new()
        {
            Text = "Rename Control Scheme",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(420, 116),
        };

        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(12),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        dialog.Controls.Add(layout);

        Label nameLabel = new()
        {
            Text = "Name",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 4, 8, 0),
        };
        layout.Controls.Add(nameLabel, 0, 0);

        TextBox nameTextBox = new()
        {
            Text = currentName,
            Dock = DockStyle.Top,
        };
        layout.Controls.Add(nameTextBox, 1, 0);

        FlowLayoutPanel buttons = new()
        {
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0, 12, 0, 0),
        };
        layout.Controls.Add(buttons, 0, 2);
        layout.SetColumnSpan(buttons, 2);

        Button okButton = new()
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            Enabled = !string.IsNullOrWhiteSpace(currentName),
        };
        Button cancelButton = new()
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            Margin = new Padding(8, 0, 0, 0),
        };

        nameTextBox.TextChanged += (_, _) => okButton.Enabled = !string.IsNullOrWhiteSpace(nameTextBox.Text);
        buttons.Controls.Add(okButton);
        buttons.Controls.Add(cancelButton);
        dialog.AcceptButton = okButton;
        dialog.CancelButton = cancelButton;
        dialog.Shown += (_, _) =>
        {
            nameTextBox.Focus();
            nameTextBox.SelectAll();
        };

        return dialog.ShowDialog(this) == DialogResult.OK
            ? nameTextBox.Text.Trim()
            : null;
    }

    private JsonArray? GetControlSchemes() =>
        _document?["ControlSchemes"] as JsonArray;

    private JsonObject? GetSelectedScheme()
    {
        JsonArray? schemes = GetControlSchemes();
        if (schemes is null || schemes.Count == 0)
        {
            return null;
        }

        if (_selectedSchemeIndex < 0 || _selectedSchemeIndex >= schemes.Count)
        {
            return null;
        }

        return schemes[_selectedSchemeIndex] as JsonObject;
    }

    private JsonObject? GetSelectedControl()
    {
        JsonArray? controls = GetSelectedScheme()?["GameControls"] as JsonArray;
        if (controls is null || controls.Count == 0)
        {
            return null;
        }

        if (_selectedControlIndex < 0 || _selectedControlIndex >= controls.Count)
        {
            return null;
        }

        return controls[_selectedControlIndex] as JsonObject;
    }

    private static string CreateCopySchemeName(JsonArray schemes, string originalName)
    {
        string baseName = string.IsNullOrWhiteSpace(originalName)
            ? "Unnamed scheme"
            : originalName.Trim();
        string candidate = $"{baseName} Copy";
        int suffix = 2;
        while (SchemeNameExists(schemes, candidate))
        {
            candidate = $"{baseName} Copy {suffix}";
            suffix++;
        }

        return candidate;
    }

    private static bool SchemeNameExists(JsonArray schemes, string name)
    {
        foreach (JsonNode? schemeNode in schemes)
        {
            if (schemeNode is JsonObject scheme
                && string.Equals(scheme["Name"]?.GetValue<string?>(), name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private void SetOnlySelectedScheme(int selectedIndex)
    {
        JsonArray? schemes = GetControlSchemes();
        if (schemes is null)
        {
            return;
        }

        for (int i = 0; i < schemes.Count; i++)
        {
            if (schemes[i] is JsonObject scheme)
            {
                scheme["Selected"] = i == selectedIndex;
            }
        }
    }

    private void MarkCurrentSchemeSelectedForLiveSave()
    {
        JsonArray? schemes = GetControlSchemes();
        if (schemes is null || schemes.Count == 0)
        {
            return;
        }

        int selectedIndex = _schemeListBox.SelectedIndex >= 0
            ? _schemeListBox.SelectedIndex
            : _selectedSchemeIndex;
        if (selectedIndex < 0 || selectedIndex >= schemes.Count)
        {
            return;
        }

        _selectedSchemeIndex = selectedIndex;
        SetOnlySelectedScheme(selectedIndex);
        RefreshSchemeList();
    }

    private void RefreshSelectionDisplay()
    {
        JsonObject? control = GetSelectedControl();
        if (control is null)
        {
            return;
        }

        _controlTitleLabel.Text = FormatControlLabel(control);
        if (_selectedControlIndex >= 0 && _selectedControlIndex < _controlListBox.Items.Count)
        {
            _controlListBox.Items[_selectedControlIndex] = FormatControlLabel(control);
        }

        _previewPanel.Invalidate();
    }

    private void SyncSelectedControlJson(JsonObject control)
    {
        bool wasLoadingUi = _isLoadingUi;
        _isLoadingUi = true;
        try
        {
            _advancedJsonTextBox.Text = ConfigDefinitions.SerializeNode(control);
            _advancedJsonDirty = false;
        }
        finally
        {
            _isLoadingUi = wasLoadingUi;
        }
    }

    private string UpdateLivePathStatus()
    {
        string livePath = ConfigService.GetLiveConfigPath(_packageComboBox.Text.Trim());
        SetStatus(File.Exists(livePath) ? $"Live config: {livePath}" : $"Live config not found: {livePath}");
        return livePath;
    }

    private bool TryUpdateLivePathStatus()
    {
        if (IsShuttingDown)
        {
            return false;
        }

        try
        {
            UpdateLivePathStatus();
            return true;
        }
        catch (ArgumentException)
        {
            SetStatus("Enter a package name to use live config features");
            return false;
        }
    }

    private void SetStatus(string text)
    {
        if (!IsShuttingDown)
        {
            _statusLabel.Text = text;
        }
    }

    private void LoadEmptyState()
    {
        _document = null;
        _wrapperSettings = ConfigService.CreateDefaultWrapperSettings();
        _configPath = null;
        _wrapperConfigPath = null;
        _selectedSchemeIndex = 0;
        _selectedControlIndex = 0;
        _schemeListBox.Items.Clear();
        _controlListBox.Items.Clear();
        _schemeSummaryLabel.Text = "No schemes";
        _controlTitleLabel.Text = "No control selected";
        _typeInfoLabel.Text = "No control selected.";
        ClearEditors(_commonEditors.Values);
        ClearEditors(_typeEditors.Values);
        _advancedJsonTextBox.Clear();
        _advancedJsonDirty = false;
        _previewPanel.Invalidate();
    }

    private string FormatControlLabel(JsonObject control)
    {
        string controlType = control["Type"]?.GetValue<string?>() ?? "Control";
        if (control["Guidance"] is JsonObject guidance)
        {
            string? keyActivate = guidance["KeyActivate"]?.GetValue<string?>();
            if (!string.IsNullOrWhiteSpace(keyActivate))
            {
                return $"{controlType} - {keyActivate}";
            }

            string? dpadTitle = guidance["DpadTitle"]?.GetValue<string?>();
            if (!string.IsNullOrWhiteSpace(dpadTitle))
            {
                return $"{controlType} - {dpadTitle}";
            }
        }

        string? topLevelKeyActivate = control["KeyActivate"]?.GetValue<string?>();
        if (!string.IsNullOrWhiteSpace(topLevelKeyActivate))
        {
            return $"{controlType} - {topLevelKeyActivate}";
        }

        string? key = control["Key"]?.GetValue<string?>();
        if (!string.IsNullOrWhiteSpace(key))
        {
            return $"{controlType} - {key}";
        }

        return controlType;
    }

    private void DrawPreview(Graphics graphics)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(_previewPanel.BackColor);
        _previewMarkers.Clear();

        JsonArray? controls = GetSelectedScheme()?["GameControls"] as JsonArray;
        if (controls is null)
        {
            _previewBounds = RectangleF.Empty;
            return;
        }

        float width = Math.Max(_previewPanel.ClientSize.Width, 320);
        float height = Math.Max(_previewPanel.ClientSize.Height, 220);
        const float margin = 12f;
        float innerWidth = width - (margin * 2);
        float innerHeight = Math.Min(height - (margin * 2), innerWidth * 9f / 16f);
        innerWidth = innerHeight * 16f / 9f;
        float offsetX = (width - innerWidth) / 2f;
        float offsetY = (height - innerHeight) / 2f;
        _previewBounds = new RectangleF(offsetX, offsetY, innerWidth, innerHeight);

        using Pen borderPen = new(Color.FromArgb(51, 65, 85), 2);
        using SolidBrush surfaceBrush = new(Color.FromArgb(17, 24, 39));
        graphics.FillRectangle(surfaceBrush, _previewBounds);
        graphics.DrawRectangle(borderPen, _previewBounds.X, _previewBounds.Y, _previewBounds.Width, _previewBounds.Height);

        using Pen gridPen = new(Color.FromArgb(31, 41, 55), 1);
        for (int tick = 1; tick < 10; tick++)
        {
            float x = offsetX + (innerWidth * tick / 10f);
            float y = offsetY + (innerHeight * tick / 10f);
            graphics.DrawLine(gridPen, x, offsetY, x, offsetY + innerHeight);
            graphics.DrawLine(gridPen, offsetX, y, offsetX + innerWidth, y);
        }

        using Font font = new(Font.FontFamily, 8, FontStyle.Regular);
        using Brush captionBrush = new SolidBrush(Color.FromArgb(148, 163, 184));
        graphics.DrawString("0,0", font, captionBrush, offsetX + 8, offsetY + 8);

        SizeF bottomTextSize = graphics.MeasureString("100,100", font);
        graphics.DrawString("100,100", font, captionBrush, offsetX + innerWidth - bottomTextSize.Width - 8, offsetY + innerHeight - bottomTextSize.Height - 8);

        for (int i = 0; i < controls.Count; i++)
        {
            if (controls[i] is not JsonObject control)
            {
                continue;
            }

            double xValue = ConfigService.TryGetDouble(control["X"], out double x) ? x : 0d;
            double yValue = ConfigService.TryGetDouble(control["Y"], out double y) ? y : 0d;
            float canvasX = offsetX + (float)(xValue / 100d * innerWidth);
            float canvasY = offsetY + (float)(yValue / 100d * innerHeight);
            bool selected = i == _selectedControlIndex;
            float radius = selected ? 10f : 7f;
            Color fillColor = selected ? Color.FromArgb(245, 158, 11) : Color.FromArgb(56, 189, 248);
            Color outlineColor = selected ? Color.FromArgb(248, 250, 252) : Color.FromArgb(15, 23, 42);
            RectangleF markerBounds = new(canvasX - radius, canvasY - radius, radius * 2, radius * 2);

            using SolidBrush fillBrush = new(fillColor);
            using Pen markerPen = new(outlineColor, 2);
            graphics.FillEllipse(fillBrush, markerBounds);
            graphics.DrawEllipse(markerPen, markerBounds);

            using Font numberFont = new(Font.FontFamily, 9, FontStyle.Bold);
            using Brush numberBrush = new SolidBrush(Color.FromArgb(229, 231, 235));
            string label = (i + 1).ToString(CultureInfo.InvariantCulture);
            SizeF labelSize = graphics.MeasureString(label, numberFont);
            graphics.DrawString(label, numberFont, numberBrush, canvasX - (labelSize.Width / 2), canvasY - radius - labelSize.Height - 2);

            _previewMarkers.Add(new PreviewMarker(i, markerBounds));
        }
    }

    private void OnPreviewClicked(Point location)
    {
        if (_isClosing || _previewMarkers.Count == 0)
        {
            return;
        }

        PreviewMarker? hit = _previewMarkers
            .Where(marker => Inflate(marker.Bounds, 6f).Contains(location))
            .OrderBy(marker => DistanceSquared(location, marker.Bounds))
            .FirstOrDefault();

        if (hit is null)
        {
            return;
        }

        _selectedControlIndex = hit.Index;
        _controlListBox.SelectedIndex = hit.Index;
        _controlListBox.TopIndex = hit.Index;
        LoadSelectedControlIntoUi();
    }

    private void RunUiAction(Action action, string title)
    {
        if (IsShuttingDown)
        {
            return;
        }

        try
        {
            action();
        }
        catch (JsonException ex)
        {
            if (IsShuttingDown) return;
            MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (InvalidDataException ex)
        {
            if (IsShuttingDown) return;
            MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (UnauthorizedAccessException ex)
        {
            if (IsShuttingDown) return;
            MessageBox.Show(
                this,
                $"Windows denied access to a protected BlueStacks path.\n\n{ex.Message}\n\nRestart BlueStacks CFG Editor as administrator and try again.",
                title,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            if (IsShuttingDown) return;
            ErrorLogger.ShowUnexpectedError(this, title, ex);
        }
    }

    private static RectangleF Inflate(RectangleF rectangle, float amount) =>
        RectangleF.FromLTRB(rectangle.Left - amount, rectangle.Top - amount, rectangle.Right + amount, rectangle.Bottom + amount);

    private static float DistanceSquared(Point point, RectangleF rectangle)
    {
        float centerX = rectangle.Left + (rectangle.Width / 2f);
        float centerY = rectangle.Top + (rectangle.Height / 2f);
        float dx = point.X - centerX;
        float dy = point.Y - centerY;
        return (dx * dx) + (dy * dy);
    }

    private static void ConfigureInitialSplitterLayout(SplitContainer splitContainer)
    {
        if (!splitContainer.IsHandleCreated || splitContainer.Width <= 0)
        {
            return;
        }

        const int preferredPanel1 = 360;
        const int panel1Min = 320;
        const int panel2Min = 550;

        splitContainer.Panel1MinSize = panel1Min;
        splitContainer.Panel2MinSize = panel2Min;

        int maxPanel1 = splitContainer.Width - panel2Min;
        if (maxPanel1 < panel1Min)
        {
            return;
        }

        splitContainer.SplitterDistance = Math.Clamp(preferredPanel1, panel1Min, maxPanel1);
    }
}

internal sealed record FieldEditor(FieldDefinition Definition, Control Control);

internal sealed record PreviewMarker(int Index, RectangleF Bounds);

internal static class ControlExtensions
{
    internal static T Also<T>(this T value, Action<T> configure)
    {
        configure(value);
        return value;
    }
}
