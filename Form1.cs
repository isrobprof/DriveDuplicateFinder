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
    private DriveService? _driveService;
    private CancellationTokenSource? _searchCancellationTokenSource;

    public Form1()
    {
        InitializeComponent();
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
        lblResumen.Text = string.Empty;
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

            var rows = duplicateGroups
                .SelectMany(group => group.Files.Select(file => new DuplicateFileRow
                {
                    GroupNumber = group.GroupNumber,
                    Name = file.Name,
                    Path = file.Path,
                    Size = FileSizeFormatter.Format(file.Size),
                    ModifiedTime = file.ModifiedTime?.ToLocalTime().ToString("g") ?? "No disponible",
                    Md5Checksum = file.Md5Checksum,
                    ParentFolderUrl = file.ParentFolderUrl
                }))
                .ToList();

            dgvDuplicados.DataSource = rows;
            lblResumen.Text = BuildSummary(scanResult, duplicateGroups);
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

    private void dgvDuplicados_CellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != colEnlace.Index ||
            dgvDuplicados.Rows[e.RowIndex].DataBoundItem is not DuplicateFileRow row ||
            !Uri.TryCreate(row.ParentFolderUrl, UriKind.Absolute, out Uri? link) ||
            (link.Scheme != Uri.UriSchemeHttp && link.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = link.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"No se pudo abrir la ubicaci\u00F3n en Google Drive.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "Error al abrir la ubicaci\u00F3n",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void dgvDuplicados_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.ColumnIndex == colEnlace.Index && e.Value is string url && !string.IsNullOrWhiteSpace(url))
        {
            e.Value = "Abrir ubicaci\u00F3n";
            e.FormattingApplied = true;
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _searchCancellationTokenSource?.Cancel();
        _searchCancellationTokenSource?.Dispose();
        _driveService?.Dispose();
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

    private static string BuildSummary(
        DriveScanResult scanResult,
        IReadOnlyList<DuplicateGroup> duplicateGroups)
    {
        int duplicateFiles = duplicateGroups.Sum(group => group.Files.Count);
        long recoverableBytes = duplicateGroups.Sum(group => group.RecoverableBytes);

        return $"Elementos examinados: {scanResult.ItemsExamined:N0}{Environment.NewLine}" +
               $"Archivos comparables: {scanResult.ComparableFiles.Count:N0}{Environment.NewLine}" +
               $"Archivos sin MD5 ignorados: {scanResult.FilesWithoutMd5Ignored:N0}{Environment.NewLine}" +
               $"Grupos duplicados: {duplicateGroups.Count:N0}{Environment.NewLine}" +
               $"Archivos duplicados: {duplicateFiles:N0}{Environment.NewLine}" +
               $"Espacio potencialmente recuperable: {FileSizeFormatter.Format(recoverableBytes)}";
    }
}
