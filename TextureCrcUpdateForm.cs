using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BluestacksCfgEditor;

internal sealed class TextureCrcUpdateForm : Form
{
    private const string BrawlStarsPackage = ConfigDefinitions.DefaultPackage;

    private readonly ComboBox _textureSizeComboBox = new();
    private readonly ComboBox _targetImageComboBox = new();
    private readonly NumericUpDown _delayNumericUpDown = new();
    private readonly TextBox _matchColorTextBox = new();
    private readonly TextBox _configPathTextBox = new();
    private readonly TextBox _textureDumpDirTextBox = new();
    private readonly TextBox _wrapperConfigPathTextBox = new();
    private readonly TextBox _probeLogPathTextBox = new();
    private readonly Label _detectedRamLabel = new();
    private readonly Label _candidateSummaryLabel = new();
    private readonly ListBox _candidateListBox = new();
    private readonly Label _statusLabel = new();
    private readonly Button _refreshButton = new();
    private readonly Button _startButton = new();
    private readonly Button _openDumpFolderButton = new();
    private readonly Button _enableDumpingButton = new();
    private readonly Button _disableDumpingButton = new();

    private List<TextureCandidate> _candidates = [];
    private int _detectedRamMb;

    internal TextureCrcUpdateForm(string packageName)
    {
        InitializeComponent();
        LoadDefaults();
        RefreshCandidates();
    }

    private void InitializeComponent()
    {
        Text = "TextureCRC Update";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(720, 560);
        ClientSize = new Size(820, 640);
        ShowInTaskbar = false;

        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        TableLayoutPanel settings = new()
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            RowCount = 0,
            Dock = DockStyle.Top,
        };
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.Controls.Add(settings, 0, 0);

        AddComboRow(settings, "Texture size", _textureSizeComboBox, ["Auto", "4096", "2048"]);
        AddComboRow(settings, "Target image", _targetImageComboBox, ["Auto", "Feet", "Feet2"]);
        AddDelayRow(settings);
        AddTextRow(settings, "Match color", _matchColorTextBox);
        AddPathRow(settings, "Live config", _configPathTextBox, BrowseForConfigFile);
        AddPathRow(settings, "Texture dumps", _textureDumpDirTextBox, BrowseForTextureDumpFolder);
        AddPathRow(settings, "Wrapper config", _wrapperConfigPathTextBox, BrowseForWrapperConfigFile);
        AddTextRow(settings, "Probe log", _probeLogPathTextBox);

        _detectedRamLabel.AutoSize = true;
        _detectedRamLabel.Margin = new Padding(0, 8, 0, 0);
        settings.Controls.Add(_detectedRamLabel, 1, AddRow(settings));
        settings.SetColumnSpan(_detectedRamLabel, 2);

        TableLayoutPanel body = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 12, 0, 0),
        };
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(body, 0, 1);

        _candidateSummaryLabel.AutoSize = true;
        _candidateSummaryLabel.Dock = DockStyle.Fill;
        body.Controls.Add(_candidateSummaryLabel, 0, 0);

        _candidateListBox.Dock = DockStyle.Fill;
        _candidateListBox.IntegralHeight = false;
        body.Controls.Add(_candidateListBox, 0, 1);

        TableLayoutPanel footer = new()
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 2,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 12, 0, 0),
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(footer, 0, 2);

        _statusLabel.AutoSize = false;
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.AutoEllipsis = true;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.Margin = new Padding(0, 4, 8, 0);
        footer.Controls.Add(_statusLabel, 0, 0);
        footer.SetRowSpan(_statusLabel, 2);

        FlowLayoutPanel buttons = new()
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Dock = DockStyle.Fill,
        };
        footer.Controls.Add(buttons, 1, 1);

        Button closeButton = new()
        {
            Text = "Close",
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
        };
        Button helpButton = new()
        {
            Text = "Help",
            AutoSize = true,
            Margin = new Padding(8, 0, 0, 0),
        };
        _startButton.Text = "Start Update";
        _startButton.AutoSize = true;
        _startButton.Margin = new Padding(8, 0, 0, 0);
        _refreshButton.Text = "Refresh";
        _refreshButton.AutoSize = true;
        _refreshButton.Margin = new Padding(8, 0, 0, 0);
        _openDumpFolderButton.Text = "Open Dumps";
        _openDumpFolderButton.AutoSize = true;
        _openDumpFolderButton.Margin = new Padding(8, 0, 0, 0);
        _enableDumpingButton.Text = "Enable Dumping";
        _enableDumpingButton.AutoSize = true;
        _enableDumpingButton.Margin = new Padding(8, 0, 0, 0);
        _disableDumpingButton.Text = "Disable Dumping";
        _disableDumpingButton.AutoSize = true;
        _disableDumpingButton.Margin = new Padding(8, 0, 0, 0);

        _refreshButton.Click += (_, _) => RunUiAction(RefreshCandidates, "Refresh TextureCRC Failed");
        _openDumpFolderButton.Click += (_, _) => RunUiAction(OpenDumpFolder, "Open Texture Dumps Failed");
        _enableDumpingButton.Click += (_, _) => RunUiAction(EnableTextureDumping, "Enable Texture Dumping Failed");
        _disableDumpingButton.Click += (_, _) => RunUiAction(DisableTextureDumping, "Disable Texture Dumping Failed");
        _startButton.Click += async (_, _) => await RunUiActionAsync(StartUpdateAsync, "TextureCRC Update Failed");
        helpButton.Click += (_, _) => ShowHelp();

        buttons.Controls.Add(closeButton);
        buttons.Controls.Add(helpButton);
        buttons.Controls.Add(_startButton);
        buttons.Controls.Add(_refreshButton);
        buttons.Controls.Add(_openDumpFolderButton);
        buttons.Controls.Add(_disableDumpingButton);
        buttons.Controls.Add(_enableDumpingButton);

        CancelButton = closeButton;
    }

    private void LoadDefaults()
    {
        _textureSizeComboBox.SelectedIndex = 0;
        _targetImageComboBox.SelectedIndex = 0;
        _delayNumericUpDown.Value = 700;
        _matchColorTextBox.Text = "0x48E03400";
        _configPathTextBox.Text = ConfigService.GetLiveConfigPath(BrawlStarsPackage);
        _textureDumpDirTextBox.Text = Path.Combine(ConfigService.DiscoverBlueStacksDataRoot(), "Logs", "image");
        _wrapperConfigPathTextBox.Text = ConfigService.GetWrapperConfigPath();
        _probeLogPathTextBox.Text = Path.Combine(ConfigService.GetBlueStacksUserDataFolder(), "dinput8-image-marker-probe.log");
        UpdateRamStatus();
    }

    private static int AddRow(TableLayoutPanel table)
    {
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return row;
    }

    private static void AddLabel(TableLayoutPanel table, string text, int row)
    {
        table.Controls.Add(
            new Label
            {
                Text = text,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 8, 12, 0),
            },
            0,
            row);
    }

    private static void AddTextRow(TableLayoutPanel table, string label, TextBox textBox)
    {
        int row = AddRow(table);
        AddLabel(table, label, row);
        textBox.Dock = DockStyle.Top;
        textBox.Margin = new Padding(0, 4, 0, 0);
        table.Controls.Add(textBox, 1, row);
        table.SetColumnSpan(textBox, 2);
    }

    private static void AddComboRow(TableLayoutPanel table, string label, ComboBox comboBox, string[] values)
    {
        int row = AddRow(table);
        AddLabel(table, label, row);
        comboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        comboBox.Items.AddRange(values);
        comboBox.Dock = DockStyle.Top;
        comboBox.Margin = new Padding(0, 4, 0, 0);
        table.Controls.Add(comboBox, 1, row);
        table.SetColumnSpan(comboBox, 2);
    }

    private void AddDelayRow(TableLayoutPanel table)
    {
        int row = AddRow(table);
        AddLabel(table, "Delay ms", row);
        _delayNumericUpDown.Minimum = 100;
        _delayNumericUpDown.Maximum = 10000;
        _delayNumericUpDown.Increment = 100;
        _delayNumericUpDown.Dock = DockStyle.Top;
        _delayNumericUpDown.Margin = new Padding(0, 4, 0, 0);
        table.Controls.Add(_delayNumericUpDown, 1, row);
        table.SetColumnSpan(_delayNumericUpDown, 2);
    }

    private static void AddPathRow(TableLayoutPanel table, string label, TextBox textBox, Action browse)
    {
        int row = AddRow(table);
        AddLabel(table, label, row);
        textBox.Dock = DockStyle.Top;
        textBox.Margin = new Padding(0, 4, 0, 0);
        table.Controls.Add(textBox, 1, row);

        Button browseButton = new()
        {
            Text = "...",
            AutoSize = true,
            Margin = new Padding(6, 4, 0, 0),
        };
        browseButton.Click += (_, _) => browse();
        table.Controls.Add(browseButton, 2, row);
    }

    private void BrowseForConfigFile()
    {
        using OpenFileDialog dialog = new()
        {
            Title = "Select Live Config",
            Filter = "BlueStacks config (*.cfg)|*.cfg|JSON files (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        ConfigureInitialFileDialogDirectory(dialog, _configPathTextBox.Text);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _configPathTextBox.Text = dialog.FileName;
        }
    }

    private void BrowseForTextureDumpFolder()
    {
        using FolderBrowserDialog dialog = new()
        {
            Description = "Select texture dump folder",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_textureDumpDirTextBox.Text.Trim())
                ? _textureDumpDirTextBox.Text.Trim()
                : ConfigService.DiscoverBlueStacksDataRoot(),
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _textureDumpDirTextBox.Text = dialog.SelectedPath;
            RefreshCandidates();
        }
    }

    private void BrowseForWrapperConfigFile()
    {
        using OpenFileDialog dialog = new()
        {
            Title = "Select Wrapper Config",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = false,
            Multiselect = false,
        };
        ConfigureInitialFileDialogDirectory(dialog, _wrapperConfigPathTextBox.Text);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _wrapperConfigPathTextBox.Text = dialog.FileName;
        }
    }

    private static void ConfigureInitialFileDialogDirectory(FileDialog dialog, string currentPath)
    {
        string trimmed = currentPath.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        string? directory = Path.GetDirectoryName(trimmed);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            dialog.InitialDirectory = directory;
        }

        dialog.FileName = Path.GetFileName(trimmed);
    }

    private void RefreshCandidates()
    {
        UpdateRamStatus();
        string textureDumpDir = _textureDumpDirTextBox.Text.Trim();
        int textureSize = GetSelectedTextureSize();
        _candidateListBox.Items.Clear();
        _candidates = [];

        if (!Directory.Exists(textureDumpDir))
        {
            _candidateSummaryLabel.Text = $"No dump folder found for {textureSize}x{textureSize}";
            _statusLabel.Text = $"Texture dump folder not found: {textureDumpDir}";
            return;
        }

        _candidates = DiscoverTextureCandidates(textureDumpDir, textureSize);
        foreach (TextureCandidate candidate in _candidates)
        {
            _candidateListBox.Items.Add($"{candidate.Crc}  {candidate.SourceLabel}  {candidate.FileName}");
        }

        string targetImageId = GetTargetImageId(textureSize);
        _candidateSummaryLabel.Text = $"{_candidates.Count} candidates for {textureSize}x{textureSize}; target image {targetImageId}";
        _statusLabel.Text = _candidates.Count == 0
            ? $"No {textureSize}x{textureSize} CRC_0x*.png files found."
            : "Candidates refreshed.";
    }

    private async Task StartUpdateAsync()
    {
        string configPath = _configPathTextBox.Text.Trim();
        string textureDumpDir = _textureDumpDirTextBox.Text.Trim();
        string wrapperConfigPath = _wrapperConfigPathTextBox.Text.Trim();
        string probeLogPath = _probeLogPathTextBox.Text.Trim();
        string matchColor = _matchColorTextBox.Text.Trim();
        int delayMs = (int)_delayNumericUpDown.Value;
        int textureSize = GetSelectedTextureSize();
        string targetImageId = GetTargetImageId(textureSize);

        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException("Live config was not found.", configPath);
        }

        if (!Directory.Exists(textureDumpDir))
        {
            throw new DirectoryNotFoundException($"Texture dump folder was not found: {textureDumpDir}");
        }

        if (wrapperConfigPath.Length == 0)
        {
            throw new InvalidDataException("Wrapper config path is empty.");
        }

        if (probeLogPath.Length == 0)
        {
            throw new InvalidDataException("Probe log path is empty.");
        }

        if (matchColor.Length == 0)
        {
            throw new InvalidDataException("Match color is empty.");
        }

        _candidates = DiscoverTextureCandidates(textureDumpDir, textureSize);
        if (_candidates.Count == 0)
        {
            throw new InvalidDataException($"No {textureSize}x{textureSize} dumped textures were found in {textureDumpDir}");
        }

        DialogResult confirm = MessageBox.Show(
            this,
            $"Package: {BrawlStarsPackage}\n" +
            $"Target image: {targetImageId}\n" +
            $"Texture size: {textureSize}x{textureSize}\n" +
            $"Candidates: {_candidates.Count}\n" +
            $"Delay: {delayMs} ms\n" +
            $"Detected RAM: {_detectedRamMb} MB\n\n" +
            "The live config will be backed up before the probe starts.",
            "Start TextureCRC Update",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (confirm != DialogResult.OK)
        {
            return;
        }

        SetRunning(true);
        Cursor? previousCursor = Cursor.Current;
        Cursor.Current = Cursors.WaitCursor;

        string configBackupPath = $"{configPath}.probe-backup.{DateTime.Now:yyyyMMdd-HHmmss}";
        string? wrapperBackupText = File.Exists(wrapperConfigPath)
            ? await File.ReadAllTextAsync(wrapperConfigPath)
            : null;

        try
        {
            File.Copy(configPath, configBackupPath, overwrite: true);
            EnableWrapperProbe(wrapperConfigPath, matchColor, probeLogPath);
            if (File.Exists(probeLogPath))
            {
                File.Delete(probeLogPath);
            }

            for (int i = 0; i < _candidates.Count; i++)
            {
                TextureCandidate candidate = _candidates[i];
                _statusLabel.Text = $"Testing {i + 1} of {_candidates.Count}: {candidate.Crc} ({candidate.SourceLabel})";
                await File.AppendAllTextAsync(
                    probeLogPath,
                    $"=== candidate {candidate.Crc} {candidate.SourceLabel} {textureSize}x{textureSize} {candidate.FileName} ==={Environment.NewLine}");

                SetCustomSchemeImageTextureCrc(configPath, candidate.Crc, targetImageId);
                TouchReloadMarker(configPath);
                await Task.Delay(delayMs);

                if (!File.Exists(probeLogPath))
                {
                    continue;
                }

                string logText = await File.ReadAllTextAsync(probeLogPath);
                string? matchLine = logText
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(line => line.StartsWith("MATCH ", StringComparison.Ordinal));

                if (matchLine is not null)
                {
                    TextureCoordinate? runtimeCoordinate = TryParseRuntimeUv(matchLine);
                    if (runtimeCoordinate is not null)
                    {
                        SetCustomSchemeImageTextureCrc(configPath, candidate.Crc, targetImageId, runtimeCoordinate);
                    }

                    RestoreWrapperProbeSettings(wrapperConfigPath, wrapperBackupText);
                    TouchReloadMarker(configPath);
                    _statusLabel.Text = $"Match found: {candidate.Crc}";
                    string coordinateText = runtimeCoordinate is null
                        ? "TextureCoord: not logged by wrapper"
                        : $"TextureCoord: [{runtimeCoordinate.U.ToString("0.##########", CultureInfo.InvariantCulture)}, {runtimeCoordinate.V.ToString("0.##########", CultureInfo.InvariantCulture)}]";
                    MessageBox.Show(
                        this,
                        $"MATCH found.\n\nImageId: {targetImageId}\nTextureCRC: {candidate.Crc}\nCRC source: {candidate.SourceLabel}\n{coordinateText}\nTexture: {candidate.FileName}\n\nThe live config was left on the matching candidate.\nBackup: {configBackupPath}",
                        "TextureCRC Update",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }
            }

            File.Copy(configBackupPath, configPath, overwrite: true);
            RestoreWrapperProbeSettings(wrapperConfigPath, wrapperBackupText);
            TouchReloadMarker(configPath);
            _statusLabel.Text = "No match found; live config restored.";
            MessageBox.Show(
                this,
                $"No match found among {_candidates.Count} {textureSize}x{textureSize} textures.\n\nThe live config was restored from:\n{configBackupPath}",
                "TextureCRC Update",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch
        {
            try
            {
                if (File.Exists(configBackupPath))
                {
                    File.Copy(configBackupPath, configPath, overwrite: true);
                    TouchReloadMarker(configPath);
                }

                RestoreWrapperProbeSettings(wrapperConfigPath, wrapperBackupText);
            }
            catch
            {
                // Keep the original failure as the actionable error.
            }

            throw;
        }
        finally
        {
            Cursor.Current = previousCursor;
            SetRunning(false);
        }
    }

    private void OpenDumpFolder()
    {
        string textureDumpDir = _textureDumpDirTextBox.Text.Trim();
        if (!Directory.Exists(textureDumpDir))
        {
            throw new DirectoryNotFoundException($"Texture dump folder was not found: {textureDumpDir}");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = textureDumpDir,
            UseShellExecute = true,
        });
    }

    private void EnableTextureDumping()
    {
        string confPath = GetBlueStacksConfPath();
        int textureSize = GetSelectedTextureSize();
        string backupPath = UpdateBlueStacksConf(
            confPath,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["bst.image_dump_pkg"] = BrawlStarsPackage,
                ["bst.imgd_min_tex_size"] = textureSize.ToString(CultureInfo.InvariantCulture),
                ["bst.imgd_use_two_texcrc"] = "1",
            });

        _statusLabel.Text = $"Enabled Brawl Stars texture dumping at {textureSize}x{textureSize}. Backup: {Path.GetFileName(backupPath)}";
        MessageBox.Show(
            this,
            $"Enabled Brawl Stars texture dumping in:\n{confPath}\n\n" +
            $"Minimum texture size: {textureSize}x{textureSize}\n" +
            $"Backup: {backupPath}\n\n" +
            "If BlueStacks is already running, recreate the game/render context or restart BlueStacks before collecting dumps.",
            "Texture Dumping Enabled",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void DisableTextureDumping()
    {
        string confPath = GetBlueStacksConfPath();
        string backupPath = UpdateBlueStacksConf(
            confPath,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["bst.image_dump_pkg"] = string.Empty,
            });

        _statusLabel.Text = $"Disabled Brawl Stars texture dumping. Backup: {Path.GetFileName(backupPath)}";
        MessageBox.Show(
            this,
            $"Disabled texture dumping by clearing bst.image_dump_pkg in:\n{confPath}\n\n" +
            $"Backup: {backupPath}\n\n" +
            "If BlueStacks is already running, recreate the game/render context or restart BlueStacks for the setting to be reloaded.",
            "Texture Dumping Disabled",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void ShowHelp()
    {
        MessageBox.Show(
            this,
            "1. Use Enable Dumping, then restart or recreate the BlueStacks render context so bluestacks.conf is reloaded.\n\n" +
            "2. Launch Brawl Stars and wait until the main screen is fully loaded. The texture dump file should appear in Logs\\image after the main screen textures load.\n\n" +
            "3. Use Disable Dumping after the texture file is present, then restart the game.\n\n" +
            "4. Load into training mode so the marker circle is drawn with the correct color for the wrapper probe.\n\n" +
            "5. Keep Texture size on Auto unless you want to force 2048 or 4096. Auto chooses 4096 when BlueStacks RAM is at least 8192 MB, otherwise 2048.\n\n" +
            "6. Start Update backs up the live config, enables the wrapper marker probe, cycles TextureCRC candidates, and touches the reload marker after each candidate.\n\n" +
            "7. If the wrapper logs a marker match, the live config stays on the matching TextureCRC. If no match is found or an error occurs, the original live config is restored.",
            "TextureCRC Help",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void SetRunning(bool running)
    {
        _startButton.Enabled = !running;
        _refreshButton.Enabled = !running;
        _openDumpFolderButton.Enabled = !running;
        _enableDumpingButton.Enabled = !running;
        _disableDumpingButton.Enabled = !running;
    }

    private int GetSelectedTextureSize()
    {
        string selected = _textureSizeComboBox.SelectedItem?.ToString() ?? "Auto";
        if (selected == "4096")
        {
            return 4096;
        }

        if (selected == "2048")
        {
            return 2048;
        }

        return DetectBlueStacksTextureSize().TextureSize;
    }

    private string GetTargetImageId(int textureSize)
    {
        string selected = _targetImageComboBox.SelectedItem?.ToString() ?? "Auto";
        return selected == "Auto"
            ? textureSize >= 4096 ? "Feet" : "Feet2"
            : selected;
    }

    private void UpdateRamStatus()
    {
        (int textureSize, int ramMb) = DetectBlueStacksTextureSize();
        _detectedRamMb = ramMb;
        _detectedRamLabel.Text = $"Detected RAM: {ramMb} MB; auto texture size: {textureSize}x{textureSize}";
    }

    private static List<TextureCandidate> DiscoverTextureCandidates(string textureDumpDir, int textureSize)
    {
        Regex fileNamePattern = new(
            $@"^CRC_(0x[0-9A-Fa-f]+)(?:_(0x[0-9A-Fa-f]+))?_{textureSize}X{textureSize}\.png$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        return Directory.EnumerateFiles(textureDumpDir, "CRC_0x*.png", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .SelectMany(file =>
            {
                Match match = fileNamePattern.Match(file.Name);
                if (!match.Success)
                {
                    return Enumerable.Empty<TextureCandidate>();
                }

                List<TextureCandidate> candidates = [];
                AddTextureCandidate(candidates, match.Groups[1].Value, "CRC #1", file);
                if (match.Groups[2].Success)
                {
                    AddTextureCandidate(candidates, match.Groups[2].Value, "CRC #2", file);
                }

                return candidates
                    .GroupBy(candidate => candidate.Crc, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First());
            })
            .Where(candidate => candidate.Crc.Length > 2)
            .OrderByDescending(candidate => candidate.Length)
            .ThenBy(candidate => candidate.FileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.SourceLabel, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Crc, StringComparer.Ordinal)
            .ToList();
    }

    private static void AddTextureCandidate(List<TextureCandidate> candidates, string rawCrc, string sourceLabel, FileInfo file)
    {
        string crc = NormalizeCrc(rawCrc);
        if (crc.Length <= 2)
        {
            return;
        }

        candidates.Add(new TextureCandidate(
            crc,
            sourceLabel,
            file.Name,
            file.Length));
    }

    private static string NormalizeCrc(string crc)
    {
        if (!crc.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return "0x" + crc[2..].ToUpperInvariant();
    }

    private static (int TextureSize, int RamMb) DetectBlueStacksTextureSize()
    {
        int ramMb = 0;
        string confPath = Path.Combine(ConfigService.DiscoverBlueStacksDataRoot(), "bluestacks.conf");
        if (File.Exists(confPath))
        {
            foreach (string line in File.ReadLines(confPath))
            {
                int equals = line.IndexOf('=');
                if (equals <= 0)
                {
                    continue;
                }

                string key = line[..equals].Trim();
                if (!key.Equals("bst.fresh_cpu_ram", StringComparison.OrdinalIgnoreCase) &&
                    !key.EndsWith(".ram", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string valueText = line[(equals + 1)..].Trim().Trim('"');
                if (int.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    ramMb = Math.Max(ramMb, parsed);
                }
            }
        }

        return (ramMb >= 8192 ? 4096 : 2048, ramMb);
    }

    private static string GetBlueStacksConfPath() =>
        Path.Combine(ConfigService.DiscoverBlueStacksDataRoot(), "bluestacks.conf");

    private static string UpdateBlueStacksConf(string confPath, IReadOnlyDictionary<string, string> updates)
    {
        if (!File.Exists(confPath))
        {
            throw new FileNotFoundException("BlueStacks config was not found.", confPath);
        }

        string backupPath = $"{confPath}.texturecrc-backup.{DateTime.Now:yyyyMMdd-HHmmss}";
        File.Copy(confPath, backupPath, overwrite: true);

        List<string> lines = File.ReadAllLines(confPath).ToList();
        HashSet<string> applied = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            int equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            string key = line[..equals].Trim();
            if (!updates.TryGetValue(key, out string? value))
            {
                continue;
            }

            lines[i] = FormatBlueStacksConfLine(key, value);
            applied.Add(key);
        }

        foreach ((string key, string value) in updates)
        {
            if (!applied.Contains(key))
            {
                lines.Add(FormatBlueStacksConfLine(key, value));
            }
        }

        File.WriteAllLines(confPath, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return backupPath;
    }

    private static string FormatBlueStacksConfLine(string key, string value) =>
        $"{key}=\"{EscapeBlueStacksConfValue(value)}\"";

    private static string EscapeBlueStacksConfValue(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static void EnableWrapperProbe(string wrapperConfigPath, string matchColor, string probeLogPath)
    {
        string? wrapperConfigDir = Path.GetDirectoryName(wrapperConfigPath);
        if (!string.IsNullOrWhiteSpace(wrapperConfigDir))
        {
            Directory.CreateDirectory(wrapperConfigDir);
        }

        JsonObject wrapper = File.Exists(wrapperConfigPath)
            ? ConfigService.LoadConfig(wrapperConfigPath)
            : ConfigService.CreateDefaultWrapperSettings();

        wrapper["gImageMarkerProbeEnabled"] = 1;
        wrapper["gImageMarkerProbeColor"] = matchColor;
        wrapper["gImageMarkerProbeLogPath"] = probeLogPath;
        ConfigService.SaveWrapperSettings(wrapper, wrapperConfigPath);
    }

    private static void RestoreWrapperProbeSettings(string wrapperConfigPath, string? wrapperBackupText)
    {
        if (wrapperBackupText is not null)
        {
            File.WriteAllText(wrapperConfigPath, wrapperBackupText);
            return;
        }

        JsonObject wrapper = ConfigService.CreateDefaultWrapperSettings();
        wrapper["gImageMarkerProbeEnabled"] = 0;
        ConfigService.SaveWrapperSettings(wrapper, wrapperConfigPath);
    }

    private static TextureCoordinate? TryParseRuntimeUv(string matchLine)
    {
        Match match = Regex.Match(
            matchLine,
            @"\bbestUv=\((?<u>-?\d+(?:\.\d+)?),\s*(?<v>-?\d+(?:\.\d+)?)\)",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        if (!double.TryParse(match.Groups["u"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double u) ||
            !double.TryParse(match.Groups["v"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
        {
            return null;
        }

        if (!double.IsFinite(u) || !double.IsFinite(v) || u < 0 || u > 1 || v < 0 || v > 1)
        {
            return null;
        }

        return new TextureCoordinate(u, v);
    }

    private static void SetCustomSchemeImageTextureCrc(
        string configPath,
        string textureCrc,
        string imageId,
        TextureCoordinate? textureCoordinate = null)
    {
        JsonObject document = ConfigService.LoadConfig(configPath);
        int changed = 0;

        if (document["ControlSchemes"] is JsonArray schemes)
        {
            foreach (JsonNode? schemeNode in schemes)
            {
                if (schemeNode is not JsonObject scheme || IsBlueStacksDefaultScheme(scheme))
                {
                    continue;
                }

                string feet2Crc = GetExistingImageTextureCrc(scheme, "Feet2") ?? "0x7D021CAA";
                string feetCrc = GetExistingImageTextureCrc(scheme, "Feet") ?? "0xF5BBA9CA";
                TextureCoordinate feet2Coord = GetExistingImageTextureCoord(scheme, "Feet2") ?? DefaultBrawlStarsTextureCoordinate();
                TextureCoordinate feetCoord = GetExistingImageTextureCoord(scheme, "Feet") ?? DefaultBrawlStarsTextureCoordinate();
                if (string.Equals(imageId, "Feet2", StringComparison.Ordinal))
                {
                    feet2Crc = textureCrc;
                    if (textureCoordinate is not null)
                    {
                        feet2Coord = textureCoordinate;
                    }
                }
                else if (string.Equals(imageId, "Feet", StringComparison.Ordinal))
                {
                    feetCrc = textureCrc;
                    if (textureCoordinate is not null)
                    {
                        feetCoord = textureCoordinate;
                    }
                }
                else
                {
                    throw new InvalidDataException($"Unsupported TextureCRC image id: {imageId}");
                }

                scheme["Images"] = CreateBrawlStarsImagesArray(feet2Crc, feetCrc, feet2Coord, feetCoord);
                changed++;
            }
        }

        if (changed == 0)
        {
            throw new InvalidDataException($"No custom control schemes were found to update in {configPath}");
        }

        ConfigService.SaveConfig(document, configPath);
    }

    private static bool IsBlueStacksDefaultScheme(JsonObject scheme)
    {
        string? name = scheme["Name"]?.GetValue<string?>();
        return string.Equals(name, "Standard", StringComparison.Ordinal) ||
               string.Equals(name, "Smart", StringComparison.Ordinal);
    }

    private static string? GetExistingImageTextureCrc(JsonObject scheme, string imageId)
    {
        if (scheme["Images"] is not JsonArray images)
        {
            return null;
        }

        foreach (JsonNode? imageNode in images)
        {
            if (imageNode is JsonObject image &&
                string.Equals(image["ImageId"]?.GetValue<string?>(), imageId, StringComparison.Ordinal))
            {
                return image["TextureCRC"]?.GetValue<string?>();
            }
        }

        return null;
    }

    private static TextureCoordinate? GetExistingImageTextureCoord(JsonObject scheme, string imageId)
    {
        if (scheme["Images"] is not JsonArray images)
        {
            return null;
        }

        foreach (JsonNode? imageNode in images)
        {
            if (imageNode is not JsonObject image ||
                !string.Equals(image["ImageId"]?.GetValue<string?>(), imageId, StringComparison.Ordinal) ||
                image["TextureCoord"] is not JsonArray coord ||
                coord.Count < 2)
            {
                continue;
            }

            double? u = coord[0]?.GetValue<double?>();
            double? v = coord[1]?.GetValue<double?>();
            if (u is not null && v is not null)
            {
                return new TextureCoordinate(u.Value, v.Value);
            }
        }

        return null;
    }

    private static TextureCoordinate DefaultBrawlStarsTextureCoordinate() =>
        new(0.6453857422, 0.5466308594);

    private static JsonArray CreateBrawlStarsImagesArray(
        string feet2Crc,
        string feetCrc,
        TextureCoordinate feet2Coord,
        TextureCoordinate feetCoord) =>
    [
        CreateBrawlStarsImage("Feet2", feet2Crc, feet2Coord),
        CreateBrawlStarsImage("Feet", feetCrc, feetCoord),
    ];

    private static JsonObject CreateBrawlStarsImage(string imageId, string textureCrc, TextureCoordinate textureCoordinate) =>
        new()
        {
            ["ImageId"] = imageId,
            ["ImageType"] = "Version 1",
            ["DetectColor"] = true,
            ["MatchColor"] = "0x48E03400",
            ["TextureCRC"] = textureCrc,
            ["TextureIndex"] = 1,
            ["VertexIndex"] = 0,
            ["TextureCoord"] =
                new JsonArray
                {
                    textureCoordinate.U,
                    textureCoordinate.V,
                },
            ["VertexRect"] = new JsonArray(),
        };

    private static void TouchReloadMarker(string configPath)
    {
        File.WriteAllText(
            $"{configPath}.reload",
            DateTime.Now.ToString("O", CultureInfo.InvariantCulture));
    }

    private void RunUiAction(Action action, string title)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            ErrorLogger.ShowUnexpectedError(this, title, ex);
        }
    }

    private async Task RunUiActionAsync(Func<Task> action, string title)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            ErrorLogger.ShowUnexpectedError(this, title, ex);
        }
    }

    private sealed record TextureCandidate(string Crc, string SourceLabel, string FileName, long Length);

    private sealed record TextureCoordinate(double U, double V);
}
