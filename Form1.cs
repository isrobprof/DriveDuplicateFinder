using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using DriveDuplicateFinder.Data.Sqlite;
using DriveDuplicateFinder.Data.Sqlite.Repositories;
using DriveDuplicateFinder.Models;
using DriveDuplicateFinder.Models.Persistence;
using DriveDuplicateFinder.Services;
using DriveDuplicateFinder.Utilities;
using Google.Apis.Drive.v3;

namespace DriveDuplicateFinder;

public partial class Form1 : Form
{
    private readonly GoogleDriveFileService _fileService = new();
    private readonly bool _demoMode;
    private readonly string? _demoDataDirectory;
    private DemoReviewSeed? _demoSeed;
    private readonly RecoverableFullScanService _recoverableFullScanService;
    private readonly ReviewStateRepository _reviewStateRepository;
    private readonly DuplicateFinderService _duplicateFinderService = new();
    private readonly DuplicateReviewService _reviewService = new();
    private readonly KeepRecommendationService _recommendationService = new();
    private readonly RecommendationApplicationService _recommendationApplicationService = new();
    private readonly RecommendationBatchService _recommendationBatchService;
    private readonly ReviewStateStorageService _reviewStateStorageService = new();
    private readonly GoogleDriveTrashService _trashService = new();
    private readonly CleanupBatchService _cleanupBatchService;
    private readonly CleanupHistoryService _cleanupHistoryService = new();
    private readonly List<DuplicateGroupReview> _reviews = [];
    private readonly BindingList<ReviewGroupRow> _mainGridRows = [];
    private readonly Dictionary<string, KeepRecommendation> _recommendationsByGroupId = new(StringComparer.Ordinal);
    private readonly RecommendationUndoSession _recommendationUndoSession = new();
    private readonly RecommendationBatchUndoSession _recommendationBatchUndoSession = new();
    private readonly PagedRequestGeneration _pagedRequestGeneration = new();
    private readonly HashSet<string> _batchSelectedStableIds = new(StringComparer.Ordinal);
    private readonly ToolTip _recommendationToolTip = new();
    private readonly System.Windows.Forms.Timer _reviewSaveTimer = new() { Interval = 1500 };
    private readonly SemaphoreSlim _reviewSaveSemaphore = new(1, 1);
    private List<DuplicateGroupReview> _filteredReviews = [];
    private DriveService? _driveService;
    private DriveService? _cleanupDriveService;
    private CancellationTokenSource? _searchCancellationTokenSource;
    private CancellationTokenSource? _cleanupCancellationTokenSource;
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
    private bool _cleanupModeActive;
    private bool _cleanupOperationInProgress;
    private bool _scanResultsAreObsolete;
    private bool _batchOperationInProgress;
    private bool _pagedReadOnlyMode;
    private bool _isBindingPagedGroups;
    private ScanInventoryIdentity? _pagedInventory;
    private DuplicateGroupSummaryPage? _pagedGroupPage;
    private DuplicateGroupIdentity? _activePagedGroup;
    private int _pagedGroupOffset;
    private int _pagedMemberOffset;
    private long _pagedDuplicateGroupCount;
    private long _pagedKeepCount;
    private long _pagedCandidateCount;
    private long _pagedUndecidedCount;
    private long _pagedCurrentMemberCount;
    private bool _isBindingPagedMembers;
    private bool _pagedReviewWriteInProgress;

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
    private readonly Button btnAplicarRecomendacion = new();
    private readonly Button btnDeshacerRecomendacion = new();
    private readonly Button btnSeleccionarVisibles = new();
    private readonly Button btnDeseleccionarTodos = new();
    private readonly Button btnAplicarRecomendacionesSeleccionadas = new();
    private readonly Button btnDeshacerUltimoLote = new();
    private readonly Button btnEnviarGruposPapelera = new();
    private readonly Button btnGuardarRevision = new();
    private readonly Button btnVerPlan = new();
    private readonly Button btnExportarPlan = new();
    private readonly Button btnActivarLimpieza = new();
    private readonly Button btnDesactivarLimpieza = new();
    private readonly Button btnEnviarGrupoPapelera = new();
    private readonly Button btnAbrirPapelera = new();
    private readonly Button btnRestablecerAutorizacionLimpieza = new();
    private readonly DataGridViewComboBoxColumn colDecisionDetalle = new();
    private readonly DataGridViewLinkColumn colUbicacionDetalle = new();
    private readonly DataGridViewCheckBoxColumn colSeleccionLote = new();
    private readonly Label lblNotasRevision = new();
    private readonly Label lblModoLimpieza = new();
    private readonly Label lblElegibilidadLimpieza = new();
    private readonly Label lblSeleccionLote = new();
    private readonly CheckBox chkLegacyFullScan = new();
    private readonly GroupBox grpPagedGroups = new();
    private readonly GroupBox grpPagedMembers = new();
    private readonly DataGridView dgvPagedGroups = new();
    private readonly DataGridView dgvPagedMembers = new();
    private readonly Label lblPagedGroupPage = new();
    private readonly Label lblPagedMembersPage = new();
    private readonly Label lblPagedReadOnly = new();
    private readonly Label lblWorkflowStep = new();
    private readonly Button btnPagedPreviousGroupPage = new();
    private readonly Button btnPagedNextGroupPage = new();
    private readonly Button btnPagedPreviousMemberPage = new();
    private readonly Button btnPagedNextMemberPage = new();
    private readonly Button btnPagedConfirmGroup = new();
    private readonly Button btnPagedSkipGroup = new();
    private readonly Button btnPagedViewCleanupPlan = new();
    private readonly Button btnReopenLatestScan = new();
    private const int PagedGroupPageSize = 1;
    private const int PagedMemberPageSize = 100;

    public Form1() : this(demoMode: false) { }

    public Form1(bool demoMode)
    {
        WinFormsStartupLocalChecks.EnsureStaThread();
        _demoMode = demoMode;
        _demoDataDirectory = demoMode ? DemoReviewData.CreateTemporaryDirectoryPath() : null;
        _recoverableFullScanService = new RecoverableFullScanService(_fileService, _demoDataDirectory);
        _reviewStateRepository = new ReviewStateRepository(new SqliteConnectionFactory(new LocalDataPathService(_demoDataDirectory)));
        _recommendationBatchService = new RecommendationBatchService(_recommendationApplicationService, _reviewService);
        _cleanupBatchService = new CleanupBatchService(_trashService);
        InitializeComponent();
        InitializeReviewControls();
        FormClosing += Form1_FormClosing;
        Shown += Form1_Shown;
        _reviewSaveTimer.Tick += reviewSaveTimer_Tick;
        if (_demoMode)
        {
            SetPagedReadOnlyMode(true);
            btnConectar.Enabled = false;
            btnConectar.Text = "Conexión deshabilitada";
            btnBuscar.Enabled = false;
            btnBuscar.Text = "Escaneo remoto deshabilitado";
            btnReopenLatestScan.Enabled = false;
            chkLegacyFullScan.Enabled = false;
            lblTitulo.Text = "MODO DEMOSTRACIÓN — Drive Duplicate Finder";
            lblDescripcion.Text = "Datos ficticios en SQLite temporal. Sin OAuth, escaneo remoto, preflight ni limpieza real.";
            lblPagedReadOnly.Text = "MODO DEMOSTRACIÓN — decisiones únicamente locales; ningún cambio se enviará a Google Drive.";
            Text = "DriveDuplicateFinder — MODO DEMOSTRACIÓN";
        }
    }

    private async void btnConectar_Click(object? sender, EventArgs e)
    {
        if (_demoMode) return;
        btnConectar.Enabled = false;
        btnBuscar.Enabled = false;
        btnReopenLatestScan.Enabled = false;
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
            UpdateWorkflowStep(0, "Conectado; listo para analizar");
            btnBuscar.Enabled = true;
            btnReopenLatestScan.Enabled = true;

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
        if (_demoMode) return;
        if (_driveService is null)
        {
            lblEstado.Text = "Con\u00E9ctese con Google Drive antes de buscar duplicados.";
            return;
        }

        if (chkLegacyFullScan.Checked && MessageBox.Show(
                "El escaneo heredado carga en memoria todos los archivos comparables y grupos; con inventarios grandes puede consumir mucha memoria. ¿Continuar con esta ruta de compatibilidad?",
                "Confirmar escaneo heredado",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        btnConectar.Enabled = false;
        btnBuscar.Enabled = false;
        btnReopenLatestScan.Enabled = false;
        btnCancelar.Enabled = true;
        CancelPagedQueries();
        SetPagedReadOnlyMode(false);
        _pagedInventory = null;
        _pagedGroupPage = null;
        _activePagedGroup = null;
        _pagedDuplicateGroupCount = 0;
        _pagedGroupOffset = 0;
        _pagedMemberOffset = 0;
        dgvDuplicados.DataSource = null;
        dgvPagedGroups.DataSource = null;
        dgvPagedMembers.DataSource = null;
        _recommendationBatchUndoSession.Clear();
        _batchSelectedStableIds.Clear();
        _searchCancellationTokenSource = new CancellationTokenSource();
        _lastScanResult = null;
        _scanResultsAreObsolete = true;
        _reviews.Clear();
        _recommendationsByGroupId.Clear();
        _recommendationUndoSession.Clear();
        _recommendationBatchUndoSession.Clear();
        _batchSelectedStableIds.Clear();
        _selectedReview = null;
        RefreshReviewViews(null);
        RefreshRecommendationActionAvailability();

        try
        {
            UpdateWorkflowStep(0, "Análisis en curso");
            var progress = new Progress<DriveScanProgress>(UpdateProgress);
            if (!chkLegacyFullScan.Checked)
            {
                lblEstado.Text = "Preparando escaneo completo recuperable...";
                FullScanPreparation preparation = await _recoverableFullScanService.PrepareAsync(
                    _driveService,
                    _searchCancellationTokenSource.Token);
                ScanSessionRecord? resumeSession = preparation.IncompleteSession;
                if (resumeSession is not null)
                {
                    DialogResult resumeChoice = MessageBox.Show(
                        $"Hay un escaneo completo incompleto de esta cuenta y alcance.{Environment.NewLine}" +
                        $"Páginas confirmadas: {resumeSession.ProcessedPageCount:N0}; elementos: {resumeSession.ProcessedItemCount:N0}.{Environment.NewLine}{Environment.NewLine}" +
                        "Sí: reanudar. No: descartar y comenzar de nuevo. Cancelar: volver sin iniciar.",
                        "Escaneo recuperable",
                        MessageBoxButtons.YesNoCancel,
                        MessageBoxIcon.Question);
                    if (resumeChoice == DialogResult.Cancel)
                    {
                        lblEstado.Text = "Escaneo no iniciado.";
                        return;
                    }
                    if (resumeChoice == DialogResult.No)
                    {
                        await _recoverableFullScanService.AbandonAsync(resumeSession, _searchCancellationTokenSource.Token);
                        resumeSession = null;
                    }
                }

                RecoverableFullScanResult completedScan = await _recoverableFullScanService.RunAsync(
                    _driveService,
                    preparation,
                    resumeSession,
                    progress,
                    _searchCancellationTokenSource.Token);
                await _reviewStateRepository.RegisterCompletedInventoryAsync(completedScan.Inventory, _searchCancellationTokenSource.Token);
                ActivatePagedInventory(completedScan);
                await LoadPagedGroupPageAsync(0);
                progressBar.Style = ProgressBarStyle.Continuous;
                progressBar.Value = 100;
                lblEstado.Text = "Búsqueda paginada completada. Las decisiones de conservación se guardan localmente; limpieza deshabilitada.";
                return;
            }

            // Ruta de compatibilidad: solo se ejecuta por selección explícita del usuario.
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
            _recommendationsByGroupId.Clear();
            _recommendationUndoSession.Clear();
            _recommendationBatchUndoSession.Clear();
            _batchSelectedStableIds.Clear();
            _reviews.AddRange(_reviewService.CreateReviews(duplicateGroups));
            _lastScanResult = scanResult;
            _scanResultsAreObsolete = false;
            btnAbrirPapelera.Enabled = false;

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
            UpdateWorkflowStep(1, "Ruta heredada; revisión disponible");
        }
        catch (OperationCanceledException)
        {
            progressBar.Style = ProgressBarStyle.Continuous;
            progressBar.Value = 0;
            lblEstado.Text = "B\u00FAsqueda cancelada.";
            UpdateWorkflowStep(0, "Análisis cancelado");
        }
        catch (Exception ex)
        {
            progressBar.Style = ProgressBarStyle.Continuous;
            progressBar.Value = 0;
            lblEstado.Text = "Error al buscar duplicados.";
            UpdateWorkflowStep(0, "Análisis no completado");

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
            btnReopenLatestScan.Enabled = _driveService is not null;
            btnCancelar.Enabled = false;
            if (_pagedReadOnlyMode)
                RefreshCleanupEligibility();
            else
                SetCleanupUiBusy(false);
            RefreshBatchSelectionUi();
        }
    }

    private async void btnReopenLatestScan_Click(object? sender, EventArgs e)
    {
        if (_demoMode) return;
        if (_driveService is null) return;
        btnReopenLatestScan.Enabled = false;
        btnBuscar.Enabled = false;
        btnConectar.Enabled = false;
        btnCancelar.Enabled = true;
        CancelPagedQueries();
        try
        {
            lblEstado.Text = "Abriendo el último análisis Full completado de esta cuenta; no se iniciará un escaneo...";
            var cancellation = new CancellationTokenSource();
            _searchCancellationTokenSource = cancellation;
            RecoverableFullScanResult result = await _recoverableFullScanService.OpenLatestCompletedAsync(_driveService, cancellation.Token);
            await _reviewStateRepository.RegisterCompletedInventoryAsync(result.Inventory, cancellation.Token);
            ActivatePagedInventory(result);
            await LoadPagedGroupPageAsync(0);
            lblEstado.Text = "Se reabrió el inventario local completado; no se ejecutó un escaneo.";
        }
        catch (OperationCanceledException)
        {
            lblEstado.Text = "Apertura del análisis cancelada.";
        }
        catch (Exception exception)
        {
            lblEstado.Text = "No se pudo reabrir el análisis completado.";
            MessageBox.Show(exception.Message, "Reabrir análisis", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _searchCancellationTokenSource?.Dispose();
            _searchCancellationTokenSource = null;
            btnReopenLatestScan.Enabled = _driveService is not null;
            btnBuscar.Enabled = _driveService is not null;
            btnConectar.Enabled = true;
            btnCancelar.Enabled = false;
        }
    }

    private void ActivatePagedInventory(RecoverableFullScanResult result)
    {
        _lastScanResult = result.ScanResult;
        _pagedInventory = result.Inventory;
        _pagedDuplicateGroupCount = result.DuplicateGroupCount;
        _pagedKeepCount = 0;
        _pagedCandidateCount = 0;
        _pagedUndecidedCount = 0;
        _pagedCurrentMemberCount = 0;
        _pagedReadOnlyMode = true;
        _scanResultsAreObsolete = false;
        _reviewChangesPending = false;
        _reviewSaveTimer.Stop();
        DeactivateCleanupMode(showStatus: false);
        SetPagedReadOnlyMode(true);
        UpdateWorkflowStep(1, "Revisión local grupo por grupo; recomendaciones y limpieza siguen bloqueadas.");
    }

    private void btnCancelar_Click(object? sender, EventArgs e)
    {
        _searchCancellationTokenSource?.Cancel();
        _cleanupCancellationTokenSource?.Cancel();
        _pagedRequestGeneration.CancelCurrent();
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
        dgvDuplicados.CurrentCellDirtyStateChanged += dgvDuplicados_CurrentCellDirtyStateChanged;
        dgvDuplicados.CellValueChanged += dgvDuplicados_CellValueChanged;
        dgvDuplicados.ReadOnly = false;
        dgvDuplicados.EditMode = DataGridViewEditMode.EditOnEnter;

        colSeleccionLote.DataPropertyName = nameof(ReviewGroupRow.IsBatchSelected);
        colSeleccionLote.HeaderText = "Lote";
        colSeleccionLote.Name = "colSeleccionLote";
        colSeleccionLote.ReadOnly = false;
        colSeleccionLote.Width = 48;
        dgvDuplicados.Columns.Insert(0, colSeleccionLote);

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
        ConfigurePagedPanels();

        var headerLayout = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 10, 12, 8),
            RowCount = 7
        };
        headerLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        headerLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        headerLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        headerLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        headerLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        headerLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        headerLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        lblTitulo.Dock = DockStyle.Fill;
        lblDescripcion.AutoSize = true;
        lblDescripcion.Dock = DockStyle.Fill;
        lblWorkflowStep.AutoSize = true;
        lblWorkflowStep.Dock = DockStyle.Fill;
        lblWorkflowStep.Padding = new Padding(8, 6, 8, 6);
        lblWorkflowStep.BackColor = Color.AliceBlue;
        lblWorkflowStep.Text = "1 Conectar y analizar   ›   2 Revisar   ›   3 Confirmar limpieza   ›   4 Resultados";
        lblEstado.AutoSize = true;
        lblEstado.Dock = DockStyle.Fill;
        progressBar.Dock = DockStyle.Fill;
        progressBar.Margin = new Padding(0, 4, 0, 0);

        var commandPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = new Padding(0, 8, 0, 0)
        };
        ConfigureHeaderButton(btnConectar);
        ConfigureHeaderButton(btnBuscar);
        ConfigureHeaderButton(btnCancelar);
        btnReopenLatestScan.Text = "Reabrir último análisis";
        btnReopenLatestScan.AutoSize = true;
        btnReopenLatestScan.Enabled = false;
        btnReopenLatestScan.Click += btnReopenLatestScan_Click;
        ConfigureButton(btnActivarLimpieza, "Activar modo limpieza", btnActivarLimpieza_Click);
        ConfigureButton(btnDesactivarLimpieza, "Desactivar modo limpieza", btnDesactivarLimpieza_Click);
        ConfigureButton(btnRestablecerAutorizacionLimpieza, "Restablecer autorización de limpieza", btnRestablecerAutorizacionLimpieza_Click);
        chkLegacyFullScan.AutoSize = true;
        chkLegacyFullScan.Text = "Ruta heredada (memoria completa)";
        chkLegacyFullScan.Margin = new Padding(4, 6, 8, 0);
        _recommendationToolTip.SetToolTip(chkLegacyFullScan,
            "Compatibilidad explícita: carga todo el inventario y sus grupos en memoria. Déjala desmarcada para usar la consulta paginada SQLite.");
        commandPanel.Controls.AddRange([btnConectar, btnBuscar, btnReopenLatestScan, btnCancelar, chkLegacyFullScan, btnActivarLimpieza, btnDesactivarLimpieza, btnRestablecerAutorizacionLimpieza]);

        lblModoLimpieza.AutoSize = true;
        lblModoLimpieza.Dock = DockStyle.Fill;
        lblModoLimpieza.Padding = new Padding(8, 5, 8, 5);

        headerLayout.Controls.Add(lblTitulo, 0, 0);
        headerLayout.Controls.Add(lblDescripcion, 0, 1);
        headerLayout.Controls.Add(lblWorkflowStep, 0, 2);
        headerLayout.Controls.Add(commandPanel, 0, 3);
        headerLayout.Controls.Add(lblEstado, 0, 4);
        headerLayout.Controls.Add(progressBar, 0, 5);
        headerLayout.Controls.Add(lblModoLimpieza, 0, 6);

        var leftLayout = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            RowCount = 4
        };
        leftLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        leftLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        leftLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        leftLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        lblResumen.Dock = DockStyle.Fill;
        lblResumen.AutoSize = false;
        lblResumen.MinimumSize = new Size(0, 118);
        dgvDuplicados.Dock = DockStyle.Fill;
        dgvDuplicados.ScrollBars = ScrollBars.Both;
        ConfigureMainGridColumns(colEstado);
        var batchSelectionPanel = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 4),
            RowCount = 2
        };
        batchSelectionPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        batchSelectionPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var batchPreparationRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, WrapContents = true };
        var batchCleanupRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, WrapContents = true, Padding = new Padding(0, 3, 0, 0) };
        ConfigureButton(btnSeleccionarVisibles, "Seleccionar visibles", btnSeleccionarVisibles_Click);
        ConfigureButton(btnDeseleccionarTodos, "Deseleccionar todos", btnDeseleccionarTodos_Click);
        ConfigureButton(btnAplicarRecomendacionesSeleccionadas, "Aplicar recomendaciones seleccionadas", btnAplicarRecomendacionesSeleccionadas_Click);
        ConfigureButton(btnDeshacerUltimoLote, "Deshacer último lote", btnDeshacerUltimoLote_Click);
        ConfigureButton(btnEnviarGruposPapelera, "Enviar grupos seleccionados a la papelera", btnEnviarGruposPapelera_Click);
        btnEnviarGruposPapelera.MinimumSize = new Size(300, 0);
        _recommendationToolTip.SetToolTip(btnSeleccionarVisibles, "Selecciona únicamente los grupos mostrados por el filtro actual.");
        _recommendationToolTip.SetToolTip(btnAplicarRecomendacionesSeleccionadas, "Prepara decisiones locales para los grupos seleccionados. No modifica Google Drive.");
        _recommendationToolTip.SetToolTip(btnDeshacerUltimoLote, "Restaura las decisiones anteriores al último lote aplicado durante esta sesión.");
        _recommendationToolTip.SetToolTip(btnEnviarGruposPapelera, "Envía a la papelera los candidatos de todos los grupos marcados en la columna Lote.");
        lblSeleccionLote.AutoSize = true;
        lblSeleccionLote.Padding = new Padding(4, 6, 0, 0);
        batchPreparationRow.Controls.AddRange([btnSeleccionarVisibles, btnDeseleccionarTodos, btnAplicarRecomendacionesSeleccionadas, btnDeshacerUltimoLote]);
        batchCleanupRow.Controls.AddRange([new Label { AutoSize = true, Text = "Limpieza real por lotes:", Padding = new Padding(0, 6, 0, 0), ForeColor = Color.DarkRed }, btnEnviarGruposPapelera, lblSeleccionLote]);
        batchSelectionPanel.Controls.Add(batchPreparationRow, 0, 0);
        batchSelectionPanel.Controls.Add(batchCleanupRow, 0, 1);
        leftLayout.Controls.Add(lblResumen, 0, 0);
        leftLayout.Controls.Add(grpFiltros, 0, 1);
        leftLayout.Controls.Add(batchSelectionPanel, 0, 2);
        leftLayout.Controls.Add(dgvDuplicados, 0, 3);
        leftLayout.Controls.Add(grpPagedGroups, 0, 3);

        var rightLayout = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            RowCount = 1
        };
        rightLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        rightLayout.Controls.Add(grpRevision, 0, 0);
        rightLayout.Controls.Add(grpPagedMembers, 0, 0);

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
        RefreshCleanupEligibility();
    }

    private async void Form1_Shown(object? sender, EventArgs e)
    {
        BeginInvoke(new Action(InitializeMainSplitLayout));
        if (!_demoMode || _demoDataDirectory is null) return;
        try
        {
            _demoSeed = await DemoReviewData.CreateAsync(_demoDataDirectory);
            RecoverableFullScanResult result = await _recoverableFullScanService.OpenCompletedInventoryAsync(_demoSeed.Inventory);
            ActivatePagedInventory(result);
            SetPagedReadOnlyMode(true);
            await LoadPagedGroupPageAsync(0);
            lblEstado.Text = "Demo lista: inventario sintético; revisa grupos, páginas y plan local.";
        }
        catch (Exception exception)
        {
            lblEstado.Text = "No se pudo abrir la base temporal de demostración.";
            MessageBox.Show(exception.Message, "Modo demostración", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ConfigurePagedPanels()
    {
        grpPagedGroups.Text = "Duplicados exactos — páginas SQLite";
        grpPagedGroups.Dock = DockStyle.Fill;
        grpPagedGroups.Visible = false;
        var groupLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
        groupLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        groupLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        groupLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        lblPagedReadOnly.AutoSize = true;
        lblPagedReadOnly.Text = "Revisión paginada local. Elige explícitamente Conservar, Enviar a papelera (candidato local) o Sin decidir. La vista previa es local; la limpieza real está deshabilitada.";
        lblPagedReadOnly.ForeColor = Color.DarkRed;
        lblPagedReadOnly.Dock = DockStyle.Fill;
        ConfigurePagedGrid(dgvPagedGroups);
        dgvPagedGroups.Columns.AddRange(
        [
            new DataGridViewTextBoxColumn { DataPropertyName = nameof(PagedGroupDisplayRow.RepresentativeName), HeaderText = "Archivo representativo", FillWeight = 140 },
            new DataGridViewTextBoxColumn { DataPropertyName = nameof(PagedGroupDisplayRow.MemberCountText), HeaderText = "Copias", FillWeight = 50 },
            new DataGridViewTextBoxColumn { DataPropertyName = nameof(PagedGroupDisplayRow.SizeText), HeaderText = "Tamaño", FillWeight = 75 },
            new DataGridViewTextBoxColumn { DataPropertyName = nameof(PagedGroupDisplayRow.RecoverableText), HeaderText = "Recuperable", FillWeight = 85 },
            new DataGridViewTextBoxColumn { DataPropertyName = nameof(PagedGroupDisplayRow.Checksum), HeaderText = "MD5", FillWeight = 130 }
        ]);
        dgvPagedGroups.SelectionChanged += dgvPagedGroups_SelectionChanged;
        var groupNavigation = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        ConfigureButton(btnPagedPreviousGroupPage, "Grupo anterior", btnPagedPreviousGroupPage_Click);
        ConfigureButton(btnPagedNextGroupPage, "Siguiente grupo", btnPagedNextGroupPage_Click);
        lblPagedGroupPage.AutoSize = true;
        lblPagedGroupPage.Padding = new Padding(4, 6, 0, 0);
        groupNavigation.Controls.AddRange([btnPagedPreviousGroupPage, btnPagedNextGroupPage, lblPagedGroupPage]);
        groupLayout.Controls.Add(lblPagedReadOnly, 0, 0);
        groupLayout.Controls.Add(dgvPagedGroups, 0, 1);
        groupLayout.Controls.Add(groupNavigation, 0, 2);
        grpPagedGroups.Controls.Add(groupLayout);

        grpPagedMembers.Text = "Miembros del grupo — revisión local paginada";
        grpPagedMembers.Dock = DockStyle.Fill;
        grpPagedMembers.Visible = false;
        var memberLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
        memberLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        memberLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        memberLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        ConfigurePagedGrid(dgvPagedMembers);
        var pagedDecisionOptions = new[]
        {
            new DecisionOption(DuplicateFileDecision.Undecided, "SIN DECIDIR"),
            new DecisionOption(DuplicateFileDecision.Keep, "CONSERVAR"),
            new DecisionOption(DuplicateFileDecision.CandidateForTrash, "ENVIAR A PAPELERA (candidato local)")
        };
        dgvPagedMembers.Columns.AddRange(
        [
            new DataGridViewComboBoxColumn { DataPropertyName = nameof(PagedMemberDisplayRow.Decision), DataSource = pagedDecisionOptions, ValueMember = nameof(DecisionOption.Value), DisplayMember = nameof(DecisionOption.DisplayName), HeaderText = "Decisión local", Name = "colPagedDecision", FillWeight = 90, ReadOnly = false },
            new DataGridViewTextBoxColumn { DataPropertyName = nameof(PagedMemberDisplayRow.Name), HeaderText = "Nombre", FillWeight = 100 },
            new DataGridViewTextBoxColumn { DataPropertyName = nameof(PagedMemberDisplayRow.Path), HeaderText = "Ruta", FillWeight = 180 },
            new DataGridViewTextBoxColumn { DataPropertyName = nameof(PagedMemberDisplayRow.SizeText), HeaderText = "Tamaño", FillWeight = 75 },
            new DataGridViewTextBoxColumn { DataPropertyName = nameof(PagedMemberDisplayRow.ModifiedText), HeaderText = "Modificado", FillWeight = 90 },
            new DataGridViewLinkColumn { DataPropertyName = nameof(PagedMemberDisplayRow.LocationText), HeaderText = "Drive", FillWeight = 70, TrackVisitedState = false }
        ]);
        dgvPagedMembers.ReadOnly = false;
        foreach (DataGridViewColumn column in dgvPagedMembers.Columns)
            column.ReadOnly = column.Name != "colPagedDecision";
        dgvPagedMembers.EditMode = DataGridViewEditMode.EditOnEnter;
        dgvPagedMembers.CurrentCellDirtyStateChanged += dgvPagedMembers_CurrentCellDirtyStateChanged;
        dgvPagedMembers.CellValueChanged += dgvPagedMembers_CellValueChanged;
        dgvPagedMembers.CellContentClick += dgvPagedMembers_CellContentClick;
        var memberNavigation = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        ConfigureButton(btnPagedPreviousMemberPage, "Miembros anteriores", btnPagedPreviousMemberPage_Click);
        ConfigureButton(btnPagedNextMemberPage, "Más miembros", btnPagedNextMemberPage_Click);
        ConfigureButton(btnPagedConfirmGroup, "Confirmar revisión", btnPagedConfirmGroup_Click);
        ConfigureButton(btnPagedSkipGroup, "Saltar grupo", btnPagedSkipGroup_Click);
        ConfigureButton(btnPagedViewCleanupPlan, "Ver plan de limpieza", btnPagedViewCleanupPlan_Click);
        lblPagedMembersPage.AutoSize = true;
        lblPagedMembersPage.Padding = new Padding(4, 6, 0, 0);
        memberNavigation.Controls.AddRange([btnPagedPreviousMemberPage, btnPagedNextMemberPage, btnPagedConfirmGroup, btnPagedSkipGroup, btnPagedViewCleanupPlan, lblPagedMembersPage]);
        var reviewNotice = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = Color.DarkSlateBlue,
            Text = "Solo las decisiones explícitas se guardan. Los miembros ocultos o sin decidir quedan fuera del plan; la vista previa no consulta ni modifica Google Drive."
        };
        memberLayout.Controls.Add(reviewNotice, 0, 0);
        memberLayout.Controls.Add(dgvPagedMembers, 0, 1);
        memberLayout.Controls.Add(memberNavigation, 0, 2);
        grpPagedMembers.Controls.Add(memberLayout);
        UpdatePagedNavigationUi();
    }

    private static void ConfigurePagedGrid(DataGridView grid)
    {
        grid.Dock = DockStyle.Fill;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.AutoGenerateColumns = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.ReadOnly = true;
        grid.MultiSelect = false;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.EditMode = DataGridViewEditMode.EditProgrammatically;
        grid.ScrollBars = ScrollBars.Both;
    }

    private void SetPagedReadOnlyMode(bool active)
    {
        _pagedReadOnlyMode = active;
        grpFiltros.Visible = !active;
        dgvDuplicados.Visible = !active;
        grpRevision.Visible = !active;
        grpPagedGroups.Visible = active;
        grpPagedMembers.Visible = active;
        if (btnSeleccionarVisibles.Parent is Control preparationRow) preparationRow.Visible = !active;
        if (btnEnviarGruposPapelera.Parent is Control cleanupRow) cleanupRow.Visible = !active;
        txtNotas.ReadOnly = active;
        dgvGrupoDetalle.ReadOnly = active;
        btnActivarLimpieza.Enabled = !active && !_cleanupModeActive && !_cleanupOperationInProgress;
        btnDesactivarLimpieza.Enabled = !active && _cleanupModeActive && !_cleanupOperationInProgress;
        btnRestablecerAutorizacionLimpieza.Enabled = !active && !_cleanupModeActive && !_cleanupOperationInProgress;
        btnEnviarGrupoPapelera.Enabled = !active && btnEnviarGrupoPapelera.Enabled;
        btnEnviarGruposPapelera.Enabled = !active && btnEnviarGruposPapelera.Enabled;
        btnAplicarRecomendacion.Enabled = !active && btnAplicarRecomendacion.Enabled;
        btnDeshacerRecomendacion.Enabled = !active && btnDeshacerRecomendacion.Enabled;
        btnAplicarRecomendacionesSeleccionadas.Enabled = !active && btnAplicarRecomendacionesSeleccionadas.Enabled;
        btnDeshacerUltimoLote.Enabled = !active && btnDeshacerUltimoLote.Enabled;
        btnMarcarRevisado.Enabled = !active && btnMarcarRevisado.Enabled;
        btnGuardarRevision.Enabled = !active && btnGuardarRevision.Enabled;
        btnVerPlan.Enabled = !active && btnVerPlan.Enabled;
        btnExportarPlan.Enabled = !active && btnExportarPlan.Enabled;
        btnAbrirPapelera.Enabled = !active && btnAbrirPapelera.Enabled;
        if (active)
        {
            _reviewSaveTimer.Stop();
            _reviewChangesPending = false;
            _cleanupModeActive = false;
            _cleanupDriveService?.Dispose();
            _cleanupDriveService = null;
            btnAnteriorGrupo.Enabled = false;
            btnSiguienteGrupo.Enabled = false;
            btnSiguientePendiente.Enabled = false;
            btnSiguienteListo.Enabled = false;
            lblModoLimpieza.Text = "REVISIÓN PAGINADA: decisiones locales habilitadas; recomendaciones y limpieza real deshabilitadas.";
            lblModoLimpieza.BackColor = Color.LemonChiffon;
            lblModoLimpieza.ForeColor = Color.DarkRed;
            lblElegibilidadLimpieza.Text = "La ruta paginada no permite operaciones de limpieza.";
            lblElegibilidadLimpieza.ForeColor = Color.DarkRed;
        }
        RefreshCleanupEligibility();
        UpdatePagedNavigationUi();
    }

    private async Task LoadPagedGroupPageAsync(int offset)
    {
        if (!_pagedReadOnlyMode || _pagedInventory is null) return;
        PagedRequestLease request = _pagedRequestGeneration.Begin();
        CancellationToken cancellationToken = request.Token;
        btnCancelar.Enabled = true;
        btnPagedPreviousGroupPage.Enabled = false;
        btnPagedNextGroupPage.Enabled = false;
        lblPagedGroupPage.Text = "Cargando grupos…";
        dgvPagedMembers.Enabled = false;
        try
        {
            DuplicateGroupSummaryPage page = await _recoverableFullScanService.GetDuplicateGroupSummariesPageAsync(
                _pagedInventory, offset, PagedGroupPageSize, cancellationToken);
            if (!_pagedRequestGeneration.IsCurrent(request)) return;
            if (page.Items.Count == 0 && page.TotalCount > 0)
                throw new InvalidOperationException($"SQLite informó {page.TotalCount:N0} grupos, pero no devolvió el grupo solicitado (offset {offset}). Se conserva la página anterior.");

            _pagedGroupPage = page;
            _pagedGroupOffset = page.Offset;
            _pagedDuplicateGroupCount = page.TotalCount;
            _activePagedGroup = page.Items.FirstOrDefault()?.Identity;
            _pagedKeepCount = 0;
            _pagedCandidateCount = 0;
            _pagedUndecidedCount = 0;
            _pagedCurrentMemberCount = 0;
            _isBindingPagedGroups = true;
            try
            {
                dgvPagedGroups.DataSource = new BindingList<PagedGroupDisplayRow>(page.Items.Select(item => new PagedGroupDisplayRow(
                    item.Identity,
                    item.RepresentativeName,
                    item.MemberCount.ToString("N0", CultureInfo.CurrentCulture),
                    FileSizeFormatter.Format(item.Identity.SizeBytes),
                    FileSizeFormatter.Format(item.RecoverableBytes),
                    item.Identity.NormalizedChecksum)).ToList());
                if (dgvPagedGroups.Rows.Count > 0)
                {
                    dgvPagedGroups.ClearSelection();
                    dgvPagedGroups.Rows[0].Selected = true;
                    dgvPagedGroups.CurrentCell = dgvPagedGroups.Rows[0].Cells[0];
                }
            }
            finally
            {
                _isBindingPagedGroups = false;
            }

            _pagedMemberOffset = 0;
            if (_activePagedGroup is not null)
            {
                dgvPagedMembers.DataSource = null;
                lblPagedMembersPage.Text = "Cargando miembros del grupo…";
                try
                {
                    await LoadPagedMembersPageCoreAsync(_activePagedGroup, 0, request);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception) when (_pagedRequestGeneration.IsCurrent(request))
                {
                    dgvPagedMembers.DataSource = null;
                    lblPagedMembersPage.Text = "No se pudieron cargar los miembros; el grupo sigue disponible.";
                    MessageBox.Show(exception.Message, "Error al cargar miembros del grupo", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            else
            {
                dgvPagedMembers.DataSource = null;
                lblPagedMembersPage.Text = page.TotalCount == 0
                    ? "No hay grupos duplicados en este inventario."
                    : "SQLite no devolvió el grupo solicitado; no se descartó la página anterior.";
            }
            if (!_pagedRequestGeneration.IsCurrent(request)) return;
            UpdatePagedSummaryText();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_pagedReadOnlyMode && !_pagedRequestGeneration.HasActiveRequest)
                lblPagedGroupPage.Text = "Consulta de grupos cancelada.";
        }
        catch (Exception exception)
        {
            if (_pagedRequestGeneration.IsCurrent(request))
            {
                lblPagedGroupPage.Text = "Error al cambiar de grupo; se conserva la selección anterior.";
                MessageBox.Show(
                    $"No se pudo consultar la página solicitada del inventario SQLite. Se conserva la página anterior y no se usarán resultados en memoria.{Environment.NewLine}{Environment.NewLine}{exception.Message}",
                    "Error de consulta paginada",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
        finally
        {
            _pagedRequestGeneration.Complete(request);
            btnCancelar.Enabled = _searchCancellationTokenSource is not null || _pagedRequestGeneration.HasActiveRequest;
            UpdatePagedNavigationUi();
        }
    }

    private async Task LoadPagedMembersPageAsync(DuplicateGroupIdentity identity, int offset)
    {
        if (!_pagedReadOnlyMode || _pagedInventory is null || identity.Inventory != _pagedInventory) return;
        PagedRequestLease request = _pagedRequestGeneration.Begin();
        btnCancelar.Enabled = true;
        _activePagedGroup = identity;
        _pagedMemberOffset = offset;
        _pagedKeepCount = 0;
        _pagedCandidateCount = 0;
        _pagedUndecidedCount = 0;
        _pagedCurrentMemberCount = 0;
        dgvPagedMembers.DataSource = null;
        dgvPagedMembers.Enabled = false;
        lblPagedMembersPage.Text = "Cargando miembros…";
        try
        {
            await LoadPagedMembersPageCoreAsync(identity, offset, request);
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested)
        {
            if (_pagedReadOnlyMode && !_pagedRequestGeneration.HasActiveRequest)
                lblPagedMembersPage.Text = "Consulta de miembros cancelada.";
        }
        catch (Exception exception)
        {
            if (_pagedRequestGeneration.IsCurrent(request))
            {
                dgvPagedMembers.DataSource = null;
                lblPagedMembersPage.Text = "No se pudo consultar la página de miembros; no se usarán datos alternativos.";
                MessageBox.Show(exception.Message, "Error de consulta paginada", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            _pagedRequestGeneration.Complete(request);
            btnCancelar.Enabled = _searchCancellationTokenSource is not null || _pagedRequestGeneration.HasActiveRequest;
            UpdatePagedNavigationUi();
        }
    }

    private async Task LoadPagedMembersPageCoreAsync(
        DuplicateGroupIdentity identity,
        int offset,
        PagedRequestLease request)
    {
        lblPagedMembersPage.Text = "Cargando miembros…";
        _isBindingPagedMembers = true;
        dgvPagedMembers.Enabled = false;
        try
        {
            RecoverableGroupMembersDisplayPage page = await _recoverableFullScanService.GetDuplicateGroupMembersDisplayPageAsync(
                identity, offset, PagedMemberPageSize, request.Token);
            if (!_pagedRequestGeneration.IsCurrent(request) || !_pagedReadOnlyMode || identity.Inventory != _pagedInventory) return;
            _pagedMemberOffset = page.Offset;
            dgvPagedMembers.DataSource = new BindingList<PagedMemberDisplayRow>(page.Items.Select(file => new PagedMemberDisplayRow(
                file.Id, file.Name, file.Path, FileSizeFormatter.Format(file.Size),
                file.ModifiedTime?.ToLocalTime().ToString("g") ?? "No disponible", file.ParentFolderUrl, DuplicateFileDecision.Undecided)).ToList());
            if (page.TotalCount == 0)
            {
                lblPagedMembersPage.Text = "El grupo ya no contiene miembros en el inventario vigente.";
                return;
            }

            ReviewGroupPersistenceState groupState = await _reviewStateRepository.RegisterGroupAsync(identity, request.Token);
            PersistedFileDecisionPage decisions = await _reviewStateRepository.GetDecisionsPageAsync(identity, offset, PagedMemberPageSize, request.Token);
            ReviewDecisionCounts counts = await _reviewStateRepository.GetGroupDecisionCountsAsync(identity, request.Token);
            if (!_pagedRequestGeneration.IsCurrent(request)) return;
            var decisionsByFileId = decisions.Items.ToDictionary(item => item.FileId, item => item.Decision, StringComparer.Ordinal);
            foreach (DataGridViewRow gridRow in dgvPagedMembers.Rows)
            {
                if (gridRow.DataBoundItem is not PagedMemberDisplayRow row || !decisionsByFileId.TryGetValue(row.FileId, out DuplicateFileDecision decision))
                    throw new InvalidOperationException("La página de decisiones no coincide con los FileId visibles del grupo.");
                row.Decision = decision;
                gridRow.Cells["colPagedDecision"].Value = decision;
            }
            dgvPagedMembers.Refresh();
            _pagedKeepCount = counts.KeepCount;
            _pagedCandidateCount = counts.CandidateCount;
            _pagedUndecidedCount = counts.UndecidedCount;
            _pagedCurrentMemberCount = counts.MemberCount;
            lblPagedMembersPage.Text = $"Miembros {page.Offset + 1:N0}–{page.Offset + page.Items.Count:N0} de {page.TotalCount:N0} | página {page.Offset / PagedMemberPageSize + 1:N0} de {Math.Max(1, (page.TotalCount + PagedMemberPageSize - 1) / PagedMemberPageSize)} | Conservar: {counts.KeepCount:N0} | candidatos ya registrados: {counts.CandidateCount:N0} | sin decidir: {counts.UndecidedCount:N0} | revisión {(groupState.IsReviewConfirmed ? "confirmada" : "pendiente")}";
            UpdatePagedNavigationUi();
        }
        finally
        {
            if (_pagedRequestGeneration.IsCurrent(request))
            {
                _isBindingPagedMembers = false;
                dgvPagedMembers.Enabled = _pagedReadOnlyMode && _activePagedGroup is not null && !_pagedReviewWriteInProgress;
            }
        }
    }

    private void UpdatePagedSummaryText()
    {
        if (_lastScanResult is null || _pagedInventory is null) return;
        lblResumen.Text = $"Full completado | Elementos: {_lastScanResult.ItemsExamined:N0} | Comparables: {_lastScanResult.ComparableFilesCount:N0} | Sin MD5: {_lastScanResult.FilesWithoutMd5Ignored:N0}{Environment.NewLine}" +
            $"Grupos exactos: {_pagedDuplicateGroupCount:N0} | Inventario: {_pagedInventory.ScanId}";
        UpdateWorkflowStep(1, "Revisar grupo por grupo; las decisiones se guardan localmente y la limpieza permanece bloqueada.");
    }

    private void UpdateWorkflowStep(int activeStep, string status)
    {
        string[] steps = ["Conectar y analizar", "Revisar", "Confirmar limpieza", "Resultados"];
        lblWorkflowStep.Text = string.Join("   ›   ", steps.Select((step, index) =>
            index < activeStep ? $"✓ {step}" : index == activeStep ? $"● {step}" : $"○ {step}")) +
            $"     |     {status}";
    }

    private void UpdatePagedNavigationUi()
    {
        if (IsDisposed || Disposing) return;
        long groupTotal = _pagedGroupPage?.TotalCount ?? _pagedDuplicateGroupCount;
        int groupCount = _pagedGroupPage?.Items.Count ?? 0;
        int groupNumber = groupTotal == 0 ? 0 : _pagedGroupOffset + 1;
        string range = groupCount == 0 ? "No hay grupos" : $"Grupo {groupNumber:N0} de {groupTotal:N0}";
        bool groupPageLoaded = _pagedGroupPage is not null;
        lblPagedGroupPage.Text = _pagedReadOnlyMode ? range : string.Empty;
        btnPagedPreviousGroupPage.Enabled = _pagedReadOnlyMode && groupPageLoaded && PagedReadOnlyViewSafety.HasPreviousPage(_pagedGroupOffset);
        btnPagedNextGroupPage.Enabled = _pagedReadOnlyMode && groupPageLoaded &&
            PagedReadOnlyViewSafety.HasNextPage(_pagedGroupOffset, groupCount, groupTotal);
        bool hasMembers = _activePagedGroup is not null;
        btnPagedPreviousMemberPage.Enabled = _pagedReadOnlyMode && hasMembers && _pagedMemberOffset > 0;
        btnPagedNextMemberPage.Enabled = _pagedReadOnlyMode && hasMembers &&
            (long)_pagedMemberOffset + (dgvPagedMembers.Rows.Count) < _pagedCurrentMemberCount;
        btnPagedConfirmGroup.Enabled = _pagedReadOnlyMode && hasMembers && !_pagedReviewWriteInProgress && _pagedKeepCount > 0 && _pagedCandidateCount > 0;
        btnPagedSkipGroup.Enabled = _pagedReadOnlyMode && hasMembers && !_pagedReviewWriteInProgress;
        btnPagedViewCleanupPlan.Enabled = _pagedReadOnlyMode && _pagedInventory is not null && !_pagedReviewWriteInProgress;
        dgvPagedMembers.Enabled = _pagedReadOnlyMode && hasMembers && !_pagedReviewWriteInProgress;
    }

    private void CancelPagedQueries() => _pagedRequestGeneration.CancelCurrent();

    private void dgvPagedGroups_SelectionChanged(object? sender, EventArgs e)
    {
        if (!_pagedReadOnlyMode || _isBindingPagedGroups || dgvPagedGroups.CurrentRow?.DataBoundItem is not PagedGroupDisplayRow row ||
            row.Identity == _activePagedGroup)
            return;
        _ = LoadPagedMembersPageAsync(row.Identity, 0);
    }

    private void dgvPagedMembers_CellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 ||
            dgvPagedMembers.Columns[e.ColumnIndex].DataPropertyName != nameof(PagedMemberDisplayRow.LocationText) ||
            dgvPagedMembers.Rows[e.RowIndex].DataBoundItem is not PagedMemberDisplayRow row)
            return;

        OpenLocation(row.ParentFolderUrl);
    }

    private void dgvPagedMembers_CurrentCellDirtyStateChanged(object? sender, EventArgs e)
    {
        if (dgvPagedMembers.IsCurrentCellDirty && dgvPagedMembers.CurrentCell is DataGridViewComboBoxCell)
            dgvPagedMembers.CommitEdit(DataGridViewDataErrorContexts.Commit);
    }

    private async void dgvPagedMembers_CellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (!_pagedReadOnlyMode || _isBindingPagedMembers || _pagedReviewWriteInProgress || e.RowIndex < 0 || e.ColumnIndex < 0 ||
            dgvPagedMembers.Columns[e.ColumnIndex].Name != "colPagedDecision" ||
            _activePagedGroup is not DuplicateGroupIdentity identity ||
            dgvPagedMembers.Rows[e.RowIndex].DataBoundItem is not PagedMemberDisplayRow row)
            return;

        if (dgvPagedMembers.Rows[e.RowIndex].Cells[e.ColumnIndex].Value is not DuplicateFileDecision decision) return;
        row.Decision = decision;
        PagedRequestLease request = _pagedRequestGeneration.Begin();
        _pagedReviewWriteInProgress = true;
        UpdatePagedNavigationUi();
        btnCancelar.Enabled = true;
        try
        {
            await _reviewStateRepository.SetExplicitDecisionAsync(identity, row.FileId, decision, request.Token);
            if (_pagedRequestGeneration.IsCurrent(request))
            {
                _pagedReviewWriteInProgress = false;
                await LoadPagedMembersPageCoreAsync(identity, _pagedMemberOffset, request);
            }
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (_pagedRequestGeneration.IsCurrent(request))
            {
                MessageBox.Show(exception.Message, "No se pudo guardar la decisión local", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _pagedReviewWriteInProgress = false;
                await LoadPagedMembersPageCoreAsync(identity, _pagedMemberOffset, request);
            }
        }
        finally
        {
            _pagedReviewWriteInProgress = false;
            _pagedRequestGeneration.Complete(request);
            UpdatePagedNavigationUi();
        }
    }

    private async void btnPagedConfirmGroup_Click(object? sender, EventArgs e)
    {
        if (_activePagedGroup is not DuplicateGroupIdentity identity || _pagedKeepCount < 1 || _pagedCandidateCount < 1)
        {
            MessageBox.Show("Marca explícitamente al menos un archivo para conservar y otro para enviar a papelera como candidato local.", "Revisión incompleta", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show(
                $"¿Confirmar las decisiones actuales para este grupo de {_pagedCurrentMemberCount:N0} archivos? Hay {_pagedKeepCount:N0} conservados explícitamente, {_pagedCandidateCount:N0} candidatos explícitos y {_pagedUndecidedCount:N0} sin decidir. Los pendientes quedan fuera del plan. No se enviará nada a la papelera.",
                "Confirmar revisión del grupo", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        PagedRequestLease request = _pagedRequestGeneration.Begin();
        _pagedReviewWriteInProgress = true;
        UpdatePagedNavigationUi();
        try
        {
            await _reviewStateRepository.ConfirmGroupReviewAsync(identity, request.Token);
            if (!_pagedRequestGeneration.IsCurrent(request)) return;
            lblEstado.Text = "Revisión del grupo confirmada y guardada localmente.";
            await LoadPagedMembersPageCoreAsync(identity, _pagedMemberOffset, request);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (_pagedRequestGeneration.IsCurrent(request))
                MessageBox.Show(exception.Message, "No se pudo confirmar la revisión", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _pagedReviewWriteInProgress = false;
            _pagedRequestGeneration.Complete(request);
            UpdatePagedNavigationUi();
        }
    }

    private async void btnPagedSkipGroup_Click(object? sender, EventArgs e)
    {
        if (_pagedGroupPage is null || _activePagedGroup is null) return;
        int nextOffset = _pagedGroupOffset + PagedGroupPageSize;
        if (nextOffset >= _pagedGroupPage.TotalCount)
        {
            lblEstado.Text = "No hay más grupos; el salto no modificó el estado de revisión.";
            return;
        }
        lblEstado.Text = "Grupo saltado sin confirmar ni cambiar decisiones.";
        await LoadPagedGroupPageAsync(nextOffset);
    }

    private void btnPagedViewCleanupPlan_Click(object? sender, EventArgs e)
    {
        if (!_pagedReadOnlyMode || _pagedInventory is null || _pagedReviewWriteInProgress) return;
        using var preview = new PagedCleanupPlanPreviewForm(_recoverableFullScanService, _pagedInventory);
        preview.ShowDialog(this);
    }

    private async void btnPagedPreviousGroupPage_Click(object? sender, EventArgs e) =>
        await LoadPagedGroupPageAsync(Math.Max(0, _pagedGroupOffset - PagedGroupPageSize));

    private async void btnPagedNextGroupPage_Click(object? sender, EventArgs e) =>
        await LoadPagedGroupPageAsync(_pagedGroupOffset + PagedGroupPageSize);

    private async void btnPagedPreviousMemberPage_Click(object? sender, EventArgs e)
    {
        if (_activePagedGroup is not null)
            await LoadPagedMembersPageAsync(_activePagedGroup, Math.Max(0, _pagedMemberOffset - PagedMemberPageSize));
    }

    private async void btnPagedNextMemberPage_Click(object? sender, EventArgs e)
    {
        if (_activePagedGroup is not null)
            await LoadPagedMembersPageAsync(_activePagedGroup, _pagedMemberOffset + PagedMemberPageSize);
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
        ConfigureButton(btnAplicarRecomendacion, "Aplicar recomendación", btnAplicarRecomendacion_Click);
        ConfigureButton(btnDeshacerRecomendacion, "Deshacer recomendación", btnDeshacerRecomendacion_Click);
        _recommendationToolTip.SetToolTip(btnAplicarRecomendacion, "Marca localmente el archivo recomendado como Conservar y las copias elegibles como candidatas. No modifica Google Drive.");
        _recommendationToolTip.SetToolTip(btnDeshacerRecomendacion, "Restaura las decisiones que tenía este grupo antes de aplicar la recomendación.");
        ConfigureButton(btnGuardarRevision, "Guardar revisi\u00F3n", btnGuardarRevision_Click);
        ConfigureButton(btnVerPlan, "Ver plan de limpieza", btnVerPlan_Click);
        ConfigureButton(btnExportarPlan, "Exportar plan", btnExportarPlan_Click);
        ConfigureButton(btnEnviarGrupoPapelera, "Enviar este grupo a la papelera", btnEnviarGrupoPapelera_Click);
        _recommendationToolTip.SetToolTip(btnEnviarGrupoPapelera, "Envía a la papelera únicamente los candidatos del grupo que se muestra en el panel derecho.");
        ConfigureButton(btnAbrirPapelera, "Abrir papelera de Google Drive", btnAbrirPapelera_Click);
        lblElegibilidadLimpieza.AutoSize = true;
        lblElegibilidadLimpieza.Dock = DockStyle.Fill;
        lblElegibilidadLimpieza.MaximumSize = new Size(0, 54);

        var navigationPanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        navigationPanel.Controls.AddRange([btnAnteriorGrupo, btnSiguienteGrupo, btnSiguientePendiente, btnSiguienteListo]);
        var actionPanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        actionPanel.Controls.AddRange([btnAplicarRecomendacion, btnDeshacerRecomendacion, btnMarcarRevisado, btnGuardarRevision, btnVerPlan, btnExportarPlan, btnEnviarGrupoPapelera, btnAbrirPapelera]);
        var reviewLayout = new TableLayoutPanel { ColumnCount = 1, Dock = DockStyle.Fill, RowCount = 8 };
        reviewLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
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
        reviewLayout.Controls.Add(lblElegibilidadLimpieza, 0, 7);
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
            _batchSelectedStableIds.Clear();
            _selectedReview = null;
            dgvDuplicados.DataSource = null;
            dgvGrupoDetalle.DataSource = null;
            lblGrupoNavegacion.Text = "Sin grupos para revisar.";
            RefreshCleanupEligibility();
            RefreshBatchSelectionUi();
            return;
        }

        _filteredReviews = ApplyFilters(_reviews).ToList();
        DuplicateGroupReview? selected = _filteredReviews.FirstOrDefault(review => review.StableId == preferredStableId)
            ?? _filteredReviews.FirstOrDefault();

        _isRefreshingReviewControls = true;
        try
        {
            _mainGridRows.RaiseListChangedEvents = false;
            _mainGridRows.Clear();
            foreach (ReviewGroupRow row in _filteredReviews.SelectMany(CreateMainRows))
            {
                _mainGridRows.Add(row);
            }

            _mainGridRows.RaiseListChangedEvents = true;
            _mainGridRows.ResetBindings();
            if (dgvDuplicados.DataSource is null)
            {
                dgvDuplicados.DataSource = _mainGridRows;
            }
        }
        finally
        {
            _isRefreshingReviewControls = false;
        }

        SelectReview(selected);
        RefreshCleanupEligibility();
        RefreshBatchSelectionUi();
    }

    private IEnumerable<ReviewGroupRow> CreateMainRows(DuplicateGroupReview review)
    {
        return review.Group.Files.Select(file => new ReviewGroupRow
        {
            IsBatchSelected = _batchSelectedStableIds.Contains(review.StableId),
            StableGroupId = review.StableId,
            GroupNumber = review.Group.GroupNumber,
            Name = file.Name,
            Path = file.Path,
            Size = FileSizeFormatter.Format(file.Size),
            ModifiedTime = file.ModifiedTime?.ToLocalTime().ToString("g") ?? "No disponible",
            Md5Checksum = file.Md5Checksum,
            Status = GetStatusText(review.Status),
            ParentFolderUrl = file.ParentFolderUrl
        });
    }

    private void dgvDuplicados_CurrentCellDirtyStateChanged(object? sender, EventArgs e)
    {
        if (dgvDuplicados.IsCurrentCellDirty && dgvDuplicados.CurrentCell?.OwningColumn == colSeleccionLote)
        {
            dgvDuplicados.CommitEdit(DataGridViewDataErrorContexts.Commit);
        }
    }

    private void dgvDuplicados_CellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (!IsLegacyActionsAllowed || _isRefreshingReviewControls || e.RowIndex < 0 || e.ColumnIndex != colSeleccionLote.Index ||
            dgvDuplicados.Rows[e.RowIndex].DataBoundItem is not ReviewGroupRow row)
        {
            return;
        }

        if (row.IsBatchSelected)
        {
            _batchSelectedStableIds.Add(row.StableGroupId);
        }
        else
        {
            _batchSelectedStableIds.Remove(row.StableGroupId);
        }

        RefreshReviewViews(row.StableGroupId);
    }

    private void btnSeleccionarVisibles_Click(object? sender, EventArgs e)
    {
        if (!IsLegacyActionsAllowed) return;
        foreach (DuplicateGroupReview review in _filteredReviews)
        {
            _batchSelectedStableIds.Add(review.StableId);
        }

        RefreshReviewViews(_selectedReview?.StableId);
    }

    private void btnDeseleccionarTodos_Click(object? sender, EventArgs e)
    {
        if (!IsLegacyActionsAllowed) return;
        _batchSelectedStableIds.Clear();
        RefreshReviewViews(_selectedReview?.StableId);
    }

    private void RefreshBatchSelectionUi()
    {
        int visibleSelected = _filteredReviews.Count(review => _batchSelectedStableIds.Contains(review.StableId));
        lblSeleccionLote.Text = $"Seleccionados: {_batchSelectedStableIds.Count} (visibles: {visibleSelected})";
        bool canChangeSelection = IsLegacyActionsAllowed && _filteredReviews.Count > 0 && _searchCancellationTokenSource is null &&
            !_cleanupOperationInProgress && !_batchOperationInProgress && !_closeAfterSave;
        bool interactionAllowed = IsLegacyActionsAllowed && _batchSelectedStableIds.Count > 0 && _searchCancellationTokenSource is null &&
            !_cleanupOperationInProgress && !_batchOperationInProgress && !_scanResultsAreObsolete && !_closeAfterSave && !_reviewStateIsCorrupt;
        btnSeleccionarVisibles.Enabled = canChangeSelection;
        btnDeseleccionarTodos.Enabled = _batchSelectedStableIds.Count > 0 && canChangeSelection;
        btnAplicarRecomendacionesSeleccionadas.Enabled = interactionAllowed;
        btnEnviarGruposPapelera.Enabled = interactionAllowed && _cleanupModeActive && _cleanupDriveService is not null;
        _recommendationToolTip.SetToolTip(btnEnviarGruposPapelera, GetBatchCleanupToolTip());
        btnDeshacerUltimoLote.Enabled = IsLegacyActionsAllowed && _recommendationBatchUndoSession.HasSnapshot && _searchCancellationTokenSource is null &&
            !_cleanupOperationInProgress && !_batchOperationInProgress && !_scanResultsAreObsolete;
    }

    private string GetBatchCleanupToolTip()
    {
        if (_batchSelectedStableIds.Count == 0)
        {
            return "Selecciona uno o varios grupos en la columna Lote.";
        }

        if (_scanResultsAreObsolete)
        {
            return "Los resultados están obsoletos. Repite la búsqueda.";
        }

        if (!_cleanupModeActive || _cleanupDriveService is null)
        {
            return "Activa el modo limpieza para modificar Google Drive.";
        }

        if (_cleanupOperationInProgress || _batchOperationInProgress)
        {
            return "Hay una operación de limpieza en curso.";
        }

        return "Envía a la papelera los candidatos de todos los grupos marcados en la columna Lote.";
    }

    private void RefreshAfterRecommendationChange(DuplicateGroupReview review)
    {
        lblResumen.Text = BuildSummary(_lastScanResult, _reviewService.BuildSummary(_reviews));
        bool statusChangesFilterOrOrder = cmbFiltroEstado.SelectedItem is ReviewStatusFilterOption { Status: not null } ||
            cmbOrden.SelectedIndex == 4;
        if (statusChangesFilterOrOrder || !_filteredReviews.Any(item => item.StableId == review.StableId))
        {
            RefreshReviewViews(review.StableId);
            return;
        }

        int firstRow = _mainGridRows.ToList().FindIndex(row => row.StableGroupId == review.StableId);
        if (firstRow < 0)
        {
            RefreshReviewViews(review.StableId);
            return;
        }

        int oldRowCount = _mainGridRows.Count(row => row.StableGroupId == review.StableId);
        IReadOnlyList<ReviewGroupRow> replacementRows = CreateMainRows(review).ToArray();
        _mainGridRows.RaiseListChangedEvents = false;
        for (int index = 0; index < oldRowCount; index++)
        {
            _mainGridRows.RemoveAt(firstRow);
        }

        for (int index = 0; index < replacementRows.Count; index++)
        {
            _mainGridRows.Insert(firstRow + index, replacementRows[index]);
        }

        _mainGridRows.RaiseListChangedEvents = true;
        _mainGridRows.ResetBindings();
        SelectReview(review);
        SelectMainRow(review.StableId);
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
            RefreshCleanupEligibility();
            return;
        }

        int currentIndex = _filteredReviews.FindIndex(item => item.StableId == review.StableId);
        lblGrupoNavegacion.Text = $"Grupo {currentIndex + 1} de {_filteredReviews.Count} (visual {review.Group.GroupNumber})";
        lblEstadoRevision.Text = $"Estado: {GetStatusText(review.Status)}. Espacio recuperable del grupo: {FileSizeFormatter.Format(review.Group.RecoverableBytes)}.";

        KeepRecommendation recommendation = GetOrCreateRecommendation(review);
        ApplyRecommendationPreview? recommendationPreview = TryCreateRecommendationPreview(review, recommendation);
        lblRecomendacion.Text = recommendationPreview is null
            ? recommendation.Reason
            : $"{recommendation.Reason}{Environment.NewLine}" +
              $"{recommendationPreview.CandidateFiles.Count} copias se marcarán como candidatas. " +
              $"{recommendationPreview.SkippedFiles.Count} archivos quedarán sin decidir por seguridad.";

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

        RefreshCleanupEligibility();
    }

    private KeepRecommendation GetOrCreateRecommendation(DuplicateGroupReview review)
    {
        if (!_recommendationsByGroupId.TryGetValue(review.StableId, out KeepRecommendation? recommendation))
        {
            recommendation = _recommendationService.GetRecommendation(review);
            _recommendationsByGroupId[review.StableId] = recommendation;
        }

        return recommendation;
    }

    private ApplyRecommendationPreview? TryCreateRecommendationPreview(
        DuplicateGroupReview review,
        KeepRecommendation recommendation)
    {
        try
        {
            return _recommendationApplicationService.CreatePreview(review, recommendation);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private bool CanApplyRecommendation(DuplicateGroupReview? review)
    {
        if (!IsLegacyActionsAllowed || review is null || review.Group.Files.Count < 2 || review.ReviewedWithoutCleanup ||
            _searchCancellationTokenSource is not null || _cleanupOperationInProgress || _batchOperationInProgress || _scanResultsAreObsolete)
        {
            return false;
        }

        if (review.Status is not (DuplicateGroupReviewStatus.Pending or DuplicateGroupReviewStatus.PartiallyReviewed or DuplicateGroupReviewStatus.ReadyForCleanup))
        {
            return false;
        }

        return TryCreateRecommendationPreview(review, GetOrCreateRecommendation(review)) is not null;
    }

    private void RefreshRecommendationActionAvailability()
    {
        bool interactionAllowed = _searchCancellationTokenSource is null && !_cleanupOperationInProgress && !_batchOperationInProgress;
        btnAplicarRecomendacion.Enabled = IsLegacyActionsAllowed && interactionAllowed && CanApplyRecommendation(_selectedReview);
        btnDeshacerRecomendacion.Enabled = IsLegacyActionsAllowed && interactionAllowed && _selectedReview is not null &&
            _recommendationUndoSession.HasSnapshot(_selectedReview.StableId);
    }

    private void dgvDuplicados_SelectionChanged(object? sender, EventArgs e)
    {
        if (!IsLegacyActionsAllowed || _isRefreshingReviewControls || dgvDuplicados.CurrentRow?.DataBoundItem is not ReviewGroupRow row)
        {
            return;
        }

        SelectReview(_filteredReviews.FirstOrDefault(review => review.StableId == row.StableGroupId));
    }

    private void dgvGrupoDetalle_CellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (!IsLegacyActionsAllowed || _isRefreshingReviewControls || e.RowIndex < 0 || e.ColumnIndex != colDecisionDetalle.Index ||
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

        if (result.Accepted)
        {
            _recommendationUndoSession.Invalidate(_selectedReview.StableId);
            InvalidateBatchUndoIfAffected(_selectedReview.StableId);
        }

        MarkReviewChanged();
        RefreshReviewViews(_selectedReview.StableId);
    }

    private void dgvGrupoDetalle_CurrentCellDirtyStateChanged(object? sender, EventArgs e)
    {
        if (IsLegacyActionsAllowed && dgvGrupoDetalle.IsCurrentCellDirty)
        {
            dgvGrupoDetalle.CommitEdit(DataGridViewDataErrorContexts.Commit);
        }
    }

    private void dgvGrupoDetalle_CellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (!IsLegacyActionsAllowed || e.RowIndex < 0 || e.ColumnIndex != colUbicacionDetalle.Index ||
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
        if (!IsLegacyActionsAllowed || _isRefreshingReviewControls || _selectedReview is null || string.Equals(_selectedReview.Notes ?? string.Empty, txtNotas.Text, StringComparison.Ordinal))
        {
            return;
        }

        _reviewService.UpdateNotes(_selectedReview, txtNotas.Text);
        InvalidateBatchUndoIfAffected(_selectedReview.StableId);
        MarkReviewChanged();
        RefreshReviewViews(_selectedReview.StableId);
    }

    private void btnMarcarRevisado_Click(object? sender, EventArgs e)
    {
        if (!IsLegacyActionsAllowed) return;
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
        _recommendationUndoSession.Invalidate(_selectedReview.StableId);
        InvalidateBatchUndoIfAffected(_selectedReview.StableId);
        MarkReviewChanged();
        RefreshReviewViews(_selectedReview.StableId);
    }

    private async void btnAplicarRecomendacion_Click(object? sender, EventArgs e)
    {
        if (!IsLegacyActionsAllowed) return;
        DuplicateGroupReview? review = _selectedReview;
        if (!CanApplyRecommendation(review) || review is null)
        {
            return;
        }

        KeepRecommendation recommendation = GetOrCreateRecommendation(review);
        ApplyRecommendationPreview? preview = TryCreateRecommendationPreview(review, recommendation);
        if (preview is null)
        {
            return;
        }

        using var previewForm = new ApplyRecommendationPreviewForm(preview);
        if (previewForm.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        RecommendationApplicationResult result;
        try
        {
            result = _recommendationApplicationService.ApplyConfirmedPreview(
                review,
                preview,
                _reviewService,
                isConfirmed: true);
        }
        catch (InvalidOperationException exception)
        {
            MessageBox.Show(exception.Message, "No se pudo aplicar la recomendación", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _recommendationUndoSession.Store(result.PreviousState);
        InvalidateBatchUndoIfAffected(review.StableId);
        MarkReviewChanged();
        RefreshAfterRecommendationChange(review);
        await SaveReviewStateAsync(forceOverwriteCorruptState: false, showError: true);
        lblEstado.Text = "Recomendación aplicada al grupo.";
    }

    private async void btnDeshacerRecomendacion_Click(object? sender, EventArgs e)
    {
        if (!IsLegacyActionsAllowed) return;
        if (_selectedReview is not DuplicateGroupReview review ||
            !_recommendationUndoSession.TryGet(review.StableId, out RecommendationApplicationSnapshot? snapshot) ||
            snapshot is null)
        {
            return;
        }

        try
        {
            _recommendationApplicationService.RestoreSnapshot(review, snapshot, _reviewService);
        }
        catch (InvalidOperationException exception)
        {
            MessageBox.Show(exception.Message, "No se pudo deshacer la recomendación", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _recommendationUndoSession.Invalidate(review.StableId);
        MarkReviewChanged();
        RefreshAfterRecommendationChange(review);
        await SaveReviewStateAsync(forceOverwriteCorruptState: false, showError: true);
        lblEstado.Text = "Recomendación deshecha.";
    }

    private async void btnAplicarRecomendacionesSeleccionadas_Click(object? sender, EventArgs e)
    {
        if (!IsLegacyActionsAllowed) return;
        if (_batchSelectedStableIds.Count == 0 || _batchOperationInProgress)
        {
            return;
        }

        if (_batchSelectedStableIds.Count > RecommendationBatchService.MaxRecommendationBatchGroups)
        {
            MessageBox.Show(
                $"Has seleccionado {_batchSelectedStableIds.Count} grupos. El máximo permitido por lote es {RecommendationBatchService.MaxRecommendationBatchGroups}. Reduce la selección antes de continuar.",
                "Límite del lote",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        IReadOnlyDictionary<string, DuplicateGroupReview> reviewsByStableId = GetReviewsByStableId();
        foreach (string stableId in _batchSelectedStableIds.Where(reviewsByStableId.ContainsKey))
        {
            GetOrCreateRecommendation(reviewsByStableId[stableId]);
        }

        RecommendationBatchPreview preview = _recommendationBatchService.CreatePreview(
            _batchSelectedStableIds,
            reviewsByStableId,
            _recommendationsByGroupId,
            _scanResultsAreObsolete);
        using var previewForm = new RecommendationBatchPreviewForm(preview);
        if (previewForm.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        SetBatchUiBusy(true);
        lblEstado.Text = $"Aplicando recomendaciones: 0 de {preview.ApplicableGroupCount}.";
        RecommendationBatchApplyResult result;
        try
        {
            result = _recommendationBatchService.ApplyConfirmedPreview(
                preview,
                GetReviewsByStableId(),
                _recommendationsByGroupId,
                _scanResultsAreObsolete,
                isConfirmed: true,
                progress: (current, total) => lblEstado.Text = $"Aplicando recomendaciones: {current} de {total}.");
            foreach (RecommendationBatchGroupPreview group in preview.ApplicableGroups)
            {
                _recommendationUndoSession.Invalidate(group.GroupStableId);
            }

            MarkReviewChanged();
            if (!await SaveReviewStateAsync(forceOverwriteCorruptState: false, showError: true))
            {
                _recommendationBatchService.RestoreSnapshots(result.UndoSnapshot.Groups, GetReviewsByStableId());
                MarkReviewChanged();
                await SaveReviewStateAsync(forceOverwriteCorruptState: false, showError: true);
                MessageBox.Show("No se pudo aplicar el lote. Las decisiones locales anteriores se han restaurado.", "Lote no aplicado", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _recommendationBatchUndoSession.Store(result.UndoSnapshot);
            _batchSelectedStableIds.ExceptWith(preview.ApplicableGroups.Select(group => group.GroupStableId));
            RefreshReviewViews(_selectedReview?.StableId);
            lblEstado.Text = $"Recomendaciones aplicadas a {preview.ApplicableGroupCount} grupos. {preview.ExcludedGroupCount} grupos fueron excluidos.";
        }
        catch (InvalidOperationException exception)
        {
            MessageBox.Show(exception.Message, "No se pudo aplicar el lote", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception exception)
        {
            MessageBox.Show($"No se pudo aplicar el lote. Las decisiones locales anteriores se han restaurado.{Environment.NewLine}{Environment.NewLine}{exception.Message}", "Lote no aplicado", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBatchUiBusy(false);
        }
    }

    private async void btnDeshacerUltimoLote_Click(object? sender, EventArgs e)
    {
        if (!IsLegacyActionsAllowed) return;
        if (_batchOperationInProgress || !_recommendationBatchUndoSession.TryGet(out RecommendationBatchUndoSnapshot? snapshot) || snapshot is null)
        {
            return;
        }

        SetBatchUiBusy(true);
        RecommendationApplicationSnapshot[] afterBatchStates = snapshot.Groups
            .Where(group => GetReviewsByStableId().TryGetValue(group.GroupStableId, out _))
            .Select(group => _recommendationApplicationService.CaptureSnapshot(GetReviewsByStableId()[group.GroupStableId]))
            .ToArray();
        try
        {
            _recommendationBatchService.RestoreSnapshots(snapshot.Groups, GetReviewsByStableId());
            MarkReviewChanged();
            if (!await SaveReviewStateAsync(forceOverwriteCorruptState: false, showError: true))
            {
                _recommendationBatchService.RestoreSnapshots(afterBatchStates, GetReviewsByStableId());
                MarkReviewChanged();
                await SaveReviewStateAsync(forceOverwriteCorruptState: false, showError: true);
                MessageBox.Show("No se pudo deshacer el lote. Se restauró el estado local posterior al lote.", "Deshacer no completado", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _recommendationBatchUndoSession.Clear();
            RefreshReviewViews(_selectedReview?.StableId);
            lblEstado.Text = "Último lote deshecho.";
        }
        catch (Exception exception)
        {
            MessageBox.Show($"No se pudo deshacer el último lote.{Environment.NewLine}{Environment.NewLine}{exception.Message}", "Deshacer no completado", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBatchUiBusy(false);
        }
    }

    private async void btnEnviarGruposPapelera_Click(object? sender, EventArgs e)
    {
        if (!IsLegacyActionsAllowed) return;
        string[] selectedStableIds = _batchSelectedStableIds.ToArray();
        if (selectedStableIds.Length == 0)
        {
            MessageBox.Show(
                "Primero selecciona uno o varios grupos en la columna Lote.",
                "No hay grupos seleccionados",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        if (_cleanupOperationInProgress || _batchOperationInProgress)
        {
            return;
        }

        if (!_cleanupModeActive || _cleanupDriveService is null)
        {
            MessageBox.Show(
                "Activa el modo limpieza y completa su autorización independiente antes de preparar un lote que modifica Google Drive.",
                "Modo limpieza no activo",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        _cleanupOperationInProgress = true;
        _cleanupCancellationTokenSource = new CancellationTokenSource();
        SetCleanupUiBusy(true);
        CleanupBatchHistoryHandle? history = null;

        try
        {
            lblEstado.Text = $"Grupos seleccionados para el lote: {selectedStableIds.Length:N0}. Ejecutando el preflight global...";
            var preflightProgress = new Progress<string>(status => lblEstado.Text = status);
            CleanupBatchPreview preview = await _cleanupBatchService.CreatePreviewAsync(
                selectedStableIds,
                GetReviewsByStableId(),
                _cleanupDriveService,
                _scanResultsAreObsolete,
                preflightProgress,
                _cleanupCancellationTokenSource.Token);

            if (preview.LimitsExceeded)
            {
                MessageBox.Show(preview.LimitMessage, "Límite del lote", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            lblEstado.Text = "Creando el historial obligatorio antes de solicitar confirmación...";
            history = await _cleanupHistoryService.CreateBatchPendingAsync(preview, _cleanupCancellationTokenSource.Token);
            using var previewForm = new CleanupBatchPreviewForm(preview);
            if (previewForm.ShowDialog(this) != DialogResult.OK)
            {
                history.Record.Status = CleanupBatchStatus.CancelledBeforeChanges;
                history.Record.FinishedAtUtc = DateTimeOffset.UtcNow;
                await _cleanupHistoryService.SaveAsync(history, CancellationToken.None);
                lblEstado.Text = "La limpieza por lotes se canceló antes de modificar Google Drive.";
                return;
            }

            if (MessageBox.Show(
                    $"Se enviarán a la papelera {preview.CandidateFileCount:N0} archivos de {preview.ApplicableGroupCount:N0} grupos, con un tamaño total de {FileSizeFormatter.Format(preview.CandidateBytes)}.{Environment.NewLine}{Environment.NewLine}" +
                    "Los archivos no se eliminarán permanentemente. ¿Deseas continuar?",
                    "Última confirmación",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                history.Record.Status = CleanupBatchStatus.CancelledBeforeChanges;
                history.Record.FinishedAtUtc = DateTimeOffset.UtcNow;
                await _cleanupHistoryService.SaveAsync(history, CancellationToken.None);
                lblEstado.Text = "La limpieza por lotes se canceló antes de modificar Google Drive.";
                return;
            }

            lblEstado.Text = "Revalidando el lote antes de enviar archivos a la papelera...";
            using var progressForm = new CleanupBatchProgressForm();
            var progress = new Progress<CleanupBatchProgress>(item =>
            {
                progressForm.Report(item);
                lblEstado.Text = item.Status;
            });
            progressForm.Show(this);
            await Task.Yield();

            // A partir de este punto, el botón de la ventana de progreso solicita parar entre archivos.
            btnCancelar.Enabled = false;
            CleanupBatchResult result = await _cleanupBatchService.ExecuteAsync(
                preview,
                GetReviewsByStableId(),
                _cleanupDriveService,
                _scanResultsAreObsolete,
                _cleanupHistoryService,
                history,
                () => progressForm.StopRequested,
                progress,
                _cleanupCancellationTokenSource.Token);
            progressForm.Complete(result.Status is CleanupBatchStatus.Completed or CleanupBatchStatus.CompletedWithExclusions
                ? "Proceso completado"
                : result.Status is CleanupBatchStatus.CancelledBeforeChanges or CleanupBatchStatus.CancelledAfterChanges
                    ? "Proceso detenido"
                    : "Proceso detenido por error");
            progressForm.Hide();

            if (result.DriveChanged)
            {
                InvalidateResultsAfterDriveChange();
            }

            ShowCleanupBatchOutcome(result, history.Path);
        }
        catch (OperationCanceledException)
        {
            lblEstado.Text = "La limpieza por lotes se canceló antes de modificar Google Drive.";
        }
        catch (Exception exception)
        {
            lblEstado.Text = "La limpieza por lotes se detuvo por un error. No se continuará con otros grupos.";
            MessageBox.Show(
                $"No se pudo completar la preparación del lote.{Environment.NewLine}{Environment.NewLine}{exception.Message}",
                "Limpieza por lotes",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _cleanupCancellationTokenSource?.Dispose();
            _cleanupCancellationTokenSource = null;
            _cleanupOperationInProgress = false;
            SetCleanupUiBusy(false);
            DeactivateCleanupMode(showStatus: false);
            RefreshCleanupEligibility();
        }
    }

    private void InvalidateResultsAfterDriveChange()
    {
        _scanResultsAreObsolete = true;
        btnAbrirPapelera.Enabled = true;
        _batchSelectedStableIds.Clear();
        _recommendationUndoSession.Clear();
        _recommendationBatchUndoSession.Clear();
        _selectedReview = null;
        _filteredReviews = [];
        _mainGridRows.Clear();
        dgvGrupoDetalle.DataSource = null;
        txtNotas.Text = string.Empty;
        lblGrupoNavegacion.Text = "Google Drive ha cambiado. Los resultados anteriores ya no son válidos.";
        lblEstadoRevision.Text = "Pulsa Buscar duplicados para actualizar la información.";
        lblRecomendacion.Text = string.Empty;
        RefreshRecommendationActionAvailability();
        RefreshBatchSelectionUi();
    }

    private void ShowCleanupBatchOutcome(CleanupBatchResult result, string historyPath)
    {
        bool mustRescan = result.DriveChanged;
        string message = $"Estado: {DescribeBatchStatus(result.Status)}.{Environment.NewLine}{Environment.NewLine}" +
                         $"Grupos completados: {result.CompletedGroupCount:N0}{Environment.NewLine}" +
                         $"Grupos parcialmente procesados: {result.PartiallyProcessedGroupCount:N0}{Environment.NewLine}" +
                         $"Grupos no procesados: {result.UnprocessedGroupCount:N0}{Environment.NewLine}" +
                         $"Archivos enviados a la papelera: {result.TrashedFileCount:N0}{Environment.NewLine}" +
                         $"Archivos fallidos: {result.FailedFileCount:N0}{Environment.NewLine}" +
                         $"Espacio enviado a la papelera: {FileSizeFormatter.Format(result.TrashedBytes)}{Environment.NewLine}" +
                         $"Historial: {historyPath}" +
                         (result.Failure is null ? string.Empty : $"{Environment.NewLine}{Environment.NewLine}{result.Failure.Message}") +
                         (mustRescan ? $"{Environment.NewLine}{Environment.NewLine}Google Drive ha cambiado. Ejecuta un nuevo análisis antes de continuar." : string.Empty);
        lblEstado.Text = mustRescan
            ? "Google Drive ha cambiado. Ejecuta un nuevo análisis antes de continuar."
            : "No se modificó ningún archivo. Puedes corregir el problema y volver a intentar.";
        MessageBox.Show(message, "Resultado de limpieza por lotes", MessageBoxButtons.OK,
            result.Status is CleanupBatchStatus.Completed or CleanupBatchStatus.CompletedWithExclusions ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private static string DescribeBatchStatus(CleanupBatchStatus status) => status switch
    {
        CleanupBatchStatus.Completed => "Limpieza por lotes completada",
        CleanupBatchStatus.CompletedWithExclusions => "Limpieza por lotes completada con grupos excluidos",
        CleanupBatchStatus.CancelledBeforeChanges => "Limpieza por lotes cancelada antes de cambios",
        CleanupBatchStatus.CancelledAfterChanges => "Limpieza por lotes cancelada después de cambios",
        CleanupBatchStatus.FailedBeforeChanges => "Limpieza por lotes detenida antes de cambios",
        CleanupBatchStatus.FailedAfterChanges => "Limpieza por lotes detenida después de cambios",
        _ => "Limpieza por lotes pendiente"
    };

    private IReadOnlyDictionary<string, DuplicateGroupReview> GetReviewsByStableId() =>
        _reviews.ToDictionary(review => review.StableId, StringComparer.Ordinal);

    private void InvalidateBatchUndoIfAffected(string? stableId)
    {
        if (_recommendationBatchUndoSession.InvalidateIfIncludes(stableId))
        {
            lblEstado.Text = "El último lote ya no puede deshacerse porque uno o varios grupos fueron modificados posteriormente.";
        }
    }

    private void SetBatchUiBusy(bool isBusy)
    {
        _batchOperationInProgress = isBusy;
        bool canReviewResults = !isBusy && !_scanResultsAreObsolete;
        dgvDuplicados.ReadOnly = !canReviewResults;
        dgvGrupoDetalle.ReadOnly = !canReviewResults || _cleanupOperationInProgress;
        txtNotas.ReadOnly = !canReviewResults || _cleanupOperationInProgress;
        btnAnteriorGrupo.Enabled = canReviewResults;
        btnSiguienteGrupo.Enabled = canReviewResults;
        btnSiguientePendiente.Enabled = canReviewResults;
        btnSiguienteListo.Enabled = canReviewResults;
        btnMarcarRevisado.Enabled = canReviewResults;
        btnGuardarRevision.Enabled = canReviewResults;
        btnVerPlan.Enabled = canReviewResults;
        btnExportarPlan.Enabled = canReviewResults;
        RefreshRecommendationActionAvailability();
        RefreshBatchSelectionUi();
    }

    private async void btnGuardarRevision_Click(object? sender, EventArgs e)
    {
        if (!IsLegacyActionsAllowed) return;
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
        RefreshCleanupEligibility();
    }

    private void btnVerPlan_Click(object? sender, EventArgs e)
    {
        if (!IsLegacyActionsAllowed) return;
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
        if (!IsLegacyActionsAllowed) return;
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

    private async void btnActivarLimpieza_Click(object? sender, EventArgs e)
    {
        if (!IsLegacyActionsAllowed || _cleanupOperationInProgress || _cleanupModeActive)
        {
            return;
        }

        using var information = new CleanupModeActivationForm();
        if (information.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        btnActivarLimpieza.Enabled = false;
        lblEstado.Text = "Solicitando la autorización independiente del modo limpieza...";
        DriveService? newCleanupDriveService = null;
        try
        {
            var authService = new GoogleDriveCleanupAuthService();
            newCleanupDriveService = await authService.ConnectAsync();
            _cleanupDriveService?.Dispose();
            _cleanupDriveService = newCleanupDriveService;
            newCleanupDriveService = null;
            _cleanupModeActive = true;
            lblEstado.Text = "Modo limpieza activo.";
            RefreshCleanupEligibility();
        }
        catch (CleanupAuthorizationScopeException)
        {
            lblEstado.Text = "Google no concedió el permiso completo necesario para el modo limpieza. Restablece la autorización y vuelve a intentarlo.";
            MessageBox.Show(
                lblEstado.Text,
                "Modo limpieza",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        catch (OperationCanceledException)
        {
            lblEstado.Text = "La autorización del modo limpieza fue cancelada. La aplicación sigue en modo de solo lectura.";
        }
        catch (Exception)
        {
            lblEstado.Text = "No se pudo activar el modo limpieza. La aplicación sigue en modo de solo lectura.";
            MessageBox.Show(
                "No se pudo completar la autorización independiente del modo limpieza. No se modificó Google Drive.",
                "Modo limpieza",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            newCleanupDriveService?.Dispose();
            RefreshCleanupEligibility();
        }
    }

    private void btnDesactivarLimpieza_Click(object? sender, EventArgs e)
    {
        if (!IsLegacyActionsAllowed) return;
        if (_cleanupOperationInProgress)
        {
            return;
        }

        DeactivateCleanupMode(showStatus: true);
    }

    private void btnRestablecerAutorizacionLimpieza_Click(object? sender, EventArgs e)
    {
        if (!IsLegacyActionsAllowed) return;
        if (_cleanupModeActive || _cleanupOperationInProgress)
        {
            MessageBox.Show(
                "Desactiva el modo limpieza antes de restablecer su autorización.",
                "Restablecer autorización de limpieza",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        if (MessageBox.Show(
                "Se eliminará únicamente la autorización local del modo limpieza. La próxima activación solicitará permiso para administrar los archivos de Google Drive. La aplicación solo utilizará ese permiso para enviar a la papelera los candidatos que confirmes. ¿Continuar?",
                "Restablecer autorización de limpieza",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        try
        {
            new GoogleDriveCleanupAuthService().ResetStoredAuthorization();
            lblEstado.Text = "La próxima activación solicitará permiso para administrar los archivos de Google Drive. La aplicación solo utilizará ese permiso para enviar a la papelera los candidatos que confirmes.";
            MessageBox.Show(lblEstado.Text, "Restablecer autorización de limpieza", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception)
        {
            MessageBox.Show(
                "No se pudo restablecer la autorización local del modo limpieza.",
                "Restablecer autorización de limpieza",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private async void btnEnviarGrupoPapelera_Click(object? sender, EventArgs e)
    {
        if (!IsLegacyActionsAllowed) return;
        DuplicateGroupReview? review = GetCurrentSelectedReview();
        if (review is null || _cleanupDriveService is null || !_cleanupModeActive || _cleanupOperationInProgress)
        {
            return;
        }

        CleanupEligibilityResult eligibility = GetCleanupEligibility();
        if (!eligibility.IsAllowed)
        {
            MessageBox.Show(
                string.Join(Environment.NewLine, eligibility.BlockingReasons),
                "Grupo bloqueado para limpieza",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            RefreshCleanupEligibility();
            return;
        }

        InvalidateBatchUndoIfAffected(review.StableId);
        _cleanupOperationInProgress = true;
        _cleanupCancellationTokenSource = new CancellationTokenSource();
        SetCleanupUiBusy(true);
        CleanupHistoryHandle? history = null;

        try
        {
            lblEstado.Text = "Creando el historial obligatorio antes de comprobar el grupo...";
            history = await _cleanupHistoryService.CreatePendingAsync(review, _cleanupCancellationTokenSource.Token);

            var preflightProgress = new Progress<string>(status => lblEstado.Text = status);
            CleanupPreflightResult preflight = await _trashService.PreflightAsync(
                _cleanupDriveService,
                review,
                _scanResultsAreObsolete,
                preflightProgress,
                _cleanupCancellationTokenSource.Token);

            history.Record.Validations.Clear();
            history.Record.Validations.AddRange(preflight.ValidationMessages);
            history.Record.KeepFile = preflight.KeepFile ?? history.Record.KeepFile;
            history.Record.CandidateFiles.Clear();
            history.Record.CandidateFiles.AddRange(preflight.CandidateFiles);
            if (!preflight.IsSuccessful)
            {
                history.Record.Status = preflight.WasCancelled
                    ? CleanupOperationStatus.Cancelled
                    : CleanupOperationStatus.PreflightFailed;
                history.Record.FinishedAtUtc = DateTimeOffset.UtcNow;
                history.Record.SanitizedErrorMessage = preflight.WasCancelled
                    ? "El preflight fue cancelado antes de modificar Google Drive."
                    : "El preflight bloqueó el grupo. Debes repetir el análisis o revisar las decisiones.";
                await _cleanupHistoryService.SaveAsync(history, CancellationToken.None);
                MessageBox.Show(
                    string.Join(Environment.NewLine, preflight.ValidationMessages),
                    preflight.WasCancelled ? "Preflight cancelado" : "Preflight bloqueado",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            await _cleanupHistoryService.SaveAsync(history, _cleanupCancellationTokenSource.Token);
            using var confirmation = new CleanupConfirmationForm(preflight);
            if (confirmation.ShowDialog(this) != DialogResult.OK)
            {
                history.Record.Status = CleanupOperationStatus.Cancelled;
                history.Record.FinishedAtUtc = DateTimeOffset.UtcNow;
                history.Record.SanitizedErrorMessage = "El usuario canceló la confirmación final antes de modificar Google Drive.";
                await _cleanupHistoryService.SaveAsync(history, CancellationToken.None);
                lblEstado.Text = "Operación de limpieza cancelada antes de enviar archivos a la papelera.";
                return;
            }

            int candidateCount = preflight.CandidateFiles.Count;
            if (MessageBox.Show(
                    $"Se enviarán {candidateCount:N0} archivos a la papelera y se conservará 1 copia.",
                    "Última confirmación",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                history.Record.Status = CleanupOperationStatus.Cancelled;
                history.Record.FinishedAtUtc = DateTimeOffset.UtcNow;
                history.Record.SanitizedErrorMessage = "El usuario rechazó la última confirmación antes de modificar Google Drive.";
                await _cleanupHistoryService.SaveAsync(history, CancellationToken.None);
                lblEstado.Text = "Operación de limpieza cancelada antes de enviar archivos a la papelera.";
                return;
            }

            var operationProgress = new Progress<(int Current, int Total, string Name)>(progress =>
            {
                lblEstado.Text = $"Archivo {progress.Current:N0} de {progress.Total:N0}: {progress.Name}";
            });
            CleanupOperationResult operation = await _trashService.ExecuteAsync(
                _cleanupDriveService,
                preflight,
                _cleanupHistoryService,
                history,
                operationProgress,
                _cleanupCancellationTokenSource.Token);

            if (operation.ShouldInvalidateScan)
            {
                InvalidateResultsAfterDriveChange();
            }

            ShowCleanupOutcome(operation);
        }
        catch (OperationCanceledException)
        {
            if (history is not null)
            {
                history.Record.Status = CleanupOperationStatus.Cancelled;
                history.Record.FinishedAtUtc = DateTimeOffset.UtcNow;
                history.Record.SanitizedErrorMessage = "La operación fue cancelada antes de completar el preflight.";
                await _cleanupHistoryService.SaveAsync(history, CancellationToken.None);
            }

            lblEstado.Text = "Operación de limpieza cancelada. No se continuó con otros archivos.";
        }
        catch (Exception)
        {
            if (history is not null)
            {
                history.Record.Status = history.Record.ProcessedFiles > 0
                    ? CleanupOperationStatus.Partial
                    : CleanupOperationStatus.Failed;
                history.Record.FinishedAtUtc = DateTimeOffset.UtcNow;
                history.Record.SanitizedErrorMessage = "La operación falló. Revisa el historial local y, si procede, la papelera.";
                await _cleanupHistoryService.SaveAsync(history, CancellationToken.None);
            }

            lblEstado.Text = "La operación de limpieza falló. No se continuó con otros archivos.";
            MessageBox.Show(
                "La operación de limpieza no pudo completarse. Consulta el historial local y repite el análisis antes de continuar.",
                "Limpieza incompleta",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _cleanupCancellationTokenSource?.Dispose();
            _cleanupCancellationTokenSource = null;
            _cleanupOperationInProgress = false;
            SetCleanupUiBusy(false);
            DeactivateCleanupMode(showStatus: false);
            RefreshCleanupEligibility();
        }
    }

    private void btnAbrirPapelera_Click(object? sender, EventArgs e)
    {
        OpenLocation("https://drive.google.com/drive/trash");
    }

    private void DeactivateCleanupMode(bool showStatus)
    {
        _cleanupDriveService?.Dispose();
        _cleanupDriveService = null;
        _cleanupModeActive = false;
        if (showStatus)
        {
            lblEstado.Text = "Modo limpieza desactivado. La conexión de análisis sigue disponible en solo lectura.";
        }

        RefreshCleanupEligibility();
    }

    private DuplicateGroupReview? GetCurrentSelectedReview()
    {
        if (string.IsNullOrWhiteSpace(_selectedReview?.StableId))
        {
            return null;
        }

        return _reviews.FirstOrDefault(review => string.Equals(
            review.StableId,
            _selectedReview.StableId,
            StringComparison.Ordinal));
    }

    private CleanupEligibilityResult GetCleanupEligibility()
    {
        return _trashService.EvaluateEligibility(
            GetCurrentSelectedReview(),
            new CleanupEligibilityContext(
                CleanupModeActive: _cleanupModeActive && _cleanupDriveService is not null,
                SearchCompleted: _lastScanResult is not null,
                ResultsAreObsolete: _scanResultsAreObsolete,
                OperationInProgress: _cleanupOperationInProgress));
    }

    private void RefreshCleanupEligibility()
    {
        if (!IsLegacyActionsAllowed)
        {
            btnActivarLimpieza.Enabled = false;
            btnDesactivarLimpieza.Enabled = false;
            btnRestablecerAutorizacionLimpieza.Enabled = false;
            btnEnviarGrupoPapelera.Enabled = false;
            btnEnviarGruposPapelera.Enabled = false;
            btnAplicarRecomendacion.Enabled = false;
            btnDeshacerRecomendacion.Enabled = false;
            btnAplicarRecomendacionesSeleccionadas.Enabled = false;
            btnDeshacerUltimoLote.Enabled = false;
            btnMarcarRevisado.Enabled = false;
            btnGuardarRevision.Enabled = false;
            btnVerPlan.Enabled = false;
            btnExportarPlan.Enabled = false;
            btnAbrirPapelera.Enabled = false;
            txtNotas.ReadOnly = true;
            dgvGrupoDetalle.ReadOnly = true;
            lblModoLimpieza.Text = "REVISIÓN PAGINADA: decisiones locales de conservación habilitadas; recomendaciones y limpieza deshabilitadas.";
            lblModoLimpieza.BackColor = Color.LemonChiffon;
            lblModoLimpieza.ForeColor = Color.DarkRed;
            lblElegibilidadLimpieza.Text = "La ruta paginada no permite operaciones de limpieza.";
            lblElegibilidadLimpieza.ForeColor = Color.DarkRed;
            RefreshBatchSelectionUi();
            return;
        }

        CleanupEligibilityResult eligibility = GetCleanupEligibility();

        btnActivarLimpieza.Enabled = !_cleanupOperationInProgress && !_cleanupModeActive;
        btnDesactivarLimpieza.Enabled = !_cleanupOperationInProgress && _cleanupModeActive;
        btnRestablecerAutorizacionLimpieza.Enabled = !_cleanupOperationInProgress && !_cleanupModeActive;
        btnEnviarGrupoPapelera.Enabled = eligibility.IsAllowed;
        lblModoLimpieza.Text = _cleanupModeActive
            ? "MODO LIMPIEZA ACTIVO: solo se puede intentar un único grupo que supere todas las validaciones."
            : "Modo limpieza desactivado: el análisis permanece en modo de solo lectura.";
        lblModoLimpieza.BackColor = _cleanupModeActive ? Color.MistyRose : Color.Honeydew;
        lblModoLimpieza.ForeColor = _cleanupModeActive ? Color.DarkRed : Color.DarkGreen;
        lblElegibilidadLimpieza.Text = eligibility.IsAllowed
            ? "El grupo seleccionado está preparado para enviarse a la papelera."
            : $"Limpieza no disponible: {string.Join(Environment.NewLine, eligibility.BlockingReasons)}";
        lblElegibilidadLimpieza.ForeColor = eligibility.IsAllowed ? Color.DarkGreen : Color.DarkGoldenrod;
        RefreshRecommendationActionAvailability();
        RefreshBatchSelectionUi();
    }

    private void SetCleanupUiBusy(bool isBusy)
    {
        btnConectar.Enabled = !isBusy;
        btnBuscar.Enabled = !isBusy && _driveService is not null;
        btnCancelar.Enabled = isBusy;
        if (isBusy)
        {
            btnActivarLimpieza.Enabled = false;
            btnDesactivarLimpieza.Enabled = false;
            btnEnviarGrupoPapelera.Enabled = false;
            btnEnviarGruposPapelera.Enabled = false;
            btnAbrirPapelera.Enabled = false;
            btnRestablecerAutorizacionLimpieza.Enabled = false;
        }
        cmbFiltroEstado.Enabled = !isBusy;
        cmbTamanoMinimo.Enabled = !isBusy;
        cmbOrden.Enabled = !isBusy;
        txtBuscarRevision.Enabled = !isBusy;
        chkSoloCompartidos.Enabled = !isBusy;
        chkMasDeDosCopias.Enabled = !isBusy;
        bool canReviewResults = !isBusy && !_scanResultsAreObsolete;
        dgvDuplicados.ReadOnly = !canReviewResults;
        dgvGrupoDetalle.ReadOnly = !canReviewResults;
        txtNotas.ReadOnly = !canReviewResults;
        btnAnteriorGrupo.Enabled = canReviewResults;
        btnSiguienteGrupo.Enabled = canReviewResults;
        btnSiguientePendiente.Enabled = canReviewResults;
        btnSiguienteListo.Enabled = canReviewResults;
        btnMarcarRevisado.Enabled = canReviewResults;
        btnGuardarRevision.Enabled = canReviewResults;
        btnVerPlan.Enabled = canReviewResults;
        btnExportarPlan.Enabled = canReviewResults;
        btnSeleccionarVisibles.Enabled = !isBusy;
        btnDeseleccionarTodos.Enabled = !isBusy && _batchSelectedStableIds.Count > 0;
        btnAplicarRecomendacionesSeleccionadas.Enabled = !isBusy && _batchSelectedStableIds.Count > 0;
        btnEnviarGruposPapelera.Enabled = !isBusy && _batchSelectedStableIds.Count > 0 && _cleanupModeActive && _cleanupDriveService is not null && !_scanResultsAreObsolete;
        btnDeshacerUltimoLote.Enabled = !isBusy && _recommendationBatchUndoSession.HasSnapshot;
        if (!isBusy)
        {
            RefreshRecommendationActionAvailability();
        }
        else
        {
            btnAplicarRecomendacion.Enabled = false;
            btnDeshacerRecomendacion.Enabled = false;
        }
    }

    private void ShowCleanupOutcome(CleanupOperationResult operation)
    {
        CleanupOperationRecord record = operation.Record;
        switch (record.Status)
        {
            case CleanupOperationStatus.Completed:
                lblEstado.Text = "Los archivos seleccionados se han enviado a la papelera. No se han eliminado permanentemente. Debes repetir la búsqueda antes de realizar otra limpieza.";
                MessageBox.Show(lblEstado.Text, "Limpieza completada", MessageBoxButtons.OK, MessageBoxIcon.Information);
                break;
            case CleanupOperationStatus.Partial:
                lblEstado.Text = "La operación quedó incompleta. Revisa la papelera y repite el análisis antes de continuar.";
                MessageBox.Show($"{lblEstado.Text}{Environment.NewLine}{Environment.NewLine}{BuildCleanupFileDetails(record)}", "Limpieza parcial", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                break;
            case CleanupOperationStatus.Cancelled:
                lblEstado.Text = "La operación fue cancelada. Repite el análisis antes de continuar si se inició algún envío.";
                MessageBox.Show($"{lblEstado.Text}{Environment.NewLine}{Environment.NewLine}{BuildCleanupFileDetails(record)}", "Limpieza cancelada", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                break;
            default:
                lblEstado.Text = operation.NoFilesChanged
                    ? "No se modificó ningún archivo. Puedes corregir el problema y volver a intentarlo."
                    : "La operación de limpieza falló antes de completarse. Repite el análisis antes de continuar.";
                MessageBox.Show($"{lblEstado.Text}{Environment.NewLine}{Environment.NewLine}{BuildCleanupFileDetails(record)}", "Limpieza fallida", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                break;
        }
    }

    private static string BuildCleanupFileDetails(CleanupOperationRecord record)
    {
        string Describe(CleanupFileStatus status, string heading)
        {
            string[] names = record.FileResults
                .Where(result => result.Status == status)
                .Select(result => result.File.Name)
                .ToArray();
            return names.Length == 0 ? string.Empty : $"{heading}: {string.Join(", ", names)}";
        }

        List<string> lines =
        [
            Describe(CleanupFileStatus.Trashed, "Enviados correctamente"),
            Describe(CleanupFileStatus.Failed, "Fallidos"),
            Describe(CleanupFileStatus.Cancelled, "Cancelados"),
            Describe(CleanupFileStatus.NotProcessed, "No procesados")
        ];
        foreach (CleanupFileResult failure in record.FileResults.Where(result => !string.IsNullOrWhiteSpace(result.FailureStage)))
        {
            lines.Add($"No se pudo enviar \"{failure.File.Name}\" a la papelera.");
            lines.Add($"Fase: {failure.FailureStage}");
            if (failure.HttpStatusCode is int statusCode)
            {
                lines.Add($"HTTP: {statusCode} {failure.HttpStatusDescription}");
            }

            if (failure.GoogleErrorCode is int errorCode)
            {
                lines.Add($"Código de error: {errorCode}");
            }

            if (!string.IsNullOrWhiteSpace(failure.FailureReason))
            {
                lines.Add($"Motivo: {failure.FailureReason}");
            }

            if (!string.IsNullOrWhiteSpace(failure.SanitizedTechnicalMessage))
            {
                lines.Add($"Mensaje: {failure.SanitizedTechnicalMessage}");
            }
        }

        string? authorizationRecommendation = GetCleanupAuthorizationRecommendation(record);
        if (!string.IsNullOrWhiteSpace(authorizationRecommendation))
        {
            lines.Add(authorizationRecommendation);
        }

        return string.Join(Environment.NewLine, lines.Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    private static string? GetCleanupAuthorizationRecommendation(CleanupOperationRecord record)
    {
        string details = string.Join(" ", record.FileResults.SelectMany(result =>
            new[]
            {
                result.FailureReason,
                result.SanitizedTechnicalMessage,
                result.Message
            }.Concat(result.ApiErrors.Select(error => $"{error.Reason} {error.Message}"))));

        if (details.Contains("appNotAuthorizedToFile", StringComparison.OrdinalIgnoreCase))
        {
            return "La autorización de limpieza no permite modificar este archivo. Restablece la autorización y asegúrate de conceder el permiso para administrar los archivos de Google Drive.";
        }

        return details.Contains("insufficientPermissions", StringComparison.OrdinalIgnoreCase) ||
               details.Contains("insufficient authentication scopes", StringComparison.OrdinalIgnoreCase) ||
               details.Contains("insufficient_scope", StringComparison.OrdinalIgnoreCase)
            ? "La autorización de limpieza no contiene el permiso necesario. Restablece la autorización y vuelve a autorizarla con permiso para administrar los archivos de Google Drive."
            : null;
    }

    private void btnAnteriorGrupo_Click(object? sender, EventArgs e) => NavigateGroups(-1, null);

    private void btnSiguienteGrupo_Click(object? sender, EventArgs e) => NavigateGroups(1, null);

    private void btnSiguientePendiente_Click(object? sender, EventArgs e) => NavigateGroups(1, DuplicateGroupReviewStatus.Pending);

    private void btnSiguienteListo_Click(object? sender, EventArgs e) => NavigateGroups(1, DuplicateGroupReviewStatus.ReadyForCleanup);

    private void NavigateGroups(int direction, DuplicateGroupReviewStatus? targetStatus)
    {
        if (!IsLegacyActionsAllowed || _filteredReviews.Count == 0)
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

    private async Task<bool> SaveReviewStateAsync(bool forceOverwriteCorruptState, bool showError)
    {
        if (!IsLegacyActionsAllowed || _reviews.Count == 0 || (_reviewStateIsCorrupt && !forceOverwriteCorruptState))
        {
            return false;
        }

        await _reviewSaveSemaphore.WaitAsync();
        int versionAtSave = _reviewChangeVersion;
        bool saved = false;
        try
        {
            await _reviewStateStorageService.SaveAsync(
                _reviews,
                forceOverwriteCorruptState,
                _reviewStateIsCorrupt);
            _reviewStateIsCorrupt = false;
            _reviewChangesPending = _reviewChangeVersion != versionAtSave;
            lblEstadoRevision.Text = $"Revisi\u00F3n local guardada en {_reviewStateStorageService.StatePath}.";
            RefreshCleanupEligibility();
            saved = true;
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

        return saved;
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
        if (!IsLegacyActionsAllowed) return;
        await SaveReviewStateAsync(forceOverwriteCorruptState: false, showError: false);
    }

    private async void Form1_FormClosing(object? sender, FormClosingEventArgs e)
    {
        CancelPagedQueries();
        if (!IsLegacyActionsAllowed) return;
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
        CancelPagedQueries();
        _searchCancellationTokenSource?.Cancel();
        _searchCancellationTokenSource?.Dispose();
        _cleanupCancellationTokenSource?.Cancel();
        _cleanupCancellationTokenSource?.Dispose();
        DeactivateCleanupMode(showStatus: false);
        _driveService?.Dispose();
        _reviewSaveTimer.Stop();
        _reviewSaveTimer.Dispose();
        _reviewSaveSemaphore.Dispose();
        if (_demoDataDirectory is not null) DemoReviewData.TryDelete(_demoDataDirectory);
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
              $"Archivos comparables: {scanResult.ComparableFilesCount:N0}{Environment.NewLine}" +
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

    private bool IsLegacyActionsAllowed => PagedReadOnlyViewSafety.LegacyActionsEnabled(_pagedReadOnlyMode, demoMode: _demoMode);

    private sealed record PagedGroupDisplayRow(
        DuplicateGroupIdentity Identity,
        string RepresentativeName,
        string MemberCountText,
        string SizeText,
        string RecoverableText,
        string Checksum);

    private sealed class PagedMemberDisplayRow(
        string fileId,
        string name,
        string path,
        string sizeText,
        string modifiedText,
        string? parentFolderUrl,
        DuplicateFileDecision decision)
    {
        public string FileId { get; } = fileId;
        public string Name { get; } = name;
        public string Path { get; } = path;
        public string SizeText { get; } = sizeText;
        public string ModifiedText { get; } = modifiedText;
        public string? ParentFolderUrl { get; } = parentFolderUrl;
        public DuplicateFileDecision Decision { get; set; } = decision;
        public string LocationText => string.IsNullOrWhiteSpace(ParentFolderUrl) ? string.Empty : "Abrir ubicación";
    }
}
