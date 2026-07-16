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
        lblDescripcion.Location = new Point(46, 90);
        lblDescripcion.Name = "lblDescripcion";
        lblDescripcion.Size = new Size(500, 44);
        lblDescripcion.TabIndex = 1;
        lblDescripcion.Text = "La aplicaciÃ³n se conectarÃ¡ a Google Drive en modo de solo lectura para comprobar el acceso a los metadatos.";
        // 
        // btnConectar
        // 
        btnConectar.Location = new Point(46, 161);
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
        lblEstado.Location = new Point(46, 223);
        lblEstado.Name = "lblEstado";
        lblEstado.Size = new Size(87, 15);
        lblEstado.TabIndex = 3;
        lblEstado.Text = "Sin conexiÃ³n.";
        // 
        // Form1
        // 
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(600, 300);
        Controls.Add(lblEstado);
        Controls.Add(btnConectar);
        Controls.Add(lblDescripcion);
        Controls.Add(lblTitulo);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        Name = "Form1";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "Drive Duplicate Finder";
        ResumeLayout(false);
        PerformLayout();
    }

    #endregion

    private Label lblTitulo;
    private Label lblDescripcion;
    private Button btnConectar;
    private Label lblEstado;
}
