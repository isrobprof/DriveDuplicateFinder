namespace DriveDuplicateFinder;

partial class Form1
{
    /// <summary>
    ///  Required designer variable.
    /// </summary>
    private System.ComponentModel.IContainer components = null;

    /// <summary>
    ///  Clean up any resources being used.
    /// </summary>
    /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    /// <summary>
    ///  Required method for Designer support - do not modify
    ///  the contents of this method with the code editor.
    /// </summary>
    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        lblTitulo = new Label();
        lblDescripcion = new Label();
        btnConectar = new Button();
        lblEstado = new Label();
        btnBuscar = new Button();
        btnCancelar = new Button();
        progressBar = new ProgressBar();
        lblResumen = new Label();
        dgvDuplicados = new DataGridView();
        colGrupo = new DataGridViewTextBoxColumn();
        colNombre = new DataGridViewTextBoxColumn();
        colRuta = new DataGridViewTextBoxColumn();
        colTamano = new DataGridViewTextBoxColumn();
        colFechaModificacion = new DataGridViewTextBoxColumn();
        colMd5 = new DataGridViewTextBoxColumn();
        colEnlace = new DataGridViewLinkColumn();
        ((System.ComponentModel.ISupportInitialize)dgvDuplicados).BeginInit();
        SuspendLayout();
        //
        // lblTitulo
        // 
        lblTitulo.AutoSize = true;
        lblTitulo.Font = new Font("Segoe UI", 18F, FontStyle.Bold, GraphicsUnit.Point);
        lblTitulo.Location = new Point(42, 38);
        lblTitulo.Name = "lblTitulo";
        lblTitulo.Size = new Size(258, 32);
        lblTitulo.TabIndex = 0;
        lblTitulo.Text = "Drive Duplicate Finder";
        //
        // lblDescripcion
        // 
        lblDescripcion.Location = new Point(46, 82);
        lblDescripcion.Name = "lblDescripcion";
        lblDescripcion.Size = new Size(830, 25);
        lblDescripcion.TabIndex = 1;
        lblDescripcion.Text = "La aplicaci\u00F3n se conectar\u00E1 a Google Drive en modo de solo lectura para comprobar el acceso a los metadatos.";
        //
        // btnConectar
        // 
        btnConectar.Location = new Point(46, 128);
        btnConectar.Name = "btnConectar";
        btnConectar.Size = new Size(212, 36);
        btnConectar.TabIndex = 2;
        btnConectar.Text = "Conectar con Google Drive";
        btnConectar.UseVisualStyleBackColor = true;
        btnConectar.Click += btnConectar_Click;
        //
        // lblEstado
        //
        lblEstado.AutoSize = true;
        lblEstado.Location = new Point(46, 185);
        lblEstado.Name = "lblEstado";
        lblEstado.Size = new Size(87, 15);
        lblEstado.TabIndex = 3;
        lblEstado.Text = "Sin conexi\u00F3n.";
        //
        // btnBuscar
        //
        btnBuscar.Enabled = false;
        btnBuscar.Location = new Point(274, 128);
        btnBuscar.Name = "btnBuscar";
        btnBuscar.Size = new Size(150, 36);
        btnBuscar.TabIndex = 3;
        btnBuscar.Text = "Buscar duplicados";
        btnBuscar.UseVisualStyleBackColor = true;
        btnBuscar.Click += btnBuscar_Click;
        //
        // btnCancelar
        //
        btnCancelar.Enabled = false;
        btnCancelar.Location = new Point(440, 128);
        btnCancelar.Name = "btnCancelar";
        btnCancelar.Size = new Size(100, 36);
        btnCancelar.TabIndex = 4;
        btnCancelar.Text = "Cancelar";
        btnCancelar.UseVisualStyleBackColor = true;
        btnCancelar.Click += btnCancelar_Click;
        //
        // progressBar
        //
        progressBar.Location = new Point(46, 215);
        progressBar.Name = "progressBar";
        progressBar.Size = new Size(990, 18);
        progressBar.Style = ProgressBarStyle.Continuous;
        progressBar.TabIndex = 5;
        //
        // lblResumen
        //
        lblResumen.BorderStyle = BorderStyle.FixedSingle;
        lblResumen.Location = new Point(46, 252);
        lblResumen.Name = "lblResumen";
        lblResumen.Size = new Size(990, 110);
        lblResumen.TabIndex = 6;
        //
        // dgvDuplicados
        //
        dgvDuplicados.AllowUserToAddRows = false;
        dgvDuplicados.AllowUserToDeleteRows = false;
        dgvDuplicados.AutoGenerateColumns = false;
        dgvDuplicados.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        dgvDuplicados.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        dgvDuplicados.Columns.AddRange(new DataGridViewColumn[] { colGrupo, colNombre, colRuta, colTamano, colFechaModificacion, colMd5, colEnlace });
        dgvDuplicados.EditMode = DataGridViewEditMode.EditProgrammatically;
        dgvDuplicados.Location = new Point(46, 382);
        dgvDuplicados.MultiSelect = false;
        dgvDuplicados.Name = "dgvDuplicados";
        dgvDuplicados.ReadOnly = true;
        dgvDuplicados.RowHeadersVisible = false;
        dgvDuplicados.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        dgvDuplicados.Size = new Size(990, 270);
        dgvDuplicados.TabIndex = 7;
        dgvDuplicados.CellContentClick += dgvDuplicados_CellContentClick;
        dgvDuplicados.CellFormatting += dgvDuplicados_CellFormatting;
        //
        // colGrupo
        //
        colGrupo.DataPropertyName = "GroupNumber";
        colGrupo.FillWeight = 40F;
        colGrupo.HeaderText = "Grupo";
        colGrupo.Name = "colGrupo";
        colGrupo.ReadOnly = true;
        //
        // colNombre
        //
        colNombre.DataPropertyName = "Name";
        colNombre.FillWeight = 110F;
        colNombre.HeaderText = "Nombre";
        colNombre.Name = "colNombre";
        colNombre.ReadOnly = true;
        //
        // colRuta
        //
        colRuta.DataPropertyName = "Path";
        colRuta.FillWeight = 210F;
        colRuta.HeaderText = "Ruta";
        colRuta.Name = "colRuta";
        colRuta.ReadOnly = true;
        //
        // colTamano
        //
        colTamano.DataPropertyName = "Size";
        colTamano.FillWeight = 70F;
        colTamano.HeaderText = "Tama\u00F1o";
        colTamano.Name = "colTamano";
        colTamano.ReadOnly = true;
        //
        // colFechaModificacion
        //
        colFechaModificacion.DataPropertyName = "ModifiedTime";
        colFechaModificacion.FillWeight = 100F;
        colFechaModificacion.HeaderText = "Modificaci\u00F3n";
        colFechaModificacion.Name = "colFechaModificacion";
        colFechaModificacion.ReadOnly = true;
        //
        // colMd5
        //
        colMd5.DataPropertyName = "Md5Checksum";
        colMd5.FillWeight = 145F;
        colMd5.HeaderText = "MD5";
        colMd5.Name = "colMd5";
        colMd5.ReadOnly = true;
        //
        // colEnlace
        //
        colEnlace.DataPropertyName = "ParentFolderUrl";
        colEnlace.FillWeight = 55F;
        colEnlace.HeaderText = "Ubicaci\u00F3n";
        colEnlace.Name = "colEnlace";
        colEnlace.ReadOnly = true;
        //
        // Form1
        //
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1080, 690);
        Controls.Add(lblEstado);
        Controls.Add(dgvDuplicados);
        Controls.Add(lblResumen);
        Controls.Add(progressBar);
        Controls.Add(btnCancelar);
        Controls.Add(btnBuscar);
        Controls.Add(btnConectar);
        Controls.Add(lblDescripcion);
        Controls.Add(lblTitulo);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        Name = "Form1";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "Drive Duplicate Finder";
        ((System.ComponentModel.ISupportInitialize)dgvDuplicados).EndInit();
        ResumeLayout(false);
        PerformLayout();
    }

    #endregion

    private Label lblTitulo;
    private Label lblDescripcion;
    private Button btnConectar;
    private Label lblEstado;
    private Button btnBuscar;
    private Button btnCancelar;
    private ProgressBar progressBar;
    private Label lblResumen;
    private DataGridView dgvDuplicados;
    private DataGridViewTextBoxColumn colGrupo;
    private DataGridViewTextBoxColumn colNombre;
    private DataGridViewTextBoxColumn colRuta;
    private DataGridViewTextBoxColumn colTamano;
    private DataGridViewTextBoxColumn colFechaModificacion;
    private DataGridViewTextBoxColumn colMd5;
    private DataGridViewLinkColumn colEnlace;
}
