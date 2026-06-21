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
            int row = fields.RowCount++;
            fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            fields.Controls.Add(
                new Label
                {
                    Text = definition.Name,
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
            fields.Controls.Add(editor, 1, row);
            _editors[definition.Name] = editor;
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

    private void LoadValues()
    {
        IReadOnlyDictionary<string, double> values = ConfigService.ReadWrapperSettings(_document);
        foreach (WrapperSettingDefinition definition in ConfigDefinitions.WrapperSettings)
        {
            _editors[definition.Name].Text = ConfigDefinitions.FormatDouble(values[definition.Name]);
        }
    }

    private void ApplyValues()
    {
        Dictionary<string, double> values = new(StringComparer.Ordinal);
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

        ConfigService.ApplyWrapperSettings(_document, values);
        DialogResult = DialogResult.OK;
        Close();
    }
}
