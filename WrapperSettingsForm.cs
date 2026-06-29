using System.Text.Json.Nodes;

namespace BluestacksCfgEditor;

internal sealed class WrapperSettingsForm : Form
{
    private readonly JsonObject _document;
    private readonly Dictionary<string, TextBox> _editors = new(StringComparer.Ordinal);

    internal WrapperSettingsForm(JsonObject document)
    {
        _document = document;
        InitializeComponent();
        LoadValues();
    }

    private void InitializeComponent()
    {
        Text = "Wrapper Settings";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(500, 400);
        ClientSize = new Size(560, 520);
        ShowInTaskbar = false;
        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        TableLayoutPanel fields = new()
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 0,
            Dock = DockStyle.Top,
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        foreach (WrapperSettingDefinition definition in ConfigDefinitions.WrapperSettings)
        {
            AddEditor(fields, definition.Name);
        }

        foreach (WrapperStringSettingDefinition definition in ConfigDefinitions.WrapperStringSettings)
        {
            AddEditor(fields, definition.Name, browseForCursorFile: true);
        }

        Panel scrollPanel = new()
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
        };
        scrollPanel.Controls.Add(fields);
        root.Controls.Add(scrollPanel, 0, 0);

        FlowLayoutPanel buttons = new()
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0, 12, 0, 0),
        };

        Button cancelButton = new()
        {
            Text = "Cancel",
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
        };
        Button applyButton = new()
        {
            Text = "Apply",
            AutoSize = true,
            Margin = new Padding(8, 0, 0, 0),
        };
        applyButton.Click += (_, _) => ApplyValues();
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(applyButton);
        root.Controls.Add(buttons, 0, 1);

        AcceptButton = applyButton;
        CancelButton = cancelButton;
    }

    private void AddEditor(TableLayoutPanel fields, string name, bool browseForCursorFile = false)
    {
        int row = fields.RowCount++;
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.Controls.Add(
            new Label
            {
                Text = name,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 8, 12, 0),
            },
            0,
            row);

        TextBox editor = new()
        {
            Dock = DockStyle.Top,
            Margin = new Padding(0, 4, 0, 0),
        };

        if (browseForCursorFile)
        {
            TableLayoutPanel pathControls = new()
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                Dock = DockStyle.Top,
                Margin = new Padding(0, 4, 0, 0),
            };
            pathControls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pathControls.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            editor.Margin = new Padding(0);
            Button browseButton = new()
            {
                Text = "...",
                AutoSize = true,
                Margin = new Padding(6, 0, 0, 0),
            };
            browseButton.Click += (_, _) => BrowseForCursorFile(editor);

            pathControls.Controls.Add(editor, 0, 0);
            pathControls.Controls.Add(browseButton, 1, 0);
            fields.Controls.Add(pathControls, 1, row);
        }
        else
        {
            fields.Controls.Add(editor, 1, row);
        }

        _editors[name] = editor;
    }

    private void BrowseForCursorFile(TextBox editor)
    {
        using OpenFileDialog dialog = new()
        {
            Title = "Select Cursor File",
            Filter = "Cursor files (*.cur;*.ani)|*.cur;*.ani|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        string currentPath = editor.Text.Trim();
        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            string? directory = Path.GetDirectoryName(currentPath);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                dialog.InitialDirectory = directory;
            }

            dialog.FileName = Path.GetFileName(currentPath);
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            editor.Text = dialog.FileName;
        }
    }

    private void LoadValues()
    {
        IReadOnlyDictionary<string, double> values = ConfigService.ReadWrapperSettings(_document);
        foreach (WrapperSettingDefinition definition in ConfigDefinitions.WrapperSettings)
        {
            _editors[definition.Name].Text = ConfigDefinitions.FormatDouble(values[definition.Name]);
        }

        IReadOnlyDictionary<string, string> stringValues = ConfigService.ReadWrapperStringSettings(_document);
        foreach (WrapperStringSettingDefinition definition in ConfigDefinitions.WrapperStringSettings)
        {
            _editors[definition.Name].Text = stringValues[definition.Name];
        }
    }

    private void ApplyValues()
    {
        Dictionary<string, double> values = new(StringComparer.Ordinal);
        Dictionary<string, string> stringValues = new(StringComparer.Ordinal);
        foreach (WrapperSettingDefinition definition in ConfigDefinitions.WrapperSettings)
        {
            string text = _editors[definition.Name].Text.Trim();
            if (text.Length == 0)
            {
                values[definition.Name] = definition.DefaultValue;
                continue;
            }

            if (!ConfigService.TryParseDouble(text, out double parsed))
            {
                MessageBox.Show(
                    this,
                    $"{definition.Name} must be a number using invariant formatting.",
                    "Invalid Wrapper Setting",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                _editors[definition.Name].Focus();
                _editors[definition.Name].SelectAll();
                return;
            }

            values[definition.Name] = parsed;
        }

        foreach (WrapperStringSettingDefinition definition in ConfigDefinitions.WrapperStringSettings)
        {
            stringValues[definition.Name] = _editors[definition.Name].Text.Trim();
        }

        ConfigService.ApplyWrapperSettings(_document, values);
        ConfigService.ApplyWrapperStringSettings(_document, stringValues);
        DialogResult = DialogResult.OK;
        Close();
    }
}
