using DriveDuplicateFinder.Services;
using Google.Apis.Drive.v3;

namespace DriveDuplicateFinder;

public partial class Form1 : Form
{
    public Form1()
    {
        InitializeComponent();
    }

    private async void btnConectar_Click(object? sender, EventArgs e)
    {
        btnConectar.Enabled = false;
        lblEstado.Text = "Conectando con Google Drive...";

        try
        {
            var authService = new GoogleDriveAuthService();

            using DriveService driveService = await authService.ConnectAsync();

            var request = driveService.Files.List();
            request.Q = "trashed = false";
            request.PageSize = 1;
            request.Fields = "files(id),nextPageToken";

            await request.ExecuteAsync();

            lblEstado.Text = "Conectado correctamente con Google Drive.";

            MessageBox.Show(
                "ConexiÃ³n realizada correctamente.",
                "Google Drive",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            lblEstado.Text = "Error al conectar con Google Drive.";

            MessageBox.Show(
                $"No se pudo conectar con Google Drive.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "Error de conexiÃ³n",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            btnConectar.Enabled = true;
        }
    }
}
