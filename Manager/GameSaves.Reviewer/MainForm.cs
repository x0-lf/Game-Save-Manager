using System.ComponentModel;
using System.Diagnostics;

namespace GameSaves.Reviewer
{
    public sealed class MainForm : Form
    {
        private readonly string _databasePath;
        private readonly MappingReviewRepository _repository;

        private readonly BindingList<MappingReviewItem> _items = new();

        private readonly DataGridView _grid = new();
        private readonly TextBox _searchTextBox = new();
        private readonly ComboBox _statusComboBox = new();
        private readonly NumericUpDown _limitNumeric = new();
        private readonly NumericUpDown _priorityNumeric = new();
        private readonly TextBox _reviewNotesTextBox = new();
        private readonly Label _statusLabel = new();

        public MainForm(string databasePath)
        {
            _databasePath = databasePath;
            _repository = new MappingReviewRepository(databasePath);

            Text = "Steam Save Manager - PCGW Mapping Reviewer";
            Width = 1500;
            Height = 900;
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;

            BuildUi();

            Load += (_, _) =>
            {
                _repository.InitializeReviewColumns();
                Reload();
            };

            KeyDown += MainForm_KeyDown;
        }

        private void BuildUi()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 4,
                ColumnCount = 1
            };

            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            Controls.Add(root);

            var topPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                Padding = new Padding(8),
                WrapContents = false
            };

            root.Controls.Add(topPanel, 0, 0);

            topPanel.Controls.Add(new Label
            {
                Text = "Status:",
                AutoSize = true,
                Padding = new Padding(0, 6, 0, 0)
            });

            _statusComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _statusComboBox.Width = 140;
            _statusComboBox.Items.AddRange(new object[]
            {
                "Pending",
                "Approved",
                "Rejected",
                "NeedsFix"
            });
            _statusComboBox.SelectedIndex = 0;
            topPanel.Controls.Add(_statusComboBox);

            topPanel.Controls.Add(new Label
            {
                Text = "Search:",
                AutoSize = true,
                Padding = new Padding(12, 6, 0, 0)
            });

            _searchTextBox.Width = 320;
            topPanel.Controls.Add(_searchTextBox);

            var reloadButton = new Button
            {
                Text = "Reload",
                Width = 90
            };
            reloadButton.Click += (_, _) => Reload();
            topPanel.Controls.Add(reloadButton);

            topPanel.Controls.Add(new Label
            {
                Text = "Limit:",
                AutoSize = true,
                Padding = new Padding(12, 6, 0, 0)
            });

            _limitNumeric.Minimum = 10;
            _limitNumeric.Maximum = 10000;
            _limitNumeric.Value = 1000;
            _limitNumeric.Increment = 100;
            _limitNumeric.Width = 90;
            topPanel.Controls.Add(_limitNumeric);

            topPanel.Controls.Add(new Label
            {
                Text = "Approve priority:",
                AutoSize = true,
                Padding = new Padding(12, 6, 0, 0)
            });

            _priorityNumeric.Minimum = 1;
            _priorityNumeric.Maximum = 1000;
            _priorityNumeric.Value = 40;
            _priorityNumeric.Width = 80;
            topPanel.Controls.Add(_priorityNumeric);

            var approveButton = new Button
            {
                Text = "Approve Selected (A)",
                Width = 160
            };
            approveButton.Click += (_, _) => ApproveSelected();
            topPanel.Controls.Add(approveButton);

            var rejectButton = new Button
            {
                Text = "Reject Selected (R)",
                Width = 150
            };
            rejectButton.Click += (_, _) => RejectSelected();
            topPanel.Controls.Add(rejectButton);

            var needsFixButton = new Button
            {
                Text = "Needs Fix (F)",
                Width = 120
            };
            needsFixButton.Click += (_, _) => NeedsFixSelected();
            topPanel.Controls.Add(needsFixButton);

            var resetButton = new Button
            {
                Text = "Reset Pending",
                Width = 120
            };
            resetButton.Click += (_, _) => ResetSelected();
            topPanel.Controls.Add(resetButton);

            var openSourceButton = new Button
            {
                Text = "Open Source (O)",
                Width = 130
            };
            openSourceButton.Click += (_, _) => OpenSelectedSource();
            topPanel.Controls.Add(openSourceButton);

            ConfigureGrid();
            root.Controls.Add(_grid, 0, 1);

            var notesPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(8)
            };

            notesPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            notesPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            notesPanel.Controls.Add(new Label
            {
                Text = "Review notes for selected action:",
                AutoSize = true
            }, 0, 0);

            _reviewNotesTextBox.Multiline = true;
            _reviewNotesTextBox.ScrollBars = ScrollBars.Vertical;
            _reviewNotesTextBox.Dock = DockStyle.Fill;
            notesPanel.Controls.Add(_reviewNotesTextBox, 0, 1);

            root.Controls.Add(notesPanel, 0, 2);

            _statusLabel.Dock = DockStyle.Fill;
            _statusLabel.Padding = new Padding(8);
            root.Controls.Add(_statusLabel, 0, 3);
        }

        private void ConfigureGrid()
        {
            _grid.Dock = DockStyle.Fill;
            _grid.AutoGenerateColumns = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.MultiSelect = true;
            _grid.ReadOnly = true;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.RowHeadersVisible = false;
            _grid.DataSource = _items;
            _grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText;

            AddTextColumn("Id", nameof(MappingReviewItem.Id), 70);
            AddTextColumn("AppID", nameof(MappingReviewItem.SteamAppId), 90);
            AddTextColumn("Game", nameof(MappingReviewItem.GameName), 240);
            AddTextColumn("Platform", nameof(MappingReviewItem.Platform), 90);
            AddTextColumn("Path", nameof(MappingReviewItem.PathTemplate), 460);
            AddTextColumn("Kind", nameof(MappingReviewItem.PathKind), 80);
            AddTextColumn("Priority", nameof(MappingReviewItem.Priority), 70);
            AddTextColumn("Enabled", nameof(MappingReviewItem.Enabled), 70);
            AddTextColumn("Review", nameof(MappingReviewItem.ReviewStatus), 90);
            AddTextColumn("Source", nameof(MappingReviewItem.SourceName), 170);
            AddTextColumn("URL", nameof(MappingReviewItem.SourceUrl), 300);

            _grid.CellDoubleClick += (_, _) => OpenSelectedSource();
        }

        private void AddTextColumn(
            string header,
            string propertyName,
            int width)
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = header,
                DataPropertyName = propertyName,
                Width = width,
                SortMode = DataGridViewColumnSortMode.Automatic
            });
        }

        private void Reload()
        {
            string status = _statusComboBox.SelectedItem?.ToString() ?? "Pending";
            int limit = Convert.ToInt32(_limitNumeric.Value);
            string? search = _searchTextBox.Text;

            List<MappingReviewItem> rows = _repository.LoadByStatus(
                status,
                limit,
                search);

            _items.Clear();

            foreach (MappingReviewItem row in rows)
                _items.Add(row);

            int pending = _repository.CountByStatus("Pending");
            int approved = _repository.CountByStatus("Approved");
            int rejected = _repository.CountByStatus("Rejected");
            int needsFix = _repository.CountByStatus("NeedsFix");

            _statusLabel.Text =
                $"DB: {_databasePath} | Showing: {_items.Count} | " +
                $"Pending: {pending} | Approved: {approved} | Rejected: {rejected} | NeedsFix: {needsFix}";
        }

        private IReadOnlyList<long> GetSelectedIds()
        {
            return _grid.SelectedRows
                .Cast<DataGridViewRow>()
                .Select(row => row.DataBoundItem)
                .OfType<MappingReviewItem>()
                .Select(item => item.Id)
                .Distinct()
                .ToList();
        }

        private MappingReviewItem? GetFirstSelectedItem()
        {
            return _grid.SelectedRows
                .Cast<DataGridViewRow>()
                .Select(row => row.DataBoundItem)
                .OfType<MappingReviewItem>()
                .FirstOrDefault();
        }

        private void ApproveSelected()
        {
            IReadOnlyList<long> ids = GetSelectedIds();

            if (ids.Count == 0)
                return;

            int priority = Convert.ToInt32(_priorityNumeric.Value);

            _repository.ApproveMappings(
                ids,
                priority,
                _reviewNotesTextBox.Text);

            Reload();
        }

        private void RejectSelected()
        {
            IReadOnlyList<long> ids = GetSelectedIds();

            if (ids.Count == 0)
                return;

            _repository.RejectMappings(
                ids,
                _reviewNotesTextBox.Text);

            Reload();
        }

        private void NeedsFixSelected()
        {
            IReadOnlyList<long> ids = GetSelectedIds();

            if (ids.Count == 0)
                return;

            _repository.MarkNeedsFix(
                ids,
                _reviewNotesTextBox.Text);

            Reload();
        }

        private void ResetSelected()
        {
            IReadOnlyList<long> ids = GetSelectedIds();

            if (ids.Count == 0)
                return;

            _repository.ResetToPending(ids);

            Reload();
        }

        private void OpenSelectedSource()
        {
            MappingReviewItem? item = GetFirstSelectedItem();

            if (item is null || string.IsNullOrWhiteSpace(item.SourceUrl))
                return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = item.SourceUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    ex.Message,
                    "Could not open source URL",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void MainForm_KeyDown(
            object? sender,
            KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.R)
            {
                Reload();
                e.Handled = true;
                return;
            }

            if (e.KeyCode == Keys.A)
            {
                ApproveSelected();
                e.Handled = true;
                return;
            }

            if (e.KeyCode == Keys.R)
            {
                RejectSelected();
                e.Handled = true;
                return;
            }

            if (e.KeyCode == Keys.F)
            {
                NeedsFixSelected();
                e.Handled = true;
                return;
            }

            if (e.KeyCode == Keys.O)
            {
                OpenSelectedSource();
                e.Handled = true;
                return;
            }
        }
    }
}