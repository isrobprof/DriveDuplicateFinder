using System.Diagnostics;
using DriveDuplicateFinder.Models;
using DriveDuplicateFinder.Services;
using DriveDuplicateFinder.Utilities;
using Google.Apis.Drive.v3;

namespace DriveDuplicateFinder;

public partial class Form1 : Form
{
    private readonly GoogleDriveFileService _fileService = new();
    private readonly DuplicateFinderService _duplicateFinderService = new();
    private readonly DuplicateReviewService _reviewService = new();
    private readonly KeepRecommendationService _recommendationService = new();
    private readonly ReviewStateStorageService _reviewStateStorageService = new();
    private readonly List<DuplicateGroupReview> _reviews = [];
    private readonly System.Windows.Forms.Timer _reviewSaveTimer = new() { Interval = 1500 };
    private readonly SemaphoreSlim _reviewSaveSemaphore = new(1, 1);
    private List<DuplicateGroupReview> _filteredReviews = [];
    private DriveService? _driveService;
    private CancellationTokenSource? _searchCancellationTokenSource;
    private DuplicateGroupReview? _selectedReview;
    private bool _isRefreshingReviewControls;
    private bool _reviewStateIsCorrupt;
    private bool _reviewChangesPending;
    private bool _closeAfterSave;
    private int _reviewChangeVersion;
    private DriveScanResult? _lastScanResult;
    private SplitContainer? _mainSplit;
    private bool _mainSplitLayoutInitialized;
    private int _mainSplitInitializationAttempts;

    private readonly GroupBox grpFiltros = new();
    private readonly ComboBox cmbFiltroEstado = new();
    private readonly ComboBox cmbTamanoMinimo = new();
    private readonly ComboBox cmbOrden = new();
    private readonly TextBox txtBuscarRevision = new();
    private readonly CheckBox chkSoloCompartidos = new();
    private readonly CheckBox chkMasDeDosCopias = new();
    private readonly GroupBox grpRevision = new();
    private readonly Label lblGrupoNavegacion = new();
    private readonly Label lblRecomendacion = new();
    private readonly Label lblEstadoRevision = new();
    private readonly DataGridView dgvGrupoDetalle = new();
    private readonly TextBox txtNotas = new();
    private readonly Button btnAnteriorGrupo = new();
    private readonly Button btnSiguienteGrupo = new();
    private readonly Button btnSiguientePendiente = new();
    private readonly Button btnSiguienteListo = new();
    private readonly Button btnMarcarRevisado = new();
    private readonly Button btnGuardarRevision = new();
    private readonly Button btnVerPlan = new();
    private readonly Button btnExportarPlan = new();
    private readonly DataGridViewComboBoxColumn colDecisionDetalle = new();
    private readonly DataGridViewLinkColumn colUbicacionDetalle = new();
    private readonly Label lblNotasRevision = new();

    public Form1()
    {
        InitializeComponent();
        InitializeReviewControls();
        FormClosing += Form1_FormClosing;
        Shown += Form1_Shown;
        _reviewSaveTimer.Tick += reviewSaveTimer_Tick;
    }

    private async void btnConectar_Click(object? sender, EventArgs e)
    {
        btnConectar.Enabled = false;
        btnBuscar.Enabled = false;
        lblEstado.Text = "Conectando con Google Drive...";
        DriveService? newDriveService = null;

        try
        {
            var authService = new GoogleDriveAuthService();
            newDriveService = await authService.ConnectAsync();

            var request = newDriveService.Files.List();
            request.Q = "trashed = false";
            request.PageSize = 1;
            request.Fields = "files(id),nextPageToken";

            await request.ExecuteAsync();

            _driveService?.Dispose();
            _driveService = newDriveService;
            newDriveService = null;

            lblEstado.Text = "Conectado correctamente con Google Drive.";
            btnBuscar.Enabled = true;

            MessageBox.Show(
                "Conexi\u00F3n realizada correctamente.",
                "Google Drive",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            lblEstado.Text = "Error al conectar con Google Drive.";

            MessageBox.Show(
                $"No se pudo conectar con Google Drive.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "Error de conexi\u00F3n",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            newDriveService?.Dispose();
            btnConectar.Enabled = true;
        }
    }

    private async void btnBuscar_Click(object? sender, EventArgs e)
    {
        if (_driveService is null)
        {
            lblEstado.Text = "Con\u00E9ctese con Google Drive antes de buscar duplicados.";
            return;
        }

        btnConectar.Enabled = false;
        btnBuscar.Enabled = false;
        btnCancelar.Enabled = true;
        dgvDuplicados.DataSource = null;
        _searchCancellationTokenSource = new CancellationTokenSource();

        try
        {
            var progress = new Progress<DriveScanProgress>(UpdateProgress);
            DriveScanResult scanResult = await _fileService.GetComparableFilesAsync(
                _driveService,
                progress,
                _searchCancellationTokenSource.Token);

            IReadOnlyList<DuplicateGroup> duplicateGroups = await Task.Run(
                () => _duplicateFinderService.FindDuplicates(
                    scanResult.ComparableFiles,
                    progress,
                    _searchCancellationTokenSource.Token),
                _searchCancellationTokenSource.Token);

            _reviews.Clear();
            _reviews.AddRange(_reviewService.CreateReviews(duplicateGroups));
            _lastScanResult = scanResult;

            ReviewStateLoadResult loadResult = await _reviewStateStorageService.LoadAsync(
                _searchCancellationTokenSource.Token);
            _reviewStateIsCorrupt = loadResult.IsCorrupt;
            if (loadResult.State is not null)
            {
                _reviewService.ApplyStoredState(_reviews, loadResult.State);
            }

            if (!string.IsNullOrWhiteSpace(loadResult.WarningMessage))
            {
                MessageBox.Show(
                    loadResult.WarningMessage,
                    "Estado de revisi\u00F3n local",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }

            _reviewChangesPending = false;
            RefreshReviewViews(null);
            lblResumen.Text = BuildSummary(_lastScanResult, _reviewService.BuildSummary(_reviews));
            progressBar.Style = ProgressBarStyle.Continuous;
            progressBar.Value = 100;
            lblEstado.Text = "B\u00FAsqueda de duplicados completada.";
        }
        catch (OperationCanceledException)
        {
            progressBar.Style = ProgressBarStyle.Continuous;
            progressBar.Value = 0;
            lblEstado.Text = "B\u00FAsqueda cancelada.";
        }
        catch (Exception ex)
        {
            progressBar.Style = ProgressBarStyle.Continuous;
            progressBar.Value = 0;
            lblEstado.Text = "Error al buscar duplicados.";

            MessageBox.Show(
                $"No se pudieron analizar los archivos de Google Drive.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "Error de b\u00FAsqueda",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _searchCancellationTokenSource.Dispose();
            _searchCancellationTokenSource = null;
            btnConectar.Enabled = true;
            btnBuscar.Enabled = _driveService is not null;
            btnCancelar.Enabled = false;
        }
    }

    private void btnCancelar_Click(object? sender, EventArgs e)
    {
        _searchCancellationTokenSource?.Cancel();
    }

    private void InitializeReviewControls()
    {
        SuspendLayout();
        Controls.Clear();

        MinimumSize = new Size(1200, 700);
        ClientSize = new Size(1360, 800);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        dgvDuplicados.SelectionChanged += dgvDuplicados_SelectionChanged;

        var colEstado = new DataGridViewTextBoxColumn
        {
            DataPropertyName = "Status",
            HeaderText = "Estado",
            Name = "colEstadoRevision",
            ReadOnly = true,
            FillWeight = 95F
        };
        dgvDuplicados.Columns.Insert(1, colEstado);

        ConfigureFilters();
        ConfigureReviewPanel();

        var headerLayout = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 10, 12, 8),
            RowCount = 5
        };
        headerLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        headerLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        headerLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        headerLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        headerLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        lblTitulo.Dock = DockStyle.Fill;
        lblDescripcion.AutoSize = true;
        lblDescripcion.Dock = DockStyle.Fill;
        lblEstado.AutoSize = true;
        lblEstado.Dock = DockStyle.Fill;
        progressBar.Dock = DockStyle.Fill;
        progressBar.Margin = new Padding(0, 4, 0, 0);

        var commandPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 8, 0, 0)
        };
        ConfigureHeaderButton(btnConectar);
        ConfigureHeaderButton(btnBuscar);
        ConfigureHeaderButton(btnCancelar);
        commandPanel.Controls.AddRange([btnConectar, btnBuscar, btnCancelar]);

        headerLayout.Controls.Add(lblTitulo, 0, 0);
        headerLayout.Controls.Add(lblDescripcion, 0, 1);
        headerLayout.Controls.Add(commandPanel, 0, 2);
        headerLayout.Controls.Add(lblEstado, 0, 3);
        headerLayout.Controls.Add(progressBar, 0, 4);

        var leftLayout = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            RowCount = 3
        };
        leftLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        leftLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        leftLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        lblResumen.Dock = DockStyle.Fill;
        lblResumen.AutoSize = false;
        lblResumen.MinimumSize = new Size(0, 118);
        dgvDuplicados.Dock = DockStyle.Fill;
        dgvDuplicados.ScrollBars = ScrollBars.Both;
        ConfigureMainGridColumns(colEstado);
        leftLayout.Controls.Add(lblResumen, 0, 0);
        leftLayout.Controls.Add(grpFiltros, 0, 1);
        leftLayout.Controls.Add(dgvDuplicados, 0, 2);

        var rightLayout = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            RowCount = 1
        };
        rightLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        rightLayout.Controls.Add(grpRevision, 0, 0);

        var mainSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.None,
            Orientation = Orientation.Vertical,
            SplitterWidth = 7,
            IsSplitterFixed = false
        };
        _mainSplit = mainSplit;
        mainSplit.Panel1.Controls.Add(leftLayout);
        mainSplit.Panel2.Controls.Add(rightLayout);

        var rootLayout = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            RowCount = 2
        };
        rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        rootLayout.Controls.Add(headerLayout, 0, 0);
        rootLayout.Controls.Add(mainSplit, 0, 1);
        Controls.Add(rootLayout);
        ResumeLayout(true);
    }

    private void Form1_Shown(object? sender, EventArgs e)
    {
        BeginInvoke(new Action(InitializeMainSplitLayout));
    }

    private void InitializeMainSplitLayout()
    {
        if (_mainSplitLayoutInitialized || _mainSplit is null || _mainSplit.IsDisposed)
        {
            return;
        }

        int width = _mainSplit.ClientSize.Width;
        if (width <= _mainSplit.SplitterWidth + 2)
        {
            if (_mainSplitInitializationAttempts++ == 0)
            {
                BeginInvoke(new Action(InitializeMainSplitLayout));
            }

            return;
        }

        int availableWidth = width - _mainSplit.SplitterWidth;
        int desiredDistance = (int)Math.Round(availableWidth * 0.42);
        desiredDistance = Math.Clamp(desiredDistance, 1, availableWidth - 1);

        _mainSplit.Panel1MinSize = 0;
        _mainSplit.Panel2MinSize = 0;
        _mainSplit.SplitterDistance = desiredDistance;

        int panel1MinSize = Math.Min(410, Math.Max(0, desiredDistance - 1));
        int panel2MinSize = Math.Min(540, Math.Max(0, availableWidth - desiredDistance - 1));

        _mainSplit.Panel1MinSize = panel1MinSize;
        _mainSplit.Panel2MinSize = panel2MinSize;
        _mainSplitLayoutInitialized = true;
    }

    private void ConfigureFilters()
    {
        grpFiltros.Text = "Filtros y ordenaci\u00F3n locales";
        grpFiltros.Controls.Clear();
        grpFiltros.Dock = DockStyle.Fill;
        grpFiltros.AutoSize = true;
        grpFiltros.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        grpFiltros.Padding = new Padding(10);

        var filterLayout = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            RowCount = 2
        };
        filterLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        filterLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var firstRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        var secondRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };

        cmbFiltroEstado.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbFiltroEstado.DisplayMember = nameof(ReviewStatusFilterOption.DisplayName);
        cmbFiltroEstado.Items.AddRange(
        [
            new ReviewStatusFilterOption("Todos", null),
            new ReviewStatusFilterOption("Pendientes", DuplicateGroupReviewStatus.Pending),
            new ReviewStatusFilterOption("Parcialmente revisados", DuplicateGroupReviewStatus.PartiallyReviewed),
            new ReviewStatusFilterOption("Listos para limpieza", DuplicateGroupReviewStatus.ReadyForCleanup),
            new ReviewStatusFilterOption("Revisados sin borrar", DuplicateGroupReviewStatus.ReviewedWithoutCleanup)
        ]);
        cmbFiltroEstado.SelectedIndex = 0;
        cmbFiltroEstado.Width = 180;

        cmbTamanoMinimo.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbTamanoMinimo.Items.AddRange(["Todos", "M\u00E1s de 10 MB", "M\u00E1s de 100 MB", "M\u00E1s de 1 GB", "M\u00E1s de 10 GB"]);
        cmbTamanoMinimo.SelectedIndex = 0;
        cmbTamanoMinimo.Width = 150;

        cmbOrden.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbOrden.Items.AddRange(["Mayor espacio recuperable", "Mayor tama\u00F1o", "M\u00E1s copias", "Ruta", "Estado", "Grupo visual"]);
        cmbOrden.SelectedIndex = 0;
        cmbOrden.Width = 220;

        chkSoloCompartidos.Text = "Solo compartidos";
        chkMasDeDosCopias.Text = "M\u00E1s de dos copias";
        txtBuscarRevision.Width = 240;

        cmbFiltroEstado.SelectedIndexChanged += (_, _) => RefreshReviewViews(_selectedReview?.StableId);
        cmbTamanoMinimo.SelectedIndexChanged += (_, _) => RefreshReviewViews(_selectedReview?.StableId);
        cmbOrden.SelectedIndexChanged += (_, _) => RefreshReviewViews(_selectedReview?.StableId);
        chkSoloCompartidos.CheckedChanged += (_, _) => RefreshReviewViews(_selectedReview?.StableId);
        chkMasDeDosCopias.CheckedChanged += (_, _) => RefreshReviewViews(_selectedReview?.StableId);
        txtBuscarRevision.TextChanged += (_, _) => RefreshReviewViews(_selectedReview?.StableId);

        firstRow.Controls.AddRange(
        [
            CreateFilterField("Estado", cmbFiltroEstado),
            CreateFilterField("Tama\u00F1o m\u00EDnimo", cmbTamanoMinimo),
            CreateFilterField("Orden", cmbOrden)
        ]);
        secondRow.Controls.Add(chkSoloCompartidos);
        secondRow.Controls.Add(chkMasDeDosCopias);
        secondRow.Controls.Add(CreateFilterField("Buscar nombre o ruta", txtBuscarRevision));
        filterLayout.Controls.Add(firstRow, 0, 0);
        filterLayout.Controls.Add(secondRow, 0, 1);
        grpFiltros.Controls.Add(filterLayout);
    }

    private void ConfigureMainGridColumns(DataGridViewTextBoxColumn colEstado)
    {
        dgvDuplicados.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        colGrupo.Width = 55;
        colEstado.Width = 145;
        colNombre.Width = 140;
        colRuta.Width = 260;
        colRuta.MinimumWidth = 260;
        colRuta.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        colTamano.Width = 90;
        colFechaModificacion.Width = 125;
        colMd5.Width = 180;
        colEnlace.Width = 130;
    }

    private void ConfigureReviewPanel()
    {
        grpRevision.Text = "Revisi\u00F3n del grupo";
        grpRevision.Controls.Clear();
        grpRevision.Dock = DockStyle.Fill;
        grpRevision.Padding = new Padding(10);

        lblGrupoNavegacion.Text = "Sin grupos para revisar.";
        lblGrupoNavegacion.AutoSize = true;
        lblEstadoRevision.AutoSize = true;
        lblRecomendacion.AutoSize = true;
        lblRecomendacion.MaximumSize = new Size(0, 42);

        ConfigureButton(btnAnteriorGrupo, "Grupo anterior", btnAnteriorGrupo_Click);
        ConfigureButton(btnSiguienteGrupo, "Grupo siguiente", btnSiguienteGrupo_Click);
        ConfigureButton(btnSiguientePendiente, "Siguiente pendiente", btnSiguientePendiente_Click);
        ConfigureButton(btnSiguienteListo, "Siguiente listo para limpieza", btnSiguienteListo_Click);

        dgvGrupoDetalle.AllowUserToAddRows = false;
        dgvGrupoDetalle.AllowUserToDeleteRows = false;
        dgvGrupoDetalle.AutoGenerateColumns = false;
        dgvGrupoDetalle.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        dgvGrupoDetalle.Dock = DockStyle.Fill;
        dgvGrupoDetalle.ScrollBars = ScrollBars.Both;
        dgvGrupoDetalle.EditMode = DataGridViewEditMode.EditOnEnter;
        dgvGrupoDetalle.ReadOnly = false;
        dgvGrupoDetalle.RowHeadersVisible = false;
        dgvGrupoDetalle.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        ConfigureDetailColumns();
        dgvGrupoDetalle.CellContentClick += dgvGrupoDetalle_CellContentClick;
        dgvGrupoDetalle.CellFormatting += dgvGrupoDetalle_CellFormatting;
        dgvGrupoDetalle.CurrentCellDirtyStateChanged += dgvGrupoDetalle_CurrentCellDirtyStateChanged;
        dgvGrupoDetalle.CellValueChanged += dgvGrupoDetalle_CellValueChanged;

        lblNotasRevision.Text = "Notas locales";
        lblNotasRevision.AutoSize = true;
        txtNotas.Multiline = true;
        txtNotas.Dock = DockStyle.Fill;
        txtNotas.MinimumSize = new Size(0, 54);
        txtNotas.Leave += txtNotas_Leave;

        ConfigureButton(btnMarcarRevisado, "Marcar revisado sin borrar", btnMarcarRevisado_Click);
        ConfigureButton(btnGuardarRevision, "Guardar revisi\u00F3n", btnGuardarRevision_Click);
        ConfigureButton(btnVerPlan, "Ver plan de limpieza", btnVerPlan_Click);
        ConfigureButton(btnExportarPlan, "Exportar plan", btnExportarPlan_Click);

        var navigationPanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        navigationPanel.Controls.AddRange([btnAnteriorGrupo, btnSiguienteGrupo, btnSiguientePendiente, btnSiguienteListo]);
        var actionPanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        actionPanel.Controls.AddRange([btnMarcarRevisado, btnGuardarRevision, btnVerPlan, btnExportarPlan]);
        var reviewLayout = new TableLayoutPanel { ColumnCount = 1, Dock = DockStyle.Fill, RowCount = 7 };
        reviewLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        reviewLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        reviewLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        reviewLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        reviewLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        reviewLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        reviewLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        reviewLayout.Controls.Add(lblGrupoNavegacion, 0, 0);
        reviewLayout.Controls.Add(navigationPanel, 0, 1);
        reviewLayout.Controls.Add(CreateReviewInfoPanel(), 0, 2);
        reviewLayout.Controls.Add(dgvGrupoDetalle, 0, 3);
        reviewLayout.Controls.Add(lblNotasRevision, 0, 4);
        reviewLayout.Controls.Add(txtNotas, 0, 5);
        reviewLayout.Controls.Add(actionPanel, 0, 6);
        grpRevision.Controls.Add(reviewLayout);
    }

    private void ConfigureDetailColumns()
    {
        colDecisionDetalle.DataPropertyName = "Decision";
        colDecisionDetalle.HeaderText = "Decisi\u00F3n";
        colDecisionDetalle.Name = "colDecisionDetalle";
        colDecisionDetalle.DataSource = new[]
        {
            new DecisionOption(DuplicateFileDecision.Undecided, "Sin decidir"),
            new DecisionOption(DuplicateFileDecision.Keep, "Conservar"),
            new DecisionOption(DuplicateFileDecision.CandidateForTrash, "Candidato a papelera")
        };
        colDecisionDetalle.DisplayMember = nameof(DecisionOption.DisplayName);
        colDecisionDetalle.ValueMember = nameof(DecisionOption.Value);
        colDecisionDetalle.Width = 165;

        DataGridViewTextBoxColumn colName = CreateTextColumn("Name", "Nombre", 135);
        DataGridViewTextBoxColumn colPath = CreateTextColumn("Path", "Ruta", 240);
        colPath.MinimumWidth = 240;
        colPath.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        dgvGrupoDetalle.Columns.AddRange(
        [
            colDecisionDetalle,
            colName,
            colPath,
            CreateTextColumn("Size", "Tama\u00F1o", 85),
            CreateTextColumn("CreatedTime", "Creaci\u00F3n", 120),
            CreateTextColumn("ModifiedTime", "Modificaci\u00F3n", 120),
            CreateTextColumn("Owner", "Propietario", 130),
            CreateTextColumn("Flags", "Indicadores", 120),
            CreateTextColumn("Recommendation", "Sugerencia", 170),
            CreateTextColumn("Warnings", "Advertencias", 170),
            colUbicacionDetalle
        ]);

        colUbicacionDetalle.DataPropertyName = "ParentFolderUrl";
        colUbicacionDetalle.HeaderText = "Ubicaci\u00F3n";
        colUbicacionDetalle.Name = "colUbicacionDetalle";
        colUbicacionDetalle.Width = 130;
    }

    private static DataGridViewTextBoxColumn CreateTextColumn(string propertyName, string headerText, int width)
    {
        return new DataGridViewTextBoxColumn
        {
            DataPropertyName = propertyName,
            HeaderText = headerText,
            Name = $"col{propertyName}",
            ReadOnly = true,
            Width = width
        };
    }

    private static Control CreateFilterField(string labelText, Control control)
    {
        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, 12, 0) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = labelText, AutoSize = true }, 0, 0);
        layout.Controls.Add(control, 0, 1);
        return layout;
    }

    private Control CreateReviewInfoPanel()
    {
        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            RowCount = 2
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        lblEstadoRevision.Dock = DockStyle.Fill;
        lblRecomendacion.Dock = DockStyle.Fill;
        layout.Controls.Add(lblEstadoRevision, 0, 0);
        layout.Controls.Add(lblRecomendacion, 0, 1);
        return layout;
    }

    private static void ConfigureHeaderButton(Button button)
    {
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.Margin = new Padding(0, 0, 8, 0);
    }

    private static void ConfigureButton(Button button, string text, EventHandler clickHandler)
    {
        button.Text = text;
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.Margin = new Padding(0, 0, 8, 4);
        button.UseVisualStyleBackColor = true;
        button.Click += clickHandler;
    }

    private void RefreshReviewViews(string? preferredStableId)
    {
        if (_reviews.Count == 0)
        {
            _filteredReviews = [];
            _selectedReview = null;
            dgvDuplicados.DataSource = null;
            dgvGrupoDetalle.DataSource = null;
            lblGrupoNavegacion.Text = "Sin grupos para revisar.";
            return;
        }

        _filteredReviews = ApplyFilters(_reviews).ToList();
        DuplicateGroupReview? selected = _filteredReviews.FirstOrDefault(review => review.StableId == preferredStableId)
            ?? _filteredReviews.FirstOrDefault();

        _isRefreshingReviewControls = true;
        try
        {
            dgvDuplicados.DataSource = _filteredReviews
                .SelectMany(review => review.Group.Files.Select(file => new ReviewGroupRow
                {
                    StableGroupId = review.StableId,
                    GroupNumber = review.Group.GroupNumber,
                    Name = file.Name,
                    Path = file.Path,
                    Size = FileSizeFormatter.Format(file.Size),
                    ModifiedTime = file.ModifiedTime?.ToLocalTime().ToString("g") ?? "No disponible",
                    Md5Checksum = file.Md5Checksum,
                    Status = GetStatusText(review.Status),
                    ParentFolderUrl = file.ParentFolderUrl
                }))
                .ToList();
        }
        finally
        {
            _isRefreshingReviewControls = false;
        }

        SelectReview(selected);
    }

    private IEnumerable<DuplicateGroupReview> ApplyFilters(IEnumerable<DuplicateGroupReview> source)
    {
        IEnumerable<DuplicateGroupReview> query = source;
        if (cmbFiltroEstado.SelectedItem is ReviewStatusFilterOption { Status: DuplicateGroupReviewStatus status })
        {
            query = query.Where(review => review.Status == status);
        }

        long minimumSize = cmbTamanoMinimo.SelectedIndex switch
        {
            1 => 10L * 1024 * 1024,
            2 => 100L * 1024 * 1024,
            3 => 1024L * 1024 * 1024,
            4 => 10L * 1024 * 1024 * 1024,
            _ => 0
        };

        if (minimumSize > 0)
        {
            query = query.Where(review => review.Group.FileSize > minimumSize);
        }

        if (chkSoloCompartidos.Checked)
        {
            query = query.Where(review => review.Group.Files.Any(file => file.IsShared == true));
        }

        if (chkMasDeDosCopias.Checked)
        {
            query = query.Where(review => review.Group.Files.Count > 2);
        }

        string searchText = txtBuscarRevision.Text.Trim();
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            query = query.Where(review => review.Group.Files.Any(file =>
                file.Name.Contains(searchText, StringComparison.CurrentCultureIgnoreCase) ||
                file.Path.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)));
        }

        return cmbOrden.SelectedIndex switch
        {
            1 => query.OrderByDescending(review => review.Group.FileSize),
            2 => query.OrderByDescending(review => review.Group.Files.Count),
            3 => query.OrderBy(review => review.Group.Files.Min(file => file.Path), StringComparer.CurrentCultureIgnoreCase),
            4 => query.OrderBy(review => review.Status).ThenByDescending(review => review.Group.RecoverableBytes),
            5 => query.OrderBy(review => review.Group.GroupNumber),
            _ => query.OrderByDescending(review => review.Group.RecoverableBytes)
        };
    }

    private void SelectReview(DuplicateGroupReview? review)
    {
        _selectedReview = review;
        if (review is null)
        {
            dgvGrupoDetalle.DataSource = null;
            lblGrupoNavegacion.Text = "No hay grupos con los filtros actuales.";
            lblEstadoRevision.Text = string.Empty;
            lblRecomendacion.Text = string.Empty;
            txtNotas.Text = string.Empty;
            return;
        }

        int currentIndex = _filteredReviews.FindIndex(item => item.StableId == review.StableId);
        lblGrupoNavegacion.Text = $"Grupo {currentIndex + 1} de {_filteredReviews.Count} (visual {review.Group.GroupNumber})";
        lblEstadoRevision.Text = $"Estado: {GetStatusText(review.Status)}. Espacio recuperable del grupo: {FileSizeFormatter.Format(review.Group.RecoverableBytes)}.";

        KeepRecommendation recommendation = _recommendationService.GetRecommendation(review);
        lblRecomendacion.Text = recommendation.Reason;

        _isRefreshingReviewControls = true;
        try
        {
            dgvGrupoDetalle.DataSource = review.Group.Files.Select(file => new ReviewFileRow
            {
                FileId = file.Id,
                Decision = review.DecisionsByFileId[file.Id],
                Name = file.Name,
                Path = file.Path,
                Size = FileSizeFormatter.Format(file.Size),
                CreatedTime = file.CreatedTime?.ToLocalTime().ToString("g") ?? "No disponible",
                ModifiedTime = file.ModifiedTime?.ToLocalTime().ToString("g") ?? "No disponible",
                Owner = file.OwnerNames.Count == 0 ? "No disponible" : string.Join(", ", file.OwnerNames),
                Flags = BuildFlags(file),
                Recommendation = file.Id == recommendation.FileId ? "Recomendado conservar" : string.Empty,
                Warnings = DuplicateReviewService.GetWarnings(file, review.Group),
                ParentFolderUrl = file.ParentFolderUrl
            }).ToList();
            txtNotas.Text = review.Notes ?? string.Empty;
        }
        finally
        {
            _isRefreshingReviewControls = false;
        }
    }

    private void dgvDuplicados_SelectionChanged(object? sender, EventArgs e)
    {
        if (_isRefreshingReviewControls || dgvDuplicados.CurrentRow?.DataBoundItem is not ReviewGroupRow row)
        {
            return;
        }

        SelectReview(_filteredReviews.FirstOrDefault(review => review.StableId == row.StableGroupId));
    }

    private void dgvGrupoDetalle_CellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_isRefreshingReviewControls || e.RowIndex < 0 || e.ColumnIndex != colDecisionDetalle.Index ||
            _selectedReview is null || dgvGrupoDetalle.Rows[e.RowIndex].DataBoundItem is not ReviewFileRow row)
        {
            return;
        }

        DecisionChangeResult result = _reviewService.SetDecision(_selectedReview, row.FileId, row.Decision);
        if (!result.Accepted)
        {
            MessageBox.Show(result.Message, "Decisi\u00F3n no permitida", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else if (!string.IsNullOrWhiteSpace(result.AutomaticallyKeptFileId))
        {
            lblEstadoRevision.Text = "Se marc\u00F3 autom\u00E1ticamente una copia como Conservar para mantener el grupo seguro.";
        }

        MarkReviewChanged();
        RefreshReviewViews(_selectedReview.StableId);
    }

    private void dgvGrupoDetalle_CurrentCellDirtyStateChanged(object? sender, EventArgs e)
    {
        if (dgvGrupoDetalle.IsCurrentCellDirty)
        {
            dgvGrupoDetalle.CommitEdit(DataGridViewDataErrorContexts.Commit);
        }
    }

    private void dgvGrupoDetalle_CellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != colUbicacionDetalle.Index ||
            dgvGrupoDetalle.Rows[e.RowIndex].DataBoundItem is not ReviewFileRow row)
        {
            return;
        }

        OpenLocation(row.ParentFolderUrl);
    }

    private void dgvGrupoDetalle_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.ColumnIndex == colUbicacionDetalle.Index && e.Value is string url && !string.IsNullOrWhiteSpace(url))
        {
            e.Value = "Abrir ubicaci\u00F3n";
            e.FormattingApplied = true;
        }
    }

    private void dgvDuplicados_CellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != colEnlace.Index ||
            dgvDuplicados.Rows[e.RowIndex].DataBoundItem is not ReviewGroupRow row)
        {
            return;
        }

        OpenLocation(row.ParentFolderUrl);
    }

    private void dgvDuplicados_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.ColumnIndex == colEnlace.Index && e.Value is string url && !string.IsNullOrWhiteSpace(url))
        {
            e.Value = "Abrir ubicaci\u00F3n";
            e.FormattingApplied = true;
        }
    }

    private void txtNotas_Leave(object? sender, EventArgs e)
    {
        if (_isRefreshingReviewControls || _selectedReview is null || string.Equals(_selectedReview.Notes ?? string.Empty, txtNotas.Text, StringComparison.Ordinal))
        {
            return;
        }

        _reviewService.UpdateNotes(_selectedReview, txtNotas.Text);
        MarkReviewChanged();
        RefreshReviewViews(_selectedReview.StableId);
    }

    private void btnMarcarRevisado_Click(object? sender, EventArgs e)
    {
        if (_selectedReview is null)
        {
            return;
        }

        if (MessageBox.Show(
                "Esta acci\u00F3n limpiar\u00E1 los candidatos locales de este grupo. No modifica Google Drive. \u00BFContinuar?",
                "Revisado sin borrar",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        _reviewService.MarkReviewedWithoutCleanup(_selectedReview);
        MarkReviewChanged();
        RefreshReviewViews(_selectedReview.StableId);
    }

    private async void btnGuardarRevision_Click(object? sender, EventArgs e)
    {
        bool forceOverwrite = false;
        if (_reviewStateIsCorrupt)
        {
            forceOverwrite = MessageBox.Show(
                "El estado local anterior parece corrupto. \u00BFReemplazarlo por el estado actual?",
                "Reemplazar estado local",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) == DialogResult.Yes;
            if (!forceOverwrite)
            {
                return;
            }
        }

        await SaveReviewStateAsync(forceOverwrite, showError: true);
    }

    private void btnVerPlan_Click(object? sender, EventArgs e)
    {
        IReadOnlyList<CleanupPlanRow> plan = _reviewService.BuildCleanupPlan(_reviews);
        if (plan.Count == 0)
        {
            MessageBox.Show(
                "No hay grupos listos para limpieza. Esta vista nunca modifica Google Drive.",
                "Plan de limpieza",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        using var form = new CleanupPlanForm(plan);
        form.ShowDialog(this);
    }

    private async void btnExportarPlan_Click(object? sender, EventArgs e)
    {
        IReadOnlyList<CleanupPlanRow> plan = _reviewService.BuildCleanupPlan(_reviews);
        if (plan.Count == 0)
        {
            MessageBox.Show("No hay grupos listos para exportar.", "Exportar plan", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Filter = "Archivo JSON|*.json",
            FileName = "plan-limpieza-drive.json",
            Title = "Exportar plan local de limpieza"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            await _reviewStateStorageService.ExportPlanAsync(dialog.FileName, plan);
            MessageBox.Show("El plan local se export\u00F3 correctamente. No se modific\u00F3 Google Drive.", "Exportar plan", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"No se pudo exportar el plan.{Environment.NewLine}{Environment.NewLine}{ex.Message}", "Error al exportar", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void btnAnteriorGrupo_Click(object? sender, EventArgs e) => NavigateGroups(-1, null);

    private void btnSiguienteGrupo_Click(object? sender, EventArgs e) => NavigateGroups(1, null);

    private void btnSiguientePendiente_Click(object? sender, EventArgs e) => NavigateGroups(1, DuplicateGroupReviewStatus.Pending);

    private void btnSiguienteListo_Click(object? sender, EventArgs e) => NavigateGroups(1, DuplicateGroupReviewStatus.ReadyForCleanup);

    private void NavigateGroups(int direction, DuplicateGroupReviewStatus? targetStatus)
    {
        if (_filteredReviews.Count == 0)
        {
            return;
        }

        int startIndex = Math.Max(0, _filteredReviews.FindIndex(review => review.StableId == _selectedReview?.StableId));
        for (int offset = 1; offset <= _filteredReviews.Count; offset++)
        {
            int index = (startIndex + (direction * offset) + _filteredReviews.Count) % _filteredReviews.Count;
            DuplicateGroupReview candidate = _filteredReviews[index];
            if (targetStatus is null || candidate.Status == targetStatus)
            {
                SelectReview(candidate);
                SelectMainRow(candidate.StableId);
                return;
            }
        }
    }

    private void SelectMainRow(string stableGroupId)
    {
        foreach (DataGridViewRow row in dgvDuplicados.Rows)
        {
            if (row.DataBoundItem is ReviewGroupRow groupRow && groupRow.StableGroupId == stableGroupId)
            {
                dgvDuplicados.CurrentCell = row.Cells[0];
                return;
            }
        }
    }

    private async Task SaveReviewStateAsync(bool forceOverwriteCorruptState, bool showError)
    {
        if (_reviews.Count == 0 || (_reviewStateIsCorrupt && !forceOverwriteCorruptState))
        {
            return;
        }

        await _reviewSaveSemaphore.WaitAsync();
        int versionAtSave = _reviewChangeVersion;
        try
        {
            await _reviewStateStorageService.SaveAsync(
                _reviews,
                forceOverwriteCorruptState,
                _reviewStateIsCorrupt);
            _reviewStateIsCorrupt = false;
            _reviewChangesPending = _reviewChangeVersion != versionAtSave;
            lblEstadoRevision.Text = $"Revisi\u00F3n local guardada en {_reviewStateStorageService.StatePath}.";
        }
        catch (Exception ex)
        {
            if (showError)
            {
                MessageBox.Show($"No se pudo guardar la revisi\u00F3n local.{Environment.NewLine}{Environment.NewLine}{ex.Message}", "Error al guardar", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            else
            {
                lblEstadoRevision.Text = "Cambios pendientes: no se pudo guardar el estado local.";
            }
        }
        finally
        {
            _reviewSaveSemaphore.Release();
            if (_reviewChangesPending && !_reviewStateIsCorrupt)
            {
                _reviewSaveTimer.Start();
            }
        }
    }

    private void MarkReviewChanged()
    {
        _reviewChangesPending = true;
        _reviewChangeVersion++;
        if (_selectedReview is not null)
        {
            lblResumen.Text = BuildSummary(_lastScanResult, _reviewService.BuildSummary(_reviews));
        }

        if (!_reviewStateIsCorrupt)
        {
            _reviewSaveTimer.Stop();
            _reviewSaveTimer.Start();
        }
    }

    private async void reviewSaveTimer_Tick(object? sender, EventArgs e)
    {
        _reviewSaveTimer.Stop();
        await SaveReviewStateAsync(forceOverwriteCorruptState: false, showError: false);
    }

    private async void Form1_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_closeAfterSave || !_reviewChangesPending || _reviewStateIsCorrupt)
        {
            return;
        }

        e.Cancel = true;
        _closeAfterSave = true;
        await SaveReviewStateAsync(forceOverwriteCorruptState: false, showError: true);
        BeginInvoke(Close);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _searchCancellationTokenSource?.Cancel();
        _searchCancellationTokenSource?.Dispose();
        _driveService?.Dispose();
        _reviewSaveTimer.Stop();
        _reviewSaveTimer.Dispose();
        _reviewSaveSemaphore.Dispose();
        base.OnFormClosed(e);
    }

    private void UpdateProgress(DriveScanProgress progress)
    {
        lblEstado.Text = progress.Status;

        if (progress.Percentage is int percentage)
        {
            progressBar.Style = ProgressBarStyle.Continuous;
            progressBar.Value = percentage;
        }
        else
        {
            progressBar.Style = ProgressBarStyle.Marquee;
        }
    }

    private static string BuildSummary(DriveScanResult? scanResult, DuplicateReviewSummary reviewSummary)
    {
        string scanSummary = scanResult is null
            ? string.Empty
            : $"Elementos examinados: {scanResult.ItemsExamined:N0}{Environment.NewLine}" +
              $"Archivos comparables: {scanResult.ComparableFiles.Count:N0}{Environment.NewLine}" +
              $"Archivos sin MD5 ignorados: {scanResult.FilesWithoutMd5Ignored:N0}{Environment.NewLine}";

        return scanSummary +
               $"Grupos: {reviewSummary.TotalGroups:N0} | Pendientes: {reviewSummary.PendingGroups:N0} | Parciales: {reviewSummary.PartiallyReviewedGroups:N0} | Listos: {reviewSummary.ReadyForCleanupGroups:N0} | Revisados: {reviewSummary.ReviewedWithoutCleanupGroups:N0}{Environment.NewLine}" +
               $"Conservar: {reviewSummary.FilesMarkedKeep:N0} | Candidatos locales: {reviewSummary.CandidateFiles:N0} ({FileSizeFormatter.Format(reviewSummary.CandidateBytes)}) | Compartidos: {reviewSummary.SharedCandidateFiles:N0} | Destacados: {reviewSummary.StarredCandidateFiles:N0} | Sin decidir en parciales: {reviewSummary.UndecidedFilesInPartiallyReviewedGroups:N0}";
    }

    private static string BuildFlags(DriveFileInfo file)
    {
        var flags = new List<string>();
        if (file.IsShared == true)
        {
            flags.Add("Compartido");
        }

        if (file.IsStarred == true)
        {
            flags.Add("Destacado");
        }

        if (!string.IsNullOrWhiteSpace(file.SharedDriveId))
        {
            flags.Add("Unidad compartida");
        }

        return flags.Count == 0 ? "-" : string.Join(", ", flags);
    }

    private static string GetStatusText(DuplicateGroupReviewStatus status)
    {
        return status switch
        {
            DuplicateGroupReviewStatus.Pending => "Pendiente",
            DuplicateGroupReviewStatus.PartiallyReviewed => "Parcialmente revisado",
            DuplicateGroupReviewStatus.ReadyForCleanup => "Listo para limpieza",
            DuplicateGroupReviewStatus.ReviewedWithoutCleanup => "Revisado sin borrar",
            _ => status.ToString()
        };
    }

    private void OpenLocation(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? link) ||
            (link.Scheme != Uri.UriSchemeHttp && link.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = link.AbsoluteUri, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"No se pudo abrir la ubicaci\u00F3n en Google Drive.{Environment.NewLine}{Environment.NewLine}{ex.Message}", "Error al abrir la ubicaci\u00F3n", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private sealed record DecisionOption(DuplicateFileDecision Value, string DisplayName);

    private sealed record ReviewStatusFilterOption(string DisplayName, DuplicateGroupReviewStatus? Status);
}
